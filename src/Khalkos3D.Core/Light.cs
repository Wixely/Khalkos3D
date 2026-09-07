using System.Numerics;

namespace Khalkos3D;

/// <summary>What a light is, which decides how its direction and its falloff are read.</summary>
public enum LightKind
{
    /// <summary>Infinitely far away, so every point is lit from the same direction and nothing gets
    /// dimmer with distance. The sun, and the right default for looking at an object.</summary>
    Directional,

    /// <summary>A position in the scene, radiating in every direction and falling off with distance.
    /// A lamp, a flame, a glowing part.</summary>
    Point,
}

/// <summary>
/// One light.
///
/// <para><b>A struct with both shapes in it rather than a type hierarchy</b>, because these live in a
/// fixed array that is pushed to the GPU every frame. A polymorphic light would be a pointer chase
/// per light per frame to fill a buffer that is flat anyway, and the shader has to branch on the kind
/// regardless.</para>
///
/// <para><b>Intensity lives in <see cref="Color"/>.</b> A separate intensity multiplied by a colour
/// is the same number twice, and it invites the bug where a light is black and nobody can tell which
/// of the two is zero. Values above 1 are simply a brighter lamp — the frame is tone mapped, so that
/// is meaningful rather than clipped.</para>
/// </summary>
public readonly record struct Light
{
    /// <summary>
    /// How many lights a frame may carry. Fixed, because the shader declares its arrays at compile
    /// time and a per-scene recompile would be a worse trade than an upper bound.
    ///
    /// <para>Eight is the conventional size for a forward renderer and more than a viewer has ever
    /// needed. The cost of the ones you do not use is only the uniform slots they reserve — the
    /// shader loops to <see cref="RenderSettings.Lights"/>'s count, not to this — so a scene with two
    /// lights does two lights' work.</para>
    /// </summary>
    public const int Max = 8;

    /// <summary>Which shape this is.</summary>
    public LightKind Kind { get; init; }

    /// <summary>
    /// For <see cref="LightKind.Directional"/>, the direction the light TRAVELS — pointing from the
    /// lamp towards the scene, the same convention as <see cref="RenderSettings.LightDirection"/>.
    /// For <see cref="LightKind.Point"/>, the position it radiates from, in world space.
    ///
    /// <para>One field for both because it is one register on the GPU either way, and because the
    /// two are never both meaningful.</para>
    /// </summary>
    public Vector3 Vector { get; init; }

    /// <summary>Colour and intensity together, linear RGB.</summary>
    public Vector3 Color { get; init; } = Vector3.One;

    /// <summary>
    /// How far a point light reaches, in scene units. Ignored by a directional light.
    ///
    /// <para><b>A range rather than pure inverse-square</b>, because inverse-square never actually
    /// reaches zero: every light would touch every fragment in the scene forever, which costs the
    /// same as a light you can see and looks like a faint wash nobody asked for. The falloff is
    /// windowed so it is inverse-square where it matters and exactly nothing at the edge.</para>
    /// </summary>
    public float Range { get; init; } = 1f;

    /// <summary>Create a light. Prefer <see cref="Directional"/> or <see cref="Point"/>.</summary>
    public Light() { }

    /// <summary>A light from infinitely far away, travelling in <paramref name="direction"/>.</summary>
    public static Light Directional(Vector3 direction, Vector3 color) =>
        new() { Kind = LightKind.Directional, Vector = direction, Color = color };

    /// <summary>A lamp at <paramref name="position"/>, reaching <paramref name="range"/> units.</summary>
    public static Light Point(Vector3 position, Vector3 color, float range) =>
        new() { Kind = LightKind.Point, Vector = position, Color = color, Range = MathF.Max(range, 1e-4f) };
}
