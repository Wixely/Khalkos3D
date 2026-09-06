using System.Numerics;

namespace Khalkos3D;

/// <summary>
/// What the model is standing in — the light arriving from every direction that is not the lamp.
///
/// <para><b>Without this a metal renders black, and that is not a stylistic problem.</b> A metallic
/// surface has no diffuse colour at all: physically, everything you see on chrome is a reflection of
/// its surroundings. Give it nothing to reflect and it correctly renders near-black, which every user
/// reads as a broken shader rather than as an empty room.</para>
///
/// <para><b>A three-colour gradient, not an image.</b> Sky overhead, a horizon band, ground below,
/// interpolated by direction. That is a crude approximation of a real prefiltered environment map —
/// and it is the right one here, because it needs no asset to ship, no HDR decoder, no cubemap
/// precomputation and no per-platform texture format. It is identical on all six targets and costs a
/// few instructions. A real IBL is a later upgrade that slots into the same place; this is what makes
/// materials look like materials in the meantime.</para>
/// </summary>
public readonly record struct Environment
{
    /// <summary>A neutral studio: cool light from above, warm bounce from below. The defaults exist
    /// because "pick three colours" is not a reasonable thing to ask of someone who wants to look at
    /// an STL.</summary>
    public static Environment Studio { get; } = new();

    /// <summary>Colour arriving from directly overhead, linear RGB.</summary>
    public Vector3 Sky { get; init; } = new(0.42f, 0.47f, 0.56f);

    /// <summary>Colour arriving from the horizon — the band the eye reads as "the room".</summary>
    public Vector3 Horizon { get; init; } = new(0.28f, 0.29f, 0.31f);

    /// <summary>Colour bouncing up from below. Warmer and darker than the sky, which is what a floor
    /// does and what stops a model looking like it is floating in a void.</summary>
    public Vector3 Ground { get; init; } = new(0.16f, 0.14f, 0.12f);

    /// <summary>Overall brightness. 0 turns the environment off entirely, which is worth having for
    /// a technical view where a flat unlit read matters more than a plausible one.</summary>
    public float Intensity { get; init; } = 1f;

    /// <summary>Create a studio environment.</summary>
    public Environment() { }

    /// <summary>A single flat colour from every direction — the old behaviour, and occasionally what
    /// a diagram wants.</summary>
    public static Environment Flat(Vector3 colour) =>
        new() { Sky = colour, Horizon = colour, Ground = colour };

    /// <summary>
    /// The colour arriving from a direction, as the shader computes it. Public so a caller can match
    /// a background to the environment — a model lit by a room it is visibly not standing in is the
    /// giveaway that makes a render look pasted on.
    /// </summary>
    /// <param name="direction">Unit direction to look along.</param>
    /// <param name="up">Which axis is up, so the gradient runs the right way for the file.</param>
    public Vector3 Sample(Vector3 direction, UpAxis up)
    {
        var height = up == UpAxis.Z ? direction.Z : direction.Y;
        // Squared blend towards the poles rather than linear: a linear ramp puts the horizon colour
        // in a narrow band and reads as two flat halves with a seam, which is exactly what a gradient
        // is meant to avoid.
        var t = Math.Clamp(height, -1f, 1f);
        var colour = t >= 0f
            ? Vector3.Lerp(Horizon, Sky, t * t)
            : Vector3.Lerp(Horizon, Ground, t * t);
        return colour * MathF.Max(Intensity, 0f);
    }
}
