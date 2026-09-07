using System.Numerics;
using Xunit;

namespace Khalkos3D.Tests;

/// <summary>
/// What a light is, on a machine with no GPU: the shapes, the defaults, and the compatibility
/// promise that a caller who never sets <see cref="RenderSettings.Lights"/> is lit exactly as before.
/// </summary>
public class LightTests
{
    [Fact]
    public void A_directional_light_carries_the_direction_it_travels()
    {
        var light = Light.Directional(new Vector3(0f, -1f, 0f), new Vector3(2f, 2f, 2f));

        Assert.Equal(LightKind.Directional, light.Kind);
        Assert.Equal(new Vector3(0f, -1f, 0f), light.Vector);
        Assert.Equal(new Vector3(2f), light.Color);
    }

    [Fact]
    public void A_point_light_carries_where_it_is_and_how_far_it_reaches()
    {
        var light = Light.Point(new Vector3(1f, 2f, 3f), new Vector3(5f), range: 8f);

        Assert.Equal(LightKind.Point, light.Kind);
        Assert.Equal(new Vector3(1f, 2f, 3f), light.Vector);
        Assert.Equal(8f, light.Range);
    }

    [Fact]
    public void A_point_light_cannot_have_a_range_of_zero()
    {
        // Zero is what marks a light directional once it reaches the shader, so a point light must
        // never carry it — a lamp with no reach would silently become a lamp at infinity.
        Assert.True(Light.Point(Vector3.Zero, Vector3.One, range: 0f).Range > 0f);
        Assert.True(Light.Point(Vector3.Zero, Vector3.One, range: -5f).Range > 0f);
    }

    [Fact]
    public void Intensity_lives_in_the_colour_rather_than_beside_it()
    {
        // One number, not two that can disagree about which of them made the lamp black.
        var dim = Light.Directional(-Vector3.UnitY, new Vector3(0.5f));
        var bright = Light.Directional(-Vector3.UnitY, new Vector3(5f));

        Assert.True(bright.Color.X > dim.Color.X);
    }

    [Fact]
    public void A_frame_carries_no_lights_until_someone_says_otherwise()
    {
        // Which means the key light. Every caller written before this existed keeps the frame it had.
        Assert.Empty(RenderSettings.Default.Lights);
        Assert.Equal(new Vector3(2.6f), RenderSettings.Default.LightColor);
    }

    [Fact]
    public void The_maximum_is_a_documented_constant_rather_than_a_number_in_the_shader()
    {
        // The GLSL declares its arrays from this, so the two cannot disagree — and a renderer pushing
        // more lights than the shader declared would drop them with nothing to show for it.
        Assert.Equal(8, Light.Max);
    }
}
