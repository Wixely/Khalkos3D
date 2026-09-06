using System.Runtime.InteropServices;

namespace StannumFab.Gl;

/// <summary>Which GLSL dialect a context compiles. The only host difference an application's shader
/// source cannot ignore — and WebGL2 <b>is</b> OpenGL ES 3.0, so a phone and a browser want the same
/// one and only the desktop differs.</summary>
public enum GlslDialect
{
    /// <summary>Desktop OpenGL 3.3 core — <c>#version 330 core</c>.</summary>
    Gl330Core,
    /// <summary>OpenGL ES 3.0, which is also WebGL2 — <c>#version 300 es</c>.</summary>
    GlEs300,
}

/// <summary>
/// The GL entry points this renderer calls, resolved per context.
///
/// <para><b>Instanced, never static.</b> <c>wglGetProcAddress</c>'s answers are only valid for the
/// context that was current when it was asked, so a static table is a latent bug the moment a process
/// has two — a window and an offscreen target, which is an ordinary thing for an application to
/// want. One of these belongs to one <see cref="GlRenderer"/>.</para>
///
/// <para>Every function here is core in BOTH OpenGL 3.3 and OpenGL ES 3.0, so a driver providing
/// either provides all of them. Anything missing is a real diagnosis — a broken loader, or a context
/// that is not what it claimed — and is reported by name rather than crashed on.</para>
/// </summary>
public sealed unsafe class GlApi
{
    // ---- constants ------------------------------------------------------------------------

    internal const uint COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x0100;
    internal const uint VENDOR = 0x1F00, RENDERER = 0x1F01, VERSION = 0x1F02;
    internal const uint DEPTH_TEST = 0x0B71, LESS = 0x0201, LEQUAL = 0x0203;
    internal const uint BLEND = 0x0BE2, SCISSOR_TEST = 0x0C11, STENCIL_TEST = 0x0B90;
    internal const uint CULL_FACE = 0x0B44, DITHER = 0x0BD0, BACK = 0x0405, CCW = 0x0901;
    internal const uint SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303;
    internal const uint TEXTURE0 = 0x84C0, TEXTURE_2D = 0x0DE1;
    internal const uint MAX_COMBINED_TEXTURE_IMAGE_UNITS = 0x8B4D;
    internal const uint UNPACK_ALIGNMENT = 0x0CF5, PACK_ALIGNMENT = 0x0D05;
    internal const uint RGBA = 0x1908, RGBA8 = 0x8058, UNSIGNED_BYTE = 0x1401, UNSIGNED_INT = 0x1405;
    internal const uint FLOAT = 0x1406, TRIANGLES = 0x0004, LINES = 0x0001, POINTS = 0x0000;
    internal const uint ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STATIC_DRAW = 0x88E4;
    internal const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30;
    internal const uint COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    internal const uint TEX_MIN_FILTER = 0x2801, TEX_MAG_FILTER = 0x2800;
    internal const uint TEX_WRAP_S = 0x2802, TEX_WRAP_T = 0x2803;
    internal const int LINEAR = 0x2601, NEAREST = 0x2600;
    internal const int LINEAR_MIPMAP_LINEAR = 0x2703, NEAREST_MIPMAP_LINEAR = 0x2702;
    internal const int REPEAT = 0x2901, CLAMP_TO_EDGE = 0x812F, MIRRORED_REPEAT = 0x8370;
    internal const uint FRAMEBUFFER = 0x8D40, RENDERBUFFER = 0x8D41;
    internal const uint COLOR_ATTACHMENT0 = 0x8CE0, DEPTH_ATTACHMENT = 0x8D00;
    internal const uint DEPTH_COMPONENT24 = 0x81A6, FRAMEBUFFER_COMPLETE = 0x8CD5;

    // ---- the table ------------------------------------------------------------------------

    internal delegate* unmanaged<uint, byte*> GetString;
    internal delegate* unmanaged<uint> GetError;
    internal delegate* unmanaged<uint, int*, void> GetIntegerv;
    internal delegate* unmanaged<int, int, int, int, void> Viewport;
    internal delegate* unmanaged<float, float, float, float, void> ClearColor;
    internal delegate* unmanaged<uint, void> Clear;
    internal delegate* unmanaged<uint, void> Enable;
    internal delegate* unmanaged<uint, void> Disable;
    internal delegate* unmanaged<uint, void> DepthFunc;
    internal delegate* unmanaged<byte, void> DepthMask;
    internal delegate* unmanaged<byte, byte, byte, byte, void> ColorMask;
    internal delegate* unmanaged<uint, void> CullFace;
    internal delegate* unmanaged<uint, void> FrontFace;
    internal delegate* unmanaged<uint, uint, void> BlendFunc;
    internal delegate* unmanaged<uint, void> ActiveTexture;
    internal delegate* unmanaged<uint, uint, void> BindSampler;
    internal delegate* unmanaged<uint, int, void> PixelStorei;

    internal delegate* unmanaged<uint, uint> CreateShader;
    internal delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    internal delegate* unmanaged<uint, void> CompileShader;
    internal delegate* unmanaged<uint, uint, int*, void> GetShaderiv;
    internal delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog;
    internal delegate* unmanaged<uint, void> DeleteShader;
    internal delegate* unmanaged<uint> CreateProgram;
    internal delegate* unmanaged<uint, uint, void> AttachShader;
    internal delegate* unmanaged<uint, void> LinkProgram;
    internal delegate* unmanaged<uint, uint, int*, void> GetProgramiv;
    internal delegate* unmanaged<uint, int, int*, byte*, void> GetProgramInfoLog;
    internal delegate* unmanaged<uint, void> UseProgram;
    internal delegate* unmanaged<uint, void> DeleteProgram;
    internal delegate* unmanaged<uint, uint, byte*, void> BindAttribLocation;
    internal delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    internal delegate* unmanaged<int, int, byte, float*, void> UniformMatrix4fv;
    internal delegate* unmanaged<int, float, float, float, float, void> Uniform4f;
    internal delegate* unmanaged<int, float, float, float, void> Uniform3f;
    internal delegate* unmanaged<int, float, void> Uniform1f;
    internal delegate* unmanaged<int, int, void> Uniform1i;

    internal delegate* unmanaged<int, uint*, void> GenVertexArrays;
    internal delegate* unmanaged<uint, void> BindVertexArray;
    internal delegate* unmanaged<int, uint*, void> DeleteVertexArrays;
    internal delegate* unmanaged<int, uint*, void> GenBuffers;
    internal delegate* unmanaged<uint, uint, void> BindBuffer;
    internal delegate* unmanaged<uint, nint, void*, uint, void> BufferData;
    internal delegate* unmanaged<int, uint*, void> DeleteBuffers;
    internal delegate* unmanaged<uint, int, uint, byte, int, void*, void> VertexAttribPointer;
    internal delegate* unmanaged<uint, void> EnableVertexAttribArray;
    internal delegate* unmanaged<uint, void> DisableVertexAttribArray;
    internal delegate* unmanaged<uint, int, uint, void*, void> DrawElements;

    internal delegate* unmanaged<int, uint*, void> GenTextures;
    internal delegate* unmanaged<uint, uint, void> BindTexture;
    internal delegate* unmanaged<int, uint*, void> DeleteTextures;
    internal delegate* unmanaged<uint, uint, int, void> TexParameteri;
    internal delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    internal delegate* unmanaged<uint, void> GenerateMipmap;
    internal delegate* unmanaged<int, int, int, int, uint, uint, void*, void> ReadPixels;

    internal delegate* unmanaged<int, uint*, void> GenFramebuffers;
    internal delegate* unmanaged<uint, uint, void> BindFramebuffer;
    internal delegate* unmanaged<int, uint*, void> DeleteFramebuffers;
    internal delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    internal delegate* unmanaged<uint, uint> CheckFramebufferStatus;
    internal delegate* unmanaged<int, uint*, void> GenRenderbuffers;
    internal delegate* unmanaged<uint, uint, void> BindRenderbuffer;
    internal delegate* unmanaged<int, uint*, void> DeleteRenderbuffers;
    internal delegate* unmanaged<uint, uint, int, int, void> RenderbufferStorage;
    internal delegate* unmanaged<uint, uint, uint, uint, void> FramebufferRenderbuffer;

    /// <summary>Texture units this driver reports, so the sampler reset covers all of them rather
    /// than a guess. Clamped, because the loop runs per frame.</summary>
    internal int TextureUnits { get; private set; } = 8;

    /// <summary>What the driver calls itself. The single most useful thing to log when a report says
    /// it looks wrong on someone else's machine — a phone names its chip, an emulator says
    /// "SwiftShader", and the difference explains most surprises.</summary>
    public string Renderer { get; private set; } = "";

    /// <summary>The <c>GL_VERSION</c> string.</summary>
    public string Version { get; private set; } = "";

    /// <summary>The driver vendor.</summary>
    public string Vendor { get; private set; } = "";

    /// <summary>
    /// Which shader dialect to emit — asked of the driver rather than guessed from the platform.
    ///
    /// <para>"OpenGL ES 3.2 v1.r26" and "WebGL 2.0 (OpenGL ES 3.0 Chromium)" both want
    /// <c>#version 300 es</c>; a desktop's "4.6.0 NVIDIA" wants <c>#version 330 core</c>. Deriving it
    /// from the string is right in the cases platform detection would also get right, and right in
    /// the ones it would not — an ES context on a desktop, or desktop GL inside an emulator.</para>
    /// </summary>
    public GlslDialect Dialect { get; private set; }

    private GlApi() { }

    /// <summary>Resolve the table, or say which entry points are missing. Names are tried verbatim
    /// and then with an <c>ARB</c> suffix, since some drivers publish only the extension spelling of
    /// a function that later became core.</summary>
    public static GlApi? Load(Func<string, nint> getProcAddress, out IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        var gl = new GlApi();
        List<string>? absent = null;

        nint P(string name)
        {
            var p = getProcAddress(name);
            if (p == 0) p = getProcAddress(name + "ARB");
            if (p == 0) (absent ??= []).Add(name);
            return p;
        }

        gl.GetString = (delegate* unmanaged<uint, byte*>)P("glGetString");
        gl.GetError = (delegate* unmanaged<uint>)P("glGetError");
        gl.GetIntegerv = (delegate* unmanaged<uint, int*, void>)P("glGetIntegerv");
        gl.Viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        gl.ClearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor");
        gl.Clear = (delegate* unmanaged<uint, void>)P("glClear");
        gl.Enable = (delegate* unmanaged<uint, void>)P("glEnable");
        gl.Disable = (delegate* unmanaged<uint, void>)P("glDisable");
        gl.DepthFunc = (delegate* unmanaged<uint, void>)P("glDepthFunc");
        gl.DepthMask = (delegate* unmanaged<byte, void>)P("glDepthMask");
        gl.ColorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)P("glColorMask");
        gl.CullFace = (delegate* unmanaged<uint, void>)P("glCullFace");
        gl.FrontFace = (delegate* unmanaged<uint, void>)P("glFrontFace");
        gl.BlendFunc = (delegate* unmanaged<uint, uint, void>)P("glBlendFunc");
        gl.ActiveTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture");
        gl.BindSampler = (delegate* unmanaged<uint, uint, void>)P("glBindSampler");
        gl.PixelStorei = (delegate* unmanaged<uint, int, void>)P("glPixelStorei");

        gl.CreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        gl.ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        gl.CompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        gl.GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        gl.GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        gl.DeleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
        gl.CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        gl.AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        gl.LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        gl.GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        gl.GetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetProgramInfoLog");
        gl.UseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        gl.DeleteProgram = (delegate* unmanaged<uint, void>)P("glDeleteProgram");
        gl.BindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)P("glBindAttribLocation");
        gl.GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        gl.UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        gl.Uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f");
        gl.Uniform3f = (delegate* unmanaged<int, float, float, float, void>)P("glUniform3f");
        gl.Uniform1f = (delegate* unmanaged<int, float, void>)P("glUniform1f");
        gl.Uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");

        gl.GenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
        gl.BindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
        gl.DeleteVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glDeleteVertexArrays");
        gl.GenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        gl.BindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
        gl.BufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)P("glBufferData");
        gl.DeleteBuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteBuffers");
        gl.VertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)P("glVertexAttribPointer");
        gl.EnableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray");
        gl.DisableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glDisableVertexAttribArray");
        gl.DrawElements = (delegate* unmanaged<uint, int, uint, void*, void>)P("glDrawElements");

        gl.GenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
        gl.BindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture");
        gl.DeleteTextures = (delegate* unmanaged<int, uint*, void>)P("glDeleteTextures");
        gl.TexParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri");
        gl.TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
        gl.GenerateMipmap = (delegate* unmanaged<uint, void>)P("glGenerateMipmap");
        gl.ReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)P("glReadPixels");

        gl.GenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        gl.BindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
        gl.DeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteFramebuffers");
        gl.FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
        gl.CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
        gl.GenRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glGenRenderbuffers");
        gl.BindRenderbuffer = (delegate* unmanaged<uint, uint, void>)P("glBindRenderbuffer");
        gl.DeleteRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteRenderbuffers");
        gl.RenderbufferStorage = (delegate* unmanaged<uint, uint, int, int, void>)P("glRenderbufferStorage");
        gl.FramebufferRenderbuffer = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glFramebufferRenderbuffer");

        missing = (IReadOnlyList<string>?)absent ?? [];
        if (missing.Count > 0) return null;

        gl.Renderer = gl.Str(RENDERER);
        gl.Version = gl.Str(VERSION);
        gl.Vendor = gl.Str(VENDOR);
        gl.Dialect =
            gl.Version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase) ||
            gl.Version.Contains("WebGL", StringComparison.OrdinalIgnoreCase)
                ? GlslDialect.GlEs300 : GlslDialect.Gl330Core;

        var units = 8;
        gl.GetIntegerv(MAX_COMBINED_TEXTURE_IMAGE_UNITS, &units);
        gl.TextureUnits = units is > 0 and <= 64 ? units : 8;
        return gl;
    }

    internal string Str(uint name) =>
        GetString is null ? "" : Marshal.PtrToStringUTF8((nint)GetString(name)) ?? "";

    /// <summary>
    /// Put the driver into the documented state the renderer assumes. Called before every frame.
    ///
    /// <para><b>Enforced rather than written down, because written down was not enough elsewhere.</b>
    /// The line that matters is the first one: Skia — and any other library sharing this context —
    /// binds SAMPLER OBJECTS, and a bound sampler object overrides <i>every</i> texture parameter on
    /// that unit, wrap mode included. A texture set to <c>GL_REPEAT</c> then clamps, so a tiling UV
    /// samples one edge texel across most of the model and the result looks like a broken unwrap
    /// rather than like state leakage. Nothing errors, and the parameters read back exactly as they
    /// were set. It was invisible on one desktop driver and glaring on a phone.</para>
    /// </summary>
    internal void ResetState()
    {
        for (var unit = 0u; unit < (uint)TextureUnits; unit++) BindSampler(unit, 0);

        Disable(BLEND);
        Disable(SCISSOR_TEST);
        Disable(STENCIL_TEST);
        Disable(DITHER);
        Enable(DEPTH_TEST);
        DepthFunc(LESS);
        DepthMask(1);
        ColorMask(1, 1, 1, 1);
        FrontFace(CCW);
        ActiveTexture(TEXTURE0);
        // Skia sets these to 1 for its own uploads. A row-aligned upload then reads the wrong bytes
        // per row and the texture shears — the classic diagonal smear.
        PixelStorei(UNPACK_ALIGNMENT, 4);
        PixelStorei(PACK_ALIGNMENT, 4);
    }
}
