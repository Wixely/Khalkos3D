using System.Numerics;

namespace StannumFab;

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
    /// A flat term standing in for image-based lighting.
    ///
    /// <para>Not physically anything, and it is here because the honest alternative is worse: with no
    /// environment at all, a metal has nothing to reflect and renders black. A viewer showing a black
    /// model looks broken rather than looks unlit. Replaced by a real prefiltered environment when
    /// there is one.</para>
    /// </summary>
    public Vector3 Ambient { get; init; } = new(0.16f, 0.17f, 0.19f);

    /// <summary>Clear the target to this colour before drawing, or null to draw over whatever is
    /// already there. Null by default: a host that composites this into its own frame has already
    /// cleared, and clearing again would erase what it put behind the model.</summary>
    public Vector4? ClearColor { get; init; }

    /// <summary>Draw triangle edges instead of filled faces. Costs nothing when off, and is the
    /// quickest way to see that a model is a mess of slivers rather than the shape it appears to
    /// be.</summary>
    public bool Wireframe { get; init; }

    /// <summary>Shade by surface normal instead of by material — the debug view that makes an
    /// inverted facet obvious, which on a printable mesh is the defect that matters most.</summary>
    public bool ShowNormals { get; init; }

    /// <summary>
    /// Paint back-facing triangles in a warning colour.
    ///
    /// <para><b>The counterpart to the shader flipping normals towards the viewer.</b> That flip is
    /// what stops an inverted facet appearing as a black hole in a part the slicer would print
    /// perfectly well — but it also HIDES the inversion, so a user who wants to fix their mesh has no
    /// way to see it. This is that way: with it on, anything wound the wrong way lights up.</para>
    /// </summary>
    public bool HighlightBackfaces { get; init; }

    /// <summary>The colour <see cref="HighlightBackfaces"/> uses. Magenta by default because nothing
    /// in a real material is that colour, so it cannot be mistaken for the model.</summary>
    public Vector3 BackfaceColor { get; init; } = new(1f, 0f, 0.85f);

    /// <summary>Create the defaults.</summary>
    public RenderSettings() { }
}
