using System.Numerics;
using Xunit;

namespace Khalkos3D.Tests;

/// <summary>
/// Camera framing, which needs no GPU to be wrong. Every one of these is a way a viewer opens a file
/// and shows the user something unhelpful.
/// </summary>
public class CameraTests
{
    private static BoundingBox Box(float x, float y, float z) =>
        new(new Vector3(-x / 2, -y / 2, -z / 2), new Vector3(x / 2, y / 2, z / 2));

    /// <summary>Is every corner of the box inside the view frustum, in clip space?</summary>
    private static bool Contains(Camera camera, BoundingBox bounds, float aspect = 1f)
    {
        var vp = camera.ViewProjection(aspect);
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);

            var clip = Vector4.Transform(new Vector4(corner, 1f), vp);
            if (clip.W <= 0f) return false;                                   // behind the eye
            var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
            if (MathF.Abs(ndc.X) > 1f || MathF.Abs(ndc.Y) > 1f) return false; // off the sides
            if (ndc.Z < -1f || ndc.Z > 1f) return false;                      // clipped near or far
        }
        return true;
    }

    [Theory]
    [InlineData(UpAxis.Y)]
    [InlineData(UpAxis.Z)]
    public void A_framed_model_is_visible_from_every_angle(UpAxis up)
    {
        // The reason Frame fits the bounding SPHERE and not the box: a box fitted at one angle pushes
        // a corner off screen at another, and it happens at exactly the angles a user drags to.
        var bounds = Box(30, 4, 12);

        for (var yaw = 0f; yaw < MathF.PI * 2f; yaw += 0.3f)
            foreach (var pitch in new[] { -1.2f, -0.4f, 0f, 0.4f, 1.2f })
            {
                var camera = Camera.Frame(bounds, up, yaw, pitch);
                Assert.True(Contains(camera, bounds),
                    $"{up}-up at yaw {yaw:0.00} pitch {pitch:0.00} cuts off part of the model");
            }
    }

    [Theory]
    [InlineData(0.35f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(2.5f)]
    public void A_framed_model_is_visible_at_any_aspect_ratio(float aspect)
    {
        // A PORTRAIT viewport is the case that catches this, and it caught it: a perspective
        // projection is specified vertically, so an aspect below 1 has a narrower HORIZONTAL field
        // and a vertical-only fit cuts the model off at the sides. Every phone held upright.
        var bounds = Box(20, 20, 20);
        var camera = Camera.Frame(bounds, UpAxis.Z, 0.6f, 0.5f, aspect: aspect);
        Assert.True(Contains(camera, bounds, aspect), $"aspect {aspect} clips the model");
    }

    [Fact]
    public void Framing_scales_with_the_model_rather_than_using_fixed_clip_planes()
    {
        // A fixed near/far pair cannot serve a 5 mm printed clip and a 300 m terrain at once: too
        // near and depth precision collapses into z-fighting, too far and the front is clipped away.
        var small = Camera.Frame(Box(0.005f, 0.005f, 0.005f), UpAxis.Z);
        var large = Camera.Frame(Box(300f, 300f, 300f), UpAxis.Z);

        Assert.True(small.Near < large.Near, "the near plane did not follow the model's size");
        Assert.True(small.Far < large.Far);
        // The ratio is what actually governs depth precision, and it should be roughly the same for
        // both — that is the whole point of deriving them.
        var smallRatio = small.Far / small.Near;
        var largeRatio = large.Far / large.Near;
        Assert.True(MathF.Abs(smallRatio - largeRatio) / largeRatio < 0.05f,
            $"depth precision differs by model scale: {smallRatio:0.0} against {largeRatio:0.0}");
    }

    [Fact]
    public void An_empty_scene_produces_a_usable_camera_rather_than_a_nan()
    {
        // A file that parsed but contained nothing must not take the viewer down with a NaN matrix.
        var camera = Camera.Frame(BoundingBox.Empty, UpAxis.Z);

        Assert.Equal(Vector3.Zero, camera.Target);
        Assert.True(camera.Distance > 0f);
        Assert.True(float.IsFinite(camera.Near) && camera.Near > 0f);
        foreach (var value in new[] { camera.Position.X, camera.Position.Y, camera.Position.Z })
            Assert.False(float.IsNaN(value));
    }

    [Fact]
    public void The_up_axis_changes_which_way_the_model_stands()
    {
        // Not cosmetic: an STL is Z-up by universal CAD convention, so a viewer that assumed Y-up
        // lays every printed part on its side.
        var bounds = Box(10, 10, 10);
        var y = Camera.Frame(bounds, UpAxis.Y);
        var z = Camera.Frame(bounds, UpAxis.Z);

        Assert.Equal(Vector3.UnitY, y.Up);
        Assert.Equal(Vector3.UnitZ, z.Up);
        Assert.NotEqual(y.Position, z.Position);
    }

    [Fact]
    public void Orbiting_keeps_the_target_and_the_distance()
    {
        var camera = Camera.Frame(Box(10, 10, 10), UpAxis.Z);
        var distance = camera.Distance;

        for (var i = 0; i < 40; i++) camera = camera.Orbit(0.2f, 0.1f, UpAxis.Z);

        Assert.Equal(Vector3.Zero, camera.Target);
        // Drift here would mean the model creeps towards or away from the viewer as they drag, which
        // is the kind of thing that feels wrong long before anyone can say why.
        Assert.Equal(distance, camera.Distance, 2);
    }

    [Fact]
    public void Orbiting_past_vertical_is_clamped_rather_than_flipping()
    {
        // At exactly straight down the view direction is parallel to up, the view matrix is
        // degenerate, and the model spins on its own axis. Clamping just short is the standard fix
        // and is worth pinning, because the failure looks like a physics bug rather than a maths one.
        var camera = Camera.Frame(Box(10, 10, 10), UpAxis.Z);
        for (var i = 0; i < 50; i++) camera = camera.Orbit(0f, 0.5f, UpAxis.Z);

        // What matters is that it SETTLES rather than flips: a pitch that ran past vertical would
        // put the camera underneath the model, and the view would appear to snap upside down mid-drag.
        var offset = Vector3.Normalize(camera.Position - camera.Target);
        Assert.True(offset.Z > 0f, "orbiting up ran past vertical and put the camera below the model");
        Assert.True(offset.Z < 1f, "the camera reached exactly vertical, where the view has no orientation");
        foreach (var value in new[] { offset.X, offset.Y, offset.Z })
            Assert.False(float.IsNaN(value), "the clamped camera produced a NaN direction");
        Assert.True(Contains(camera, Box(10, 10, 10)), "the clamped camera lost the model");
    }

    [Fact]
    public void Zooming_moves_closer_and_keeps_the_clip_planes_sensible()
    {
        var camera = Camera.Frame(Box(10, 10, 10), UpAxis.Z);
        var before = camera.Distance;

        var closer = camera.Zoom(0.5f);
        Assert.True(closer.Distance < before);
        Assert.True(closer.Near > 0f && closer.Near < closer.Far);

        // Nonsense in, unchanged out — rather than a camera at NaN that renders nothing and reports
        // no reason.
        Assert.Equal(camera, camera.Zoom(0f));
        Assert.Equal(camera, camera.Zoom(float.NaN));
    }
}
