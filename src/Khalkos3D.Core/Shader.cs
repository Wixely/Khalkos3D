namespace Khalkos3D;

/// <summary>How much of the program the caller's GLSL replaces.</summary>
public enum ShaderKind
{
    /// <summary>
    /// The engine keeps the program and the caller fills in the surface. Their source defines
    /// <c>void surface(inout Surface s)</c>, and optionally <c>void vertex(inout Vertex v)</c>, and
    /// declares any extra uniforms it wants; everything else — the version directive, the attributes,
    /// the varyings, the lighting, the tone mapping, the debug views, the section plane and the alpha
    /// handling — stays the engine's.
    /// </summary>
    Surface,

    /// <summary>
    /// The caller writes both stages entirely. The engine contributes the version directive, the
    /// attribute locations, and values for whichever of its uniforms the source declares. Nothing
    /// else applies: the debug views, the section plane and the shading model are all things this
    /// source now owns or does without.
    /// </summary>
    Program,
}

/// <summary>
/// GLSL supplied by the application, in one of two sizes.
///
/// <para><b>Two layers over one mechanism, and the smaller one is the one to reach for.</b> A
/// <see cref="ShaderKind.Surface"/> shader cannot get the portability wrong, because the parts that
/// differ between OpenGL 3.3, OpenGL ES 3.0 and WebGL2 — the version line, the precision qualifier,
/// the attribute and varying declarations — are not in it. The same snippet compiles on a desktop, a
/// phone and a browser, and the wireframe, normals and back-face views keep working over it. A
/// <see cref="ShaderKind.Program"/> shader can do anything and takes that guarantee back: the engine
/// still writes the version line, but the shading model, the debug views and the dialect differences
/// become the author's to handle.</para>
///
/// <para><b>Held and cached by reference, exactly like <see cref="Mesh"/>.</b> The renderer compiles
/// a program the first time it sees one of these and keeps it against the object identity, so a
/// shader shared by two hundred materials is one compile. Build it once and hold it; rebuilding an
/// equivalent instance every frame would recompile every frame, which is why this is deliberately
/// not a record with value equality.</para>
///
/// <para>Nothing here is GL-specific beyond the language: this type carries text and names, so it
/// lives with the assets rather than with the renderer, and a scene holding custom-shaded materials
/// still loads, saves and analyses on a machine with no GL at all.</para>
/// </summary>
public sealed class Shader
{
    private Shader(ShaderKind kind, string? vertex, string fragment, string? name)
    {
        Kind = kind;
        Vertex = vertex;
        Fragment = fragment;
        Name = name;
    }

    /// <summary>
    /// A surface shader: the engine's program with the caller's surface function in it.
    /// </summary>
    /// <param name="fragment">GLSL defining <c>void surface(inout Surface s)</c>, plus any uniforms
    /// it declares. It is handed the material as the engine resolved it — base colour with the
    /// texture and vertex colour already applied, the world-space normal with any normal map already
    /// applied, metallic, roughness, emissive — and whatever it leaves behind is what gets lit.</param>
    /// <param name="vertex">Optional GLSL defining <c>void vertex(inout Vertex v)</c>, which sees the
    /// object-space position, normal and uv before the transform. This is where displacement goes;
    /// leave it null for a surface that only changes how the geometry looks.</param>
    /// <param name="name">For error messages and logs. Worth setting: a compile failure that names
    /// the shader is a fix, and one that does not is a hunt.</param>
    public static Shader Surface(string fragment, string? vertex = null, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fragment);
        return new Shader(ShaderKind.Surface, vertex, fragment, name);
    }

    /// <summary>
    /// A whole program, both stages written by the caller.
    ///
    /// <para>The engine prepends the version directive — that line and only that line, because the
    /// same text is illegal in the other dialect and there is no way for source to be portable while
    /// containing it. Attribute locations are bound before linking, so <c>aPos</c>, <c>aNormal</c>,
    /// <c>aUv</c>, <c>aColor</c> and <c>aTangent</c> mean what they mean here regardless of the order
    /// the source declares them in. Every engine uniform is set on this program if the source
    /// declares it and skipped if it does not, so a program can take <c>uMvp</c> and nothing else.
    /// </para>
    /// </summary>
    /// <param name="vertex">The whole vertex stage, without a version directive.</param>
    /// <param name="fragment">The whole fragment stage, without a version directive. It must declare
    /// its own <c>out</c> — neither dialect still has <c>gl_FragColor</c>.</param>
    /// <param name="name">For error messages and logs.</param>
    public static Shader Program(string vertex, string fragment, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vertex);
        ArgumentException.ThrowIfNullOrWhiteSpace(fragment);
        return new Shader(ShaderKind.Program, vertex, fragment, name);
    }

    /// <summary>How much of the program this source replaces.</summary>
    public ShaderKind Kind { get; }

    /// <summary>The vertex source: a hook for <see cref="ShaderKind.Surface"/> and null when there is
    /// none, the whole stage for <see cref="ShaderKind.Program"/>.</summary>
    public string? Vertex { get; }

    /// <summary>The fragment source: a surface function, or the whole stage.</summary>
    public string Fragment { get; }

    /// <summary>What to call it in an error message.</summary>
    public string? Name { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name ?? "shader"} ({Kind})";
}
