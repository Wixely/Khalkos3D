using System.Numerics;

namespace Khalkos3D;

/// <summary>
/// How a frame should look: lighting, and the handful of switches a viewer needs. Backend-agnostic,
/// so it lives here rather than in the GL project — a second backend would honour the same values.
/// </summary>
public readonly record struct RenderSettings
{
    /// <summary>A neutral three-quarter key light, which is what makes an unfamiliar model read as
    /// solid the moment it appears.</summary>
    public static RenderSettings Default { get; } = new();

    /// <summary>Direction the light travels TOWARDS the scene. Normalised on use, so any length
    /// works.</summary>
    public Vector3 LightDirection { get; init; } = Vector3.Normalize(new Vector3(-0.4f, -0.8f, -0.5f));

    /// <summary>Light colour and intensity, linear RGB. Values above 1 are a brighter lamp.</summary>
    public Vector3 LightColor { get; init; } = new(2.6f, 2.6f, 2.6f);

    /// <summary>
    /// The lights, when one key light is not enough — up to <see cref="Light.Max"/> of them, of any
    /// mix of kinds.
    ///
    /// <para><b>Empty means the key light above, which is the whole compatibility story.</b> A caller
    /// who never heard of this property gets exactly the frame they got before: one directional light
    /// from <see cref="LightDirection"/> and <see cref="LightColor"/>. A caller who fills this in
    /// REPLACES that light rather than adding to it — so a scene lit entirely by its own lamps is
    /// possible, which it would not be if the key light were always present and only ever
    /// supplemented.</para>
    ///
    /// <para>More than <see cref="Light.Max"/> is not an error and not a silent surprise either: the
    /// first <see cref="Light.Max"/> are used, in the order given, and the rest are ignored. Sorting
    /// them by what matters is the caller's job, because only the caller knows whether that is
    /// distance, brightness or importance to the picture.</para>
    /// </summary>
    public IReadOnlyList<Light> Lights { get; init; } = [];

    /// <summary>
    /// What the model is standing in. See <see cref="Khalkos3D.Environment"/> — without it a metal
    /// has nothing to reflect and renders black, which reads as a broken shader rather than as an
    /// empty room.
    /// </summary>
    public Environment Environment { get; init; } = Environment.Studio;

    /// <summary>
    /// Which axis is up, so the environment gradient runs the right way.
    ///
    /// <para>Set it from <see cref="Scene.Up"/>. A Z-up model lit by a Y-up gradient is lit from the
    /// side, which looks like a lamp in the wrong place rather than like a wrong setting.</para>
    /// </summary>
    public UpAxis Up { get; init; } = UpAxis.Y;

    /// <summary>Clear the target to this colour before drawing, or null to draw over whatever is
    /// already there. Null by default: a host that composites this into its own frame has already
    /// cleared, and clearing again would erase what it put behind the model.</summary>
    public Vector4? ClearColor { get; init; }

    /// <summary>Draw triangle edges instead of filled faces. Costs nothing when off, and is the
    /// quickest way to see that a model is a mess of slivers rather than the shape it appears to
    /// be.</summary>
    public bool Wireframe { get; init; }

    /// <summary>Shade by surface normal instead of by material — the debug view that makes an
    /// inverted facet obvious, which is the defect hardest to see any other way.</summary>
    public bool ShowNormals { get; init; }

    /// <summary>
    /// Paint back-facing triangles in a warning colour.
    ///
    /// <para><b>The counterpart to the shader flipping normals towards the viewer.</b> That flip is
    /// what stops an inverted facet appearing as a black hole in geometry that is otherwise fine —
    /// but it also HIDES the inversion, so someone who wants to fix their mesh has no way to see it.
    /// This is that way: with it on, anything wound the wrong way lights up.</para>
    /// </summary>
    public bool HighlightBackfaces { get; init; }

    /// <summary>The colour <see cref="HighlightBackfaces"/> uses. Magenta by default because nothing
    /// in a real material is that colour, so it cannot be mistaken for the model.</summary>
    public Vector3 BackfaceColor { get; init; } = new(1f, 0f, 0.85f);

    /// <summary>
    /// Cut the model with a plane and look inside. Null draws it whole.
    ///
    /// <para>Build one with <see cref="BoundingBox.SectionAt"/>, which expresses the cut as a
    /// fraction through the model so a viewer can offer a slider without knowing the scale.</para>
    ///
    /// <para><b>It is a clip, not a cap, and the difference is worth stating.</b> Fragments in front
    /// of the plane are discarded, so what shows through the cut is the INSIDE of the far wall,
    /// painted in <see cref="SectionColor"/> so it reads as a surface rather than as a hole. A true
    /// cap - a flat filled face exactly where the plane crosses the solid - needs a stencil pass and
    /// only means anything on a closed manifold, which loaded geometry frequently is not. What is here
    /// shows wall thickness and internal structure, which is usually the question.</para>
    /// </summary>
    public Plane? Section { get; init; }

    /// <summary>The colour the exposed interior is painted when <see cref="Section"/> is set.</summary>
    public Vector3 SectionColor { get; init; } = new(0.85f, 0.35f, 0.12f);

    /// <summary>Create the defaults.</summary>
    public RenderSettings() { }
}
