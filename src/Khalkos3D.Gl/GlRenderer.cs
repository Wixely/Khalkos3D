using System.Numerics;
using System.Text;

namespace Khalkos3D.Gl;

/// <summary>
/// Draws a <see cref="Scene"/> with OpenGL, on every platform this engine targets.
///
/// <code>
/// var renderer = GlRenderer.Create(getProcAddress, out var error);
/// renderer?.Draw(scene, camera, width, height);
/// </code>
///
/// <para><b>It never creates a context, a window or a framebuffer.</b> It is handed a proc-address
/// function and draws into whatever is currently bound. That is what lets one renderer serve a
/// desktop window, an Android <c>SurfaceView</c>, a browser canvas and an offscreen target inside
/// somebody else's UI toolkit — which was a founding requirement rather than a nicety. A renderer
/// that owned its window would need a separate integration per host, which is the duplication this
/// project exists to avoid.</para>
///
/// <para>GPU resources are cached against the <see cref="Mesh"/> and <see cref="ImageData"/> objects
/// themselves, by reference. A plate holding twenty copies of one part is twenty transforms over one
/// upload, because that is what the scene graph already said.</para>
/// </summary>
public sealed unsafe class GlRenderer : IDisposable
{
    private readonly GlApi _gl;
    private readonly GpuProgram _builtin;

    // Programs built from caller-supplied GLSL, kept against the Shader object by reference exactly
    // as meshes are kept against the Mesh. A null value is a shader that would not build: remembering
    // the failure is what stops a broken material being recompiled sixty times a second, and what
    // makes the report a fact about that shader rather than about the last frame.
    private readonly Dictionary<Shader, GpuProgram?> _programs = new(ReferenceEqualityComparer.Instance as IEqualityComparer<Shader> ?? EqualityComparer<Shader>.Default);
    private readonly Dictionary<Shader, ShaderReport> _reports = new(ReferenceEqualityComparer.Instance as IEqualityComparer<Shader> ?? EqualityComparer<Shader>.Default);

    private readonly Dictionary<Mesh, GpuMesh> _meshes = new(ReferenceEqualityComparer.Instance as IEqualityComparer<Mesh> ?? EqualityComparer<Mesh>.Default);
    private readonly Dictionary<TextureRef, uint> _textures = [];
    private uint _whiteTexture;

    // Counts Draw calls, so the per-frame uniforms are set on each program the first time that frame
    // reaches it and not once per draw. A scene with one custom shader and four hundred nodes sets
    // the camera and the lights twice.
    private long _frame;
    private bool _disposed;

    private GlRenderer(GlApi gl, GpuProgram builtin)
    {
        _gl = gl;
        _builtin = builtin;
    }

    /// <summary>What the driver calls itself. Worth logging: it names the hardware, and it is the
    /// first question worth answering when a report says it looks wrong somewhere else.</summary>
    public string Renderer => _gl.Renderer;

    /// <summary>The <c>GL_VERSION</c> string.</summary>
    public string Version => _gl.Version;

    /// <summary>Which shader dialect this context compiles.</summary>
    public GlslDialect Dialect => _gl.Dialect;

    /// <summary>
    /// Build a renderer on the current context.
    /// </summary>
    /// <param name="getProcAddress">Resolves a GL entry point. Supplied by the host — WGL, GLX,
    /// <c>dlsym</c> on <c>libGLESv3.so</c>, or <c>emscripten_GetProcAddress</c>. Keeping it a
    /// delegate rather than a P/Invoke is what lets a browser host that cannot reach the emscripten
    /// symbols directly supply its own resolver without any change here.</param>
    /// <param name="error">Why it could not be built, when this returns null.</param>
    /// <returns>The renderer, or null — never an exception, because a machine with no usable GL must
    /// still run the application around it.</returns>
    public static GlRenderer? Create(Func<string, nint> getProcAddress, out string? error)
    {
        error = null;
        var gl = GlApi.Load(getProcAddress, out var missing);
        if (gl is null)
        {
            error = $"{missing.Count} GL entry points are missing: {string.Join(", ", missing)}";
            return null;
        }

        var program = Build(gl, ShaderSource.Vertex(gl.Dialect), ShaderSource.Fragment(gl.Dialect), out var built);
        if (program == 0) { error = built; return null; }

        return new GlRenderer(gl, new GpuProgram(gl, program));
    }

    /// <summary>
    /// Compile and link one program from finished source, or say why not.
    ///
    /// <para>The engine's own program and a caller's go through here identically, which is the point:
    /// a custom shader gets the same attribute bindings, the same log handling and the same refusal
    /// to throw. Returns 0 and sets <paramref name="error"/> on failure.</para>
    /// </summary>
    private static uint Build(GlApi gl, string vertexSource, string fragmentSource, out string? error)
    {
        var vertex = Compile(gl, GlApi.VERTEX_SHADER, vertexSource, out var vertexError);
        if (vertex == 0) { error = "vertex shader: " + vertexError; return 0; }

        var fragment = Compile(gl, GlApi.FRAGMENT_SHADER, fragmentSource, out var fragmentError);
        if (fragment == 0) { gl.DeleteShader(vertex); error = "fragment shader: " + fragmentError; return 0; }

        var program = gl.CreateProgram();
        gl.AttachShader(program, vertex);
        gl.AttachShader(program, fragment);
        // Bound before linking rather than queried after: the locations are then a fact of this
        // source rather than something the driver chose, so a mesh missing a channel just leaves that
        // slot disabled and no lookup can disagree. Every attribute is bound, including aTangent —
        // leaving one to the linker means it lands where that driver felt like putting it, which is
        // location 4 on most of them and a silently wrong normal map on the rest.
        foreach (var (location, name) in ShaderSource.Attributes) BindAttribute(gl, program, location, name);
        gl.LinkProgram(program);

        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);

        int linked;
        gl.GetProgramiv(program, GlApi.LINK_STATUS, &linked);
        if (linked == 0)
        {
            var log = stackalloc byte[2048];
            gl.GetProgramInfoLog(program, 2048, null, log);
            gl.DeleteProgram(program);
            error = "link: " + System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)log);
            return 0;
        }

        error = null;
        return program;
    }

    /// <summary>
    /// Draw a scene into the currently bound framebuffer.
    /// </summary>
    /// <param name="scene">What to draw. Uploaded on first sight and cached thereafter.</param>
    /// <param name="camera">Where from.</param>
    /// <param name="width">Viewport width in pixels — DEVICE pixels, not logical ones.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <param name="settings">Lighting and debug switches.</param>
    public void Draw(Scene scene, in Camera camera, int width, int height, RenderSettings? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0) return;
        // Nullable rather than `= default`, because a defaulted STRUCT here would arrive with a black
        // light and zero ambient - a completely black frame from a call that looks like it asked for
        // nothing in particular.
        var settings = options ?? RenderSettings.Default;

        var gl = _gl;
        gl.ResetState();
        gl.Viewport(0, 0, width, height);

        if (settings.ClearColor is { } clear)
        {
            gl.ClearColor(clear.X, clear.Y, clear.Z, clear.W);
            gl.Clear(GlApi.COLOR_BUFFER_BIT | GlApi.DEPTH_BUFFER_BIT);
        }

        // Which program each draw uses is now a property of its material, so the camera, the lights
        // and the debug switches are pushed to a program the first time this frame reaches it rather
        // than once here. A scene with no custom shaders in it does that exactly once, as before.
        _frame++;
        var viewProjection = camera.ViewProjection((float)width / height);

        // Opaque first, then transparent back to front. Sorting only the transparent half is the
        // whole reason AlphaMode exists as a distinction: sorting everything would cost a pass over
        // the scene per frame for models that never need it, which is most of them.
        List<(Mesh Mesh, Matrix4x4 World, Material Material, float Depth)>? transparent = null;

        foreach (var (mesh, world, material) in scene.Draws())
        {
            if (material.IsTransparent && !settings.ShowNormals)
            {
                var centre = Vector3.Transform(mesh.Bounds.Center, world);
                (transparent ??= []).Add((mesh, world, material, Vector3.DistanceSquared(centre, camera.Position)));
                continue;
            }
            DrawOne(mesh, world, material, TextureFor(scene, material),
                    NormalMapFor(scene, material), viewProjection, camera, settings);
        }

        if (transparent is null) return;

        transparent.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        gl.Enable(GlApi.BLEND);
        gl.BlendFunc(GlApi.SRC_ALPHA, GlApi.ONE_MINUS_SRC_ALPHA);
        // Depth still TESTED but no longer WRITTEN: a transparent surface must be hidden by opaque
        // geometry in front of it, and must not hide the transparent surfaces behind it.
        gl.DepthMask(0);
        foreach (var (mesh, world, material, _) in transparent)
            DrawOne(mesh, world, material, TextureFor(scene, material),
                    NormalMapFor(scene, material), viewProjection, camera, settings);
        gl.DepthMask(1);
        gl.Disable(GlApi.BLEND);
    }

    private void DrawOne(Mesh mesh, Matrix4x4 world, Material material, uint texture, uint normalMap,
                         Matrix4x4 viewProjection, in Camera camera, RenderSettings settings)
    {
        var gl = _gl;
        var gpu = Upload(mesh);
        if (gpu.Indices == 0) return;

        // The material chooses the program: its own shader if it has one that builds, and the
        // built-in metallic-roughness program otherwise. A shader that failed to compile draws as
        // though it were not there, which keeps the geometry on screen while the report says why it
        // is the wrong colour.
        var program = ProgramFor(material);
        var u = program.U;
        gl.UseProgram(program.Handle);
        if (program.Frame != _frame)
        {
            ApplyFrame(program, camera, settings);
            program.Frame = _frame;
        }

        var mvp = world * viewProjection;
        SetMatrix(u.Mvp, mvp);
        SetMatrix(u.Model, world);

        // The inverse transpose, which equals the model matrix only for a rigid motion. A node
        // carrying a non-uniform scale — a stretched copy on a plate — would otherwise have every
        // normal tilted, and the lighting would slide across the surface as it turned.
        SetMatrix(u.NormalMatrix,
            Matrix4x4.Invert(world, out var inverse) ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity);

        var colour = material.BaseColor;
        gl.Uniform4f(u.BaseColor, colour.X, colour.Y, colour.Z, colour.W);
        gl.Uniform1f(u.Metallic, material.Metallic);
        gl.Uniform1f(u.Roughness, material.Roughness);
        SetVector3(u.Emissive, material.Emissive);
        gl.Uniform1f(u.AlphaCutoff, material.AlphaCutoff);
        gl.Uniform1i(u.AlphaMode, material.Alpha switch
        {
            AlphaMode.Mask => 1,
            AlphaMode.Blend => 2,
            _ => 0,
        });
        gl.Uniform1i(u.HasVertexColor, gpu.HasColor ? 1 : 0);
        gl.Uniform1i(u.Unlit, material.Unlit ? 1 : 0);

        // The caller's own uniforms last, so nothing above can overwrite one of them. A name this
        // program does not declare resolves to -1 and is skipped: a driver is entitled to optimise an
        // unused uniform out of existence, so its absence is not evidence of a mistake and must not
        // be reported as one.
        if (material.ShaderValues.Count > 0)
            foreach (var (name, value) in material.ShaderValues)
                SetValue(program.Location(gl, name), value);

        gl.ActiveTexture(GlApi.TEXTURE0);
        // Always bound, even with no texture: sampling a one-pixel white image costs nothing and
        // removes a shader permutation. An UNBOUND sampler is undefined behaviour on some drivers
        // and renders black on others, which is the worst kind of difference to debug remotely.
        gl.BindTexture(GlApi.TEXTURE_2D, texture != 0 ? texture : White());
        gl.Uniform1i(u.HasTex, texture != 0 ? 1 : 0);

        // A normal map needs a tangent frame to be interpreted in. Without one the map would be
        // applied against an arbitrary basis and the lighting would swim as the model turns, so the
        // map is ignored rather than applied wrongly - and MeshTangents exists so a loader can
        // supply the frame rather than a caller discovering this.
        var useNormalMap = normalMap != 0 && gpu.HasTangents;
        gl.ActiveTexture(GlApi.TEXTURE0 + 1);
        gl.BindTexture(GlApi.TEXTURE_2D, useNormalMap ? normalMap : White());
        gl.Uniform1i(u.HasNormalMap, useNormalMap ? 1 : 0);
        gl.ActiveTexture(GlApi.TEXTURE0);

        // Culling is skipped whenever a back face is something we want to SEE: a double-sided
        // material, a wireframe, or the debug view whose entire purpose is to show them. Leaving
        // culling on with HighlightBackfaces would produce a view that reported no problems because
        // it had thrown away the evidence.
        // Culling off whenever a back face is something to SEE: a double-sided material, a
        // wireframe, the backface debug view, or a section - where the interior of the far wall IS
        // the picture, and culling it would leave the cut looking like a hole through the model.
        if (material.DoubleSided || settings.Wireframe || settings.HighlightBackfaces ||
            settings.Section is not null)
            gl.Disable(GlApi.CULL_FACE);
        else { gl.Enable(GlApi.CULL_FACE); gl.CullFace(GlApi.BACK); }

        gl.BindVertexArray(gpu.Vao);
        if (settings.Wireframe && mesh.Kind == PrimitiveKind.Triangles)
        {
            // glPolygonMode does not exist in OpenGL ES or WebGL2, so a wireframe drawn that way
            // would work on a desktop and silently do nothing everywhere else. An edge index buffer
            // costs one upload and behaves identically on all six targets, which is the trade this
            // project said it would make whenever unified and fastest disagree.
            var lines = EnsureEdges(gpu, mesh);
            gl.BindBuffer(GlApi.ELEMENT_ARRAY_BUFFER, lines.Buffer);
            gl.DrawElements(GlApi.LINES, lines.Count, GlApi.UNSIGNED_INT, null);
            gl.BindBuffer(GlApi.ELEMENT_ARRAY_BUFFER, gpu.Indices);
        }
        else
        {
            var mode = mesh.Kind switch
            {
                PrimitiveKind.Lines => GlApi.LINES,
                PrimitiveKind.Points => GlApi.POINTS,
                _ => GlApi.TRIANGLES,
            };
            gl.DrawElements(mode, gpu.IndexCount, GlApi.UNSIGNED_INT, null);
        }
        gl.BindVertexArray(0);
    }

    /// <summary>Drop the GPU copy of a mesh — for an editor that has changed it, or a viewer that has
    /// closed a document. Must be called with the context current.</summary>
    public void Forget(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (_disposed || !_meshes.Remove(mesh, out var gpu)) return;
        gpu.Delete(_gl);
    }

    /// <summary>
    /// Build a caller's shader now and say how it went, rather than finding out mid-frame.
    ///
    /// <para>Calling this is optional — a material carrying a shader compiles it the first time it is
    /// drawn either way — and it is what an application with a shader editor in it wants, because the
    /// report can then be put in front of the person who just typed the mistake. Idempotent: the
    /// result is remembered against the shader, so this is also how to read the report of a shader
    /// that has already been built.</para>
    ///
    /// <para>Must be called with the context current.</para>
    /// </summary>
    public ShaderReport Prepare(Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reports.TryGetValue(shader, out var known)) return known;

        // THE TEXT IS JUDGED BEFORE THE DRIVER SEES IT, because the driver in front of us is the
        // wrong judge of whether this compiles anywhere else. See ShaderCheck.
        var inspection = ShaderCheck.Inspect(shader);
        var notes = inspection.Notes;
        if (!inspection.Ok)
            return Remember(shader, null, new ShaderReport(false, Describe(shader, inspection.Error), notes));

        var dialect = _gl.Dialect;
        var vertexSource = shader.Kind == ShaderKind.Program
            ? ShaderSource.Whole(dialect, shader.Vertex!)
            : ShaderSource.VertexWith(dialect, shader.Vertex);
        var fragmentSource = shader.Kind == ShaderKind.Program
            ? ShaderSource.Whole(dialect, shader.Fragment)
            : ShaderSource.FragmentWith(dialect, shader.Fragment);

        var handle = Build(_gl, vertexSource, fragmentSource, out var error);
        return handle == 0
            ? Remember(shader, null, new ShaderReport(false, Describe(shader, error), notes))
            : Remember(shader, new GpuProgram(_gl, handle), new ShaderReport(true, null, notes));
    }

    /// <summary>Drop the compiled program for a shader — for an editor that has just changed its
    /// text and wants the next draw to rebuild it. Must be called with the context current.</summary>
    public void Forget(Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        if (_disposed) return;
        if (_programs.Remove(shader, out var program) && program is not null) _gl.DeleteProgram(program.Handle);
        _reports.Remove(shader);
    }

    /// <summary>Caller shaders that have been built, successfully or not.</summary>
    public int CachedShaders => _reports.Count;

    /// <summary>Meshes currently held on the GPU.</summary>
    public int CachedMeshes => _meshes.Count;

    /// <summary>Textures currently held on the GPU.</summary>
    public int CachedTextures => _textures.Count;

    // ---- uploads ---------------------------------------------------------------------------

    private GpuMesh Upload(Mesh mesh)
    {
        if (_meshes.TryGetValue(mesh, out var existing)) return existing;

        var gl = _gl;
        var hasUv = mesh.Uvs is { Length: > 0 };
        var hasColor = mesh.Colors is { Length: > 0 };
        var hasNormal = mesh.Normals is { Length: > 0 };
        var hasTangent = mesh.Tangents is { Length: > 0 };

        // Interleaved into ONE buffer rather than one per channel. A vertex is fetched as a unit, so
        // separate arrays cost an extra cache line per attribute per vertex — which on a
        // million-triangle model is the difference between a smooth orbit and a stuttering one. The
        // packing happens once, at upload, not per frame.
        var floats = 3 + (hasNormal ? 3 : 0) + (hasUv ? 2 : 0) + (hasColor ? 4 : 0) + (hasTangent ? 4 : 0);
        var stride = floats * sizeof(float);
        var data = new float[(long)mesh.VertexCount * floats];

        for (int v = 0, at = 0; v < mesh.VertexCount; v++)
        {
            var p = mesh.Positions[v];
            data[at++] = p.X; data[at++] = p.Y; data[at++] = p.Z;
            if (hasNormal) { var n = mesh.Normals![v]; data[at++] = n.X; data[at++] = n.Y; data[at++] = n.Z; }
            if (hasUv) { var t = mesh.Uvs![v]; data[at++] = t.X; data[at++] = t.Y; }
            if (hasColor) { var c = mesh.Colors![v]; data[at++] = c.X; data[at++] = c.Y; data[at++] = c.Z; data[at++] = c.W; }
            if (hasTangent) { var g = mesh.Tangents![v]; data[at++] = g.X; data[at++] = g.Y; data[at++] = g.Z; data[at++] = g.W; }
        }

        uint vao, vbo, ebo;
        gl.GenVertexArrays(1, &vao);
        gl.BindVertexArray(vao);

        gl.GenBuffers(1, &vbo);
        gl.BindBuffer(GlApi.ARRAY_BUFFER, vbo);
        fixed (float* p = data)
            gl.BufferData(GlApi.ARRAY_BUFFER, (nint)((long)data.Length * sizeof(float)), p, GlApi.STATIC_DRAW);

        var offset = 0;
        Attribute(ShaderSource.AttrPosition, 3, ref offset);
        if (hasNormal) Attribute(ShaderSource.AttrNormal, 3, ref offset);
        if (hasUv) Attribute(ShaderSource.AttrUv, 2, ref offset);
        if (hasColor) Attribute(ShaderSource.AttrColor, 4, ref offset);
        if (hasTangent) Attribute(ShaderSource.AttrTangent, 4, ref offset);

        gl.GenBuffers(1, &ebo);
        gl.BindBuffer(GlApi.ELEMENT_ARRAY_BUFFER, ebo);
        fixed (int* p = mesh.Indices)
            gl.BufferData(GlApi.ELEMENT_ARRAY_BUFFER,
                          (nint)((long)mesh.Indices.Length * sizeof(int)), p, GlApi.STATIC_DRAW);

        gl.BindVertexArray(0);

        var gpu = new GpuMesh
        {
            Vao = vao,
            Vertices = vbo,
            Indices = ebo,
            IndexCount = mesh.Indices.Length,
            HasColor = hasColor,
            HasTangents = hasTangent,
        };
        _meshes[mesh] = gpu;
        return gpu;

        void Attribute(uint location, int components, ref int floatOffset)
        {
            gl.EnableVertexAttribArray(location);
            gl.VertexAttribPointer(location, components, GlApi.FLOAT, 0, stride,
                                   (void*)(nint)(floatOffset * sizeof(float)));
            floatOffset += components;
        }
    }

    /// <summary>
    /// The GL texture for a material's base colour map, uploaded on first sight.
    ///
    /// <para>Cached against the <see cref="ImageData"/> object by reference, so an image shared by
    /// four materials is uploaded once — which is what the scene model already said by making
    /// textures indices rather than objects.</para>
    /// </summary>
    private uint TextureFor(Scene scene, Material material) =>
        material.BaseColorTexture is { } index ? TextureAt(scene, index) : 0;

    private uint NormalMapFor(Scene scene, Material material) =>
        material.NormalTexture is { } index ? TextureAt(scene, index) : 0;

    private uint TextureAt(Scene scene, int index)
    {
        if ((uint)index >= (uint)scene.Textures.Count) return 0;
        var reference = scene.Textures[index];
        if ((uint)reference.Image >= (uint)scene.Images.Count) return 0;

        var image = scene.Images[reference.Image];
        // A loader with no image decoder records a placeholder rather than failing the load, so an
        // empty one here is the documented "textures were skipped" outcome and not an error.
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length < image.Width * image.Height * 4)
            return 0;

        // Keyed on the TEXTURE, not the image: the same image sampled tiling on one material and
        // clamped on another is two GL objects, because the sampler state lives on the object.
        if (_textures.TryGetValue(reference, out var existing)) return existing;

        uint texture;
        _gl.GenTextures(1, &texture);
        _gl.BindTexture(GlApi.TEXTURE_2D, texture);
        fixed (byte* pixels = image.Pixels)
            _gl.TexImage2D(GlApi.TEXTURE_2D, 0, (int)GlApi.RGBA8, image.Width, image.Height, 0,
                           GlApi.RGBA, GlApi.UNSIGNED_BYTE, pixels);

        _gl.TexParameteri(GlApi.TEXTURE_2D, GlApi.TEX_WRAP_S, Wrap(reference.WrapS));
        _gl.TexParameteri(GlApi.TEXTURE_2D, GlApi.TEX_WRAP_T, Wrap(reference.WrapT));
        _gl.TexParameteri(GlApi.TEXTURE_2D, GlApi.TEX_MAG_FILTER,
                          reference.Magnify == TextureFilter.Nearest ? GlApi.NEAREST : GlApi.LINEAR);

        if (reference.Mipmaps)
        {
            // Without a mip chain a texture minified across a curved surface aliases into a
            // shimmering mess the moment the model turns, which reads as a rendering fault rather
            // than as a missing filter setting.
            _gl.TexParameteri(GlApi.TEXTURE_2D, GlApi.TEX_MIN_FILTER,
                reference.Minify == TextureFilter.Nearest
                    ? GlApi.NEAREST_MIPMAP_LINEAR : GlApi.LINEAR_MIPMAP_LINEAR);
            _gl.GenerateMipmap(GlApi.TEXTURE_2D);
        }
        else
        {
            _gl.TexParameteri(GlApi.TEXTURE_2D, GlApi.TEX_MIN_FILTER,
                reference.Minify == TextureFilter.Nearest ? GlApi.NEAREST : GlApi.LINEAR);
        }

        _textures[reference] = texture;
        return texture;
    }

    private static int Wrap(TextureWrap wrap) => wrap switch
    {
        TextureWrap.ClampToEdge => GlApi.CLAMP_TO_EDGE,
        TextureWrap.MirroredRepeat => GlApi.MIRRORED_REPEAT,
        _ => GlApi.REPEAT,
    };

    /// <summary>The line index buffer for a wireframe pass, built once per mesh and kept.</summary>
    private (uint Buffer, int Count) EnsureEdges(GpuMesh gpu, Mesh mesh)
    {
        if (gpu.Edges != 0) return (gpu.Edges, gpu.EdgeCount);

        var edges = new int[mesh.Indices.Length * 2];
        for (int i = 0, at = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            edges[at++] = a; edges[at++] = b;
            edges[at++] = b; edges[at++] = c;
            edges[at++] = c; edges[at++] = a;
        }

        uint buffer;
        _gl.GenBuffers(1, &buffer);
        _gl.BindBuffer(GlApi.ELEMENT_ARRAY_BUFFER, buffer);
        fixed (int* p = edges)
            _gl.BufferData(GlApi.ELEMENT_ARRAY_BUFFER,
                           (nint)((long)edges.Length * sizeof(int)), p, GlApi.STATIC_DRAW);

        gpu.Edges = buffer;
        gpu.EdgeCount = edges.Length;
        return (buffer, edges.Length);
    }

    /// <summary>A one-pixel white texture, so the shader samples unconditionally and needs no
    /// branch-free-but-untextured path. Costs four bytes once.</summary>
    private uint White()
    {
        if (_whiteTexture != 0) return _whiteTexture;
        uint texture;
        _gl.GenTextures(1, &texture);
        _gl.BindTexture(GlApi.TEXTURE_2D, texture);
        var white = stackalloc byte[4] { 255, 255, 255, 255 };
        _gl.TexImage2D(GlApi.TEXTURE_2D, 0, (int)GlApi.RGBA8, 1, 1, 0, GlApi.RGBA, GlApi.UNSIGNED_BYTE, white);
        _gl.TexParameteri(GlApi.TEXTURE_2D, GlApi.TEX_MIN_FILTER, GlApi.LINEAR);
        _gl.TexParameteri(GlApi.TEXTURE_2D, GlApi.TEX_MAG_FILTER, GlApi.LINEAR);
        _whiteTexture = texture;
        return texture;
    }

    // ---- helpers ---------------------------------------------------------------------------

    // ---- programs --------------------------------------------------------------------------

    /// <summary>The program a material draws with: its own if it has one that built, ours if not.</summary>
    private GpuProgram ProgramFor(Material material)
    {
        if (material.Shader is not { } shader) return _builtin;
        if (_programs.TryGetValue(shader, out var cached)) return cached ?? _builtin;

        Prepare(shader);
        return _programs.TryGetValue(shader, out var built) && built is not null ? built : _builtin;
    }

    private ShaderReport Remember(Shader shader, GpuProgram? program, ShaderReport report)
    {
        _programs[shader] = program;
        _reports[shader] = report;
        return report;
    }

    private static string Describe(Shader shader, string? problem) =>
        $"{shader.Name ?? "shader"}: {problem ?? "no reason given"}";

    /// <summary>
    /// Push everything that is true of the whole frame — where the camera is, what the light is
    /// doing, which debug views are on — onto one program.
    ///
    /// <para>Per program rather than per frame, because uniforms belong to a program and not to the
    /// context: a scene mixing the built-in shader with two custom ones needs this three times, and
    /// the frame counter is what stops it happening once per draw.</para>
    /// </summary>
    private void ApplyFrame(GpuProgram program, in Camera camera, RenderSettings settings)
    {
        var gl = _gl;
        var u = program.U;

        SetVector3(u.CamPos, camera.Position);
        ApplyLights(u, settings);

        var environment = settings.Environment;
        var intensity = MathF.Max(environment.Intensity, 0f);
        SetVector3(u.SkyColor, environment.Sky * intensity);
        SetVector3(u.HorizonColor, environment.Horizon * intensity);
        SetVector3(u.GroundColor, environment.Ground * intensity);
        SetVector3(u.UpAxis, settings.Up == Khalkos3D.UpAxis.Z ? Vector3.UnitZ : Vector3.UnitY);
        gl.Uniform1i(u.ShowNormals, settings.ShowNormals ? 1 : 0);
        gl.Uniform1i(u.HighlightBackfaces, settings.HighlightBackfaces ? 1 : 0);
        SetVector3(u.BackfaceColor, settings.BackfaceColor);

        gl.Uniform1i(u.SectionActive, settings.Section is not null ? 1 : 0);
        if (settings.Section is { } plane && u.SectionPlane >= 0)
            gl.Uniform4f(u.SectionPlane, plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.D);
        SetVector3(u.SectionColor, settings.SectionColor);

        // Fixed units: base colour on 0, normal map on 1. The mapping never changes, so it is set
        // when a frame first reaches a program rather than per draw.
        gl.Uniform1i(u.Tex, 0);
        gl.Uniform1i(u.NormalTex, 1);
    }

    /// <summary>
    /// Push the frame's lights into one program.
    ///
    /// <para><b>An empty <see cref="RenderSettings.Lights"/> means the key light</b>, built here from
    /// <see cref="RenderSettings.LightDirection"/> and <see cref="RenderSettings.LightColor"/>. That
    /// is what makes this change invisible to every caller who never asked for it, and it is done
    /// without allocating a list to hold one light in.</para>
    ///
    /// <para>Past <see cref="Light.Max"/> the extras are dropped rather than wrapped or averaged.
    /// Which ones survive is the caller's decision, made by the order they were given in — only they
    /// know whether that should be by distance, by brightness, or by what the picture is about.</para>
    /// </summary>
    private void ApplyLights(Uniforms u, RenderSettings settings)
    {
        var lights = settings.Lights;
        var count = lights.Count == 0 ? 1 : Math.Min(lights.Count, Light.Max);
        _gl.Uniform1i(u.LightCount, count);

        if (lights.Count == 0)
        {
            SetLight(u, 0, Light.Directional(
                Normalise(settings.LightDirection, new Vector3(0, -1, 0)), settings.LightColor));
            return;
        }

        for (var i = 0; i < count; i++) SetLight(u, i, lights[i]);
    }

    private void SetLight(Uniforms u, int slot, Light light)
    {
        var vector = light.Kind == LightKind.Directional
            ? Normalise(light.Vector, new Vector3(0, -1, 0))
            : light.Vector;

        SetVector3(u.LightVector[slot], vector);
        SetVector3(u.LightColor[slot], light.Color);
        // Zero is what marks a light directional in the shader: a point light with no reach is not a
        // light, so the value is free to mean something else.
        if (u.LightRange[slot] >= 0)
            _gl.Uniform1f(u.LightRange[slot], light.Kind == LightKind.Point ? MathF.Max(light.Range, 1e-4f) : 0f);
    }

    /// <summary>
    /// Set one caller-supplied value, choosing the call from the value's own type.
    ///
    /// <para>The kind is carried rather than inferred because GL does not object to the wrong one: a
    /// vec3 set with <c>glUniform4f</c> leaves the old value in place and returns quietly, so the
    /// mistake shows up as a shader that works where it was written and not elsewhere.</para>
    /// </summary>
    private void SetValue(int location, ShaderValue value)
    {
        if (location < 0) return;

        var gl = _gl;
        var v = value.Vector;
        switch (value.Kind)
        {
            case ShaderValueKind.Float: gl.Uniform1f(location, value.Scalar); break;
            case ShaderValueKind.Int: gl.Uniform1i(location, (int)value.Scalar); break;
            case ShaderValueKind.Vector2: gl.Uniform2f(location, v.X, v.Y); break;
            case ShaderValueKind.Vector3: gl.Uniform3f(location, v.X, v.Y, v.Z); break;
            case ShaderValueKind.Vector4: gl.Uniform4f(location, v.X, v.Y, v.Z, v.W); break;
            case ShaderValueKind.Matrix: SetMatrix(location, value.Matrix); break;
        }
    }

    private static Vector3 Normalise(Vector3 value, Vector3 fallback)
    {
        var length = value.Length();
        return length > 1e-6f ? value / length : fallback;
    }

    private void SetMatrix(int location, Matrix4x4 matrix)
    {
        if (location < 0) return;
        // Uploaded UNTRANSPOSED, and that is correct rather than an oversight. System.Numerics is
        // row-major with row vectors; GLSL is column-major with column vectors. Reading row-major
        // bytes as column-major IS the transpose, and transposing a row-vector matrix is exactly the
        // column-vector matrix GLSL wants — so the two conventions cancel and `uMvp * vec4(pos,1)`
        // in the shader is right.
        _gl.UniformMatrix4fv(location, 1, 0, &matrix.M11);
    }

    private void SetVector3(int location, Vector3 value)
    {
        if (location >= 0) _gl.Uniform3f(location, value.X, value.Y, value.Z);
    }

    private static uint Compile(GlApi gl, uint type, string source, out string? error)
    {
        error = null;
        var shader = gl.CreateShader(type);
        var bytes = Encoding.UTF8.GetBytes(source + "\0");
        fixed (byte* p = bytes)
        {
            byte** one = stackalloc byte*[1];
            one[0] = p;
            gl.ShaderSource(shader, 1, one, null);
        }
        gl.CompileShader(shader);

        int ok;
        gl.GetShaderiv(shader, GlApi.COMPILE_STATUS, &ok);
        if (ok != 0) return shader;

        var log = stackalloc byte[2048];
        gl.GetShaderInfoLog(shader, 2048, null, log);
        error = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)log);
        gl.DeleteShader(shader);
        return 0;
    }

    private static void BindAttribute(GlApi gl, uint program, uint location, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes) gl.BindAttribLocation(program, location, p);
    }

    /// <summary>
    /// One linked program: the engine's, or one built from a caller's <see cref="Shader"/>.
    /// </summary>
    private sealed class GpuProgram(GlApi gl, uint handle)
    {
        internal readonly uint Handle = handle;
        internal readonly Uniforms U = new(gl, handle);

        /// <summary>The last frame whose shared uniforms were pushed to this program.</summary>
        internal long Frame = -1;

        // Caller uniform locations, looked up once each. glGetUniformLocation is a string lookup
        // inside the driver, and the answer cannot change while the program lives.
        private readonly Dictionary<string, int> _custom = [];

        internal int Location(GlApi api, string name)
        {
            if (_custom.TryGetValue(name, out var known)) return known;

            var bytes = Encoding.UTF8.GetBytes(name + "\0");
            int location;
            fixed (byte* p = bytes) location = api.GetUniformLocation(Handle, p);
            _custom[name] = location;
            return location;
        }
    }

    /// <summary>Uniform locations, resolved once. A location of -1 means the driver optimised the
    /// uniform away, which is legal and is why every setter tolerates it.</summary>
    private sealed class Uniforms
    {
        internal readonly int Mvp, Model, NormalMatrix, CamPos, LightCount;

        /// <summary>One location per array element. Resolved element by element because that is how
        /// GL addresses them — <c>uLightColor[3]</c> is its own name — and doing it once here is what
        /// keeps a per-light string lookup out of every frame.</summary>
        internal readonly int[] LightVector = new int[Light.Max];
        internal readonly int[] LightColor = new int[Light.Max];
        internal readonly int[] LightRange = new int[Light.Max];

        internal readonly int SkyColor, HorizonColor, GroundColor, UpAxis;
        internal readonly int BaseColor, Metallic, Roughness, Emissive, Tex, HasTex;
        internal readonly int NormalTex, HasNormalMap;
        internal readonly int HasVertexColor, AlphaMode, AlphaCutoff, ShowNormals;
        internal readonly int Unlit, HighlightBackfaces, BackfaceColor;
        internal readonly int SectionActive, SectionPlane, SectionColor;

        internal Uniforms(GlApi gl, uint program)
        {
            Mvp = Find(gl, program, "uMvp");
            Model = Find(gl, program, "uModel");
            NormalMatrix = Find(gl, program, "uNormalMatrix");
            CamPos = Find(gl, program, "uCamPos");
            LightCount = Find(gl, program, "uLightCount");
            for (var i = 0; i < Light.Max; i++)
            {
                LightVector[i] = Find(gl, program, $"uLightVector[{i}]");
                LightColor[i] = Find(gl, program, $"uLightColor[{i}]");
                LightRange[i] = Find(gl, program, $"uLightRange[{i}]");
            }
            SkyColor = Find(gl, program, "uSkyColor");
            HorizonColor = Find(gl, program, "uHorizonColor");
            GroundColor = Find(gl, program, "uGroundColor");
            UpAxis = Find(gl, program, "uUpAxis");
            BaseColor = Find(gl, program, "uBaseColor");
            Metallic = Find(gl, program, "uMetallic");
            Roughness = Find(gl, program, "uRoughness");
            Emissive = Find(gl, program, "uEmissive");
            Tex = Find(gl, program, "uTex");
            HasTex = Find(gl, program, "uHasTex");
            NormalTex = Find(gl, program, "uNormalTex");
            HasNormalMap = Find(gl, program, "uHasNormalMap");
            HasVertexColor = Find(gl, program, "uHasVertexColor");
            AlphaMode = Find(gl, program, "uAlphaMode");
            AlphaCutoff = Find(gl, program, "uAlphaCutoff");
            ShowNormals = Find(gl, program, "uShowNormals");
            Unlit = Find(gl, program, "uUnlit");
            HighlightBackfaces = Find(gl, program, "uHighlightBackfaces");
            BackfaceColor = Find(gl, program, "uBackfaceColor");
            SectionActive = Find(gl, program, "uSectionActive");
            SectionPlane = Find(gl, program, "uSectionPlane");
            SectionColor = Find(gl, program, "uSectionColor");
        }

        private static int Find(GlApi gl, uint program, string name)
        {
            var bytes = Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* p = bytes) return gl.GetUniformLocation(program, p);
        }
    }

    private sealed class GpuMesh
    {
        internal uint Vao, Vertices, Indices, Edges;
        internal int IndexCount, EdgeCount;
        internal bool HasColor, HasTangents;

        internal void Delete(GlApi gl)
        {
            uint vao = Vao, vbo = Vertices, ebo = Indices, edges = Edges;
            if (vao != 0) gl.DeleteVertexArrays(1, &vao);
            if (vbo != 0) gl.DeleteBuffers(1, &vbo);
            if (ebo != 0) gl.DeleteBuffers(1, &ebo);
            if (edges != 0) gl.DeleteBuffers(1, &edges);
            Vao = Vertices = Indices = Edges = 0;
        }
    }

    /// <summary>Release every GPU resource. Must be called with the owning context CURRENT — the only
    /// moment deleting GL objects is legal, and the reason this is not a finalizer.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var gpu in _meshes.Values) gpu.Delete(_gl);
        _meshes.Clear();

        foreach (var texture in _textures.Values) { var t = texture; _gl.DeleteTextures(1, &t); }
        _textures.Clear();

        if (_whiteTexture != 0) { var t = _whiteTexture; _gl.DeleteTextures(1, &t); _whiteTexture = 0; }
        _gl.UseProgram(0);

        foreach (var program in _programs.Values) if (program is not null) _gl.DeleteProgram(program.Handle);
        _programs.Clear();
        _reports.Clear();
        _gl.DeleteProgram(_builtin.Handle);
    }
}
