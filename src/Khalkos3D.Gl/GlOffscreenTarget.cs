namespace Khalkos3D.Gl;

/// <summary>
/// A framebuffer to render into when there is no window — and the thing that makes this renderer
/// testable at all.
///
/// <para>Two real uses beyond tests: generating a thumbnail on a server that has no display, and
/// rendering into a texture that some other UI toolkit then composites. Both are
/// ordinary, and neither is served by a renderer that can only draw to a window.</para>
///
/// <para>Still creates no CONTEXT — that remains the host's job. This only allocates a colour texture
/// and a depth buffer on a context that already exists.</para>
/// </summary>
public sealed unsafe class GlOffscreenTarget : IDisposable
{
    private readonly GlApi _gl;
    private uint _framebuffer, _colour, _depth;
    private bool _disposed;

    private GlOffscreenTarget(GlApi gl, uint framebuffer, uint colour, uint depth, int width, int height)
    {
        _gl = gl;
        _framebuffer = framebuffer;
        _colour = colour;
        _depth = depth;
        Width = width;
        Height = height;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The colour texture, for a caller that wants to draw this result somewhere else
    /// without a round trip through the CPU.</summary>
    public uint ColorTexture => _colour;

    /// <summary>The framebuffer name.</summary>
    public uint Framebuffer => _framebuffer;

    /// <summary>
    /// Allocate a target on the current context.
    /// </summary>
    /// <param name="getProcAddress">Resolves GL entry points, as for <see cref="GlRenderer.Create"/>.</param>
    /// <param name="width">Pixels across.</param>
    /// <param name="height">Pixels down.</param>
    /// <param name="error">Why it could not be made, when this returns null.</param>
    public static GlOffscreenTarget? Create(Func<string, nint> getProcAddress, int width, int height,
                                            out string? error)
    {
        error = null;
        if (width <= 0 || height <= 0) { error = $"{width}x{height} is not a usable size"; return null; }

        var gl = GlApi.Load(getProcAddress, out var missing);
        if (gl is null)
        {
            error = $"{missing.Count} GL entry points are missing: {string.Join(", ", missing)}";
            return null;
        }
        return Create(gl, width, height, out error);
    }

    internal static GlOffscreenTarget? Create(GlApi gl, int width, int height, out string? error)
    {
        error = null;
        uint framebuffer, colour, depth;

        gl.GenFramebuffers(1, &framebuffer);
        gl.BindFramebuffer(GlApi.FRAMEBUFFER, framebuffer);

        gl.GenTextures(1, &colour);
        gl.BindTexture(GlApi.TEXTURE_2D, colour);
        gl.TexImage2D(GlApi.TEXTURE_2D, 0, (int)GlApi.RGBA8, width, height, 0,
                      GlApi.RGBA, GlApi.UNSIGNED_BYTE, null);
        gl.TexParameteri(GlApi.TEXTURE_2D, GlApi.TEX_MIN_FILTER, GlApi.LINEAR);
        gl.TexParameteri(GlApi.TEXTURE_2D, GlApi.TEX_MAG_FILTER, GlApi.LINEAR);
        gl.FramebufferTexture2D(GlApi.FRAMEBUFFER, GlApi.COLOR_ATTACHMENT0, GlApi.TEXTURE_2D, colour, 0);

        // A depth buffer, and it is not optional. Without one, everything passes the depth test and
        // the model draws in submission order — so the far side of a solid appears in front of the
        // near side, intermittently, depending on how the file happened to be written.
        gl.GenRenderbuffers(1, &depth);
        gl.BindRenderbuffer(GlApi.RENDERBUFFER, depth);
        gl.RenderbufferStorage(GlApi.RENDERBUFFER, GlApi.DEPTH_COMPONENT24, width, height);
        gl.FramebufferRenderbuffer(GlApi.FRAMEBUFFER, GlApi.DEPTH_ATTACHMENT, GlApi.RENDERBUFFER, depth);

        var status = gl.CheckFramebufferStatus(GlApi.FRAMEBUFFER);
        if (status != GlApi.FRAMEBUFFER_COMPLETE)
        {
            error = $"framebuffer incomplete (0x{status:X}) at {width}x{height}";
            gl.BindFramebuffer(GlApi.FRAMEBUFFER, 0);
            gl.DeleteFramebuffers(1, &framebuffer);
            gl.DeleteTextures(1, &colour);
            gl.DeleteRenderbuffers(1, &depth);
            return null;
        }

        gl.BindFramebuffer(GlApi.FRAMEBUFFER, 0);
        return new GlOffscreenTarget(gl, framebuffer, colour, depth, width, height);
    }

    /// <summary>Make this the target of subsequent drawing.</summary>
    public void Bind()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gl.BindFramebuffer(GlApi.FRAMEBUFFER, _framebuffer);
    }

    /// <summary>Go back to the default framebuffer — the window, or whatever the host had bound.</summary>
    public void Unbind() => _gl.BindFramebuffer(GlApi.FRAMEBUFFER, 0);

    /// <summary>
    /// Read the rendered pixels back as straight RGBA8, TOP row first.
    ///
    /// <para>GL's first row is the bottom of the image and every image format's is the top, so the
    /// rows are flipped here. Doing it once, at the only place pixels leave the GPU, is what stops
    /// every caller discovering it separately by looking at an upside-down thumbnail.</para>
    /// </summary>
    public byte[] ReadPixels()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stride = Width * 4;
        var raw = new byte[stride * Height];

        Bind();
        fixed (byte* p = raw)
            _gl.ReadPixels(0, 0, Width, Height, GlApi.RGBA, GlApi.UNSIGNED_BYTE, p);

        var flipped = new byte[raw.Length];
        for (var y = 0; y < Height; y++)
            Array.Copy(raw, (Height - 1 - y) * stride, flipped, y * stride, stride);
        return flipped;
    }

    /// <summary>Release the framebuffer. Must be called with the owning context current.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        uint framebuffer = _framebuffer, colour = _colour, depth = _depth;
        _gl.BindFramebuffer(GlApi.FRAMEBUFFER, 0);
        if (framebuffer != 0) _gl.DeleteFramebuffers(1, &framebuffer);
        if (colour != 0) _gl.DeleteTextures(1, &colour);
        if (depth != 0) _gl.DeleteRenderbuffers(1, &depth);
        _framebuffer = _colour = _depth = 0;
    }
}
