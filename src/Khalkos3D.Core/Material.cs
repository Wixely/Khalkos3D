using System.Numerics;

namespace Khalkos3D;

/// <summary>How a material's alpha channel is meant to be interpreted.</summary>
public enum AlphaMode
{
    /// <summary>Fully opaque; alpha is ignored. The default, and the only one a first renderer must
    /// handle correctly.</summary>
    Opaque,
    /// <summary>A cutout: alpha below <see cref="Material.AlphaCutoff"/> is discarded, the rest is
    /// opaque. Foliage and grilles. Needs no sorting, which is why it is worth having before blend.</summary>
    Mask,
    /// <summary>True transparency, and the expensive one: correct results need the geometry sorted
    /// back to front, which is a per-frame cost and never quite right for intersecting surfaces.</summary>
    Blend,
}

/// <summary>
/// A surface, described the way glTF 2.0 describes one: metallic-roughness PBR.
///
/// <para><b>Not because glTF is the priority, but because it is the interchange consensus.</b>
/// Blender, every DCC tool and every modern engine speak this model, so a material read from OBJ,
/// 3MF or STL can be expressed in it without inventing a private vocabulary — and anything written
/// back out lands somewhere useful. The alternative, a bespoke shading model, makes every importer
/// and exporter a translation.</para>
///
/// <para>Texture fields are indices into <see cref="Scene.Textures"/> rather than objects, so a
/// scene stays serialisable, a texture shared by four materials is stored once, and the renderer can
/// upload each one exactly once.</para>
/// </summary>
public sealed class Material
{
    /// <summary>The default: a plain, slightly rough white dielectric. What a mesh with no material
    /// gets, and deliberately neutral — an unlit magenta would be more visible, but a viewer showing
    /// untextured geometry should show the geometry, not a diagnostic.</summary>
    public static Material Default { get; } = new() { Name = "default" };

    /// <summary>Name from the file, for pick lists and diagnostics.</summary>
    public string? Name { get; init; }

    /// <summary>Base colour as straight (not premultiplied) linear RGBA. Multiplies
    /// <see cref="BaseColorTexture"/> when there is one.</summary>
    public Vector4 BaseColor { get; init; } = Vector4.One;

    /// <summary>0 for a dielectric (plastic, resin, wood), 1 for bare metal. Values between are
    /// physically meaningless and exist only for texture blending across a boundary.</summary>
    public float Metallic { get; init; }

    /// <summary>0 is a mirror, 1 is fully diffuse. The default sits where most matte plastic
    /// actually lands.</summary>
    public float Roughness { get; init; } = 0.6f;

    /// <summary>Light this surface emits regardless of any lamp, linear RGB. Non-zero makes a
    /// material glow.</summary>
    public Vector3 Emissive { get; init; }

    /// <summary>Index into <see cref="Scene.Textures"/> for the base colour map, or null.</summary>
    public int? BaseColorTexture { get; init; }

    /// <summary>Index into <see cref="Scene.Textures"/> for the tangent-space normal map, or
    /// null.</summary>
    public int? NormalTexture { get; init; }

    /// <summary>How alpha is interpreted.</summary>
    public AlphaMode Alpha { get; init; } = AlphaMode.Opaque;

    /// <summary>The threshold for <see cref="AlphaMode.Mask"/>.</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>
    /// Render back faces as well as front.
    ///
    /// <para>Defaults to TRUE, which is the opposite of glTF's default and deliberate. Geometry meant
    /// to be a closed solid could be culled for free — but real STL files routinely contain inverted
    /// facets, and a hole in a model that is otherwise fine is a bug report about this renderer.
    /// Correct-looking beats theoretically-faster for a format that carries no such declaration; a
    /// glTF loader sets it from the file and gets the culling back.</para>
    /// </summary>
    public bool DoubleSided { get; init; } = true;

    /// <summary>
    /// Skip lighting entirely and show the base colour as it is.
    ///
    /// <para>For scenery rather than surfaces: a grid, a work area, an axis marker, later a path or
    /// a trace. Shading a grid line makes it dim on one side of the model and bright on the other,
    /// which reads as a rendering fault because a line has no meaningful normal to be lit by.</para>
    /// </summary>
    public bool Unlit { get; init; }

    /// <summary>
    /// GLSL the application supplies for this surface, or null for the built-in metallic-roughness
    /// program that every material has always had.
    ///
    /// <para>Held by reference and compiled once per object, so this field wants a shader built at
    /// startup and shared — see <see cref="Khalkos3D.Shader"/>. Two hundred materials pointing at one
    /// <see cref="Khalkos3D.Shader"/> are one program; two hundred equivalent shader objects are two
    /// hundred compiles.</para>
    ///
    /// <para>A renderer that cannot compile it says so and draws this material with the built-in
    /// program instead. That is the honest failure for a viewer: the geometry a user asked to see is
    /// still on screen, and the reason it is the wrong colour is in a report rather than in a blank
    /// window.</para>
    /// </summary>
    public Shader? Shader { get; init; }

    /// <summary>
    /// Values for the uniforms <see cref="Shader"/> declares, by name.
    ///
    /// <para>Per material rather than per shader, because the shader is the code and this is the
    /// dressing: one wood shader, forty materials that differ by a grain scale. A name the program
    /// does not declare is skipped rather than treated as an error — a driver is entitled to optimise
    /// an unused uniform out of existence, so its absence says nothing about whether the caller made
    /// a mistake.</para>
    ///
    /// <para>Materials are cheap objects the renderer never caches, so a value that changes every
    /// frame is a new material per frame and costs nothing on the GPU. It is the <see cref="Shader"/>
    /// that must be kept, not the material holding it.</para>
    /// </summary>
    public IReadOnlyDictionary<string, ShaderValue> ShaderValues { get; init; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, ShaderValue>.Empty;

    /// <summary>True when this material needs to be drawn after the opaque pass.</summary>
    public bool IsTransparent => Alpha == AlphaMode.Blend || BaseColor.W < 1f;
}
