using System.Numerics;
using Khalkos3D.Gl;
using Xunit;
using Xunit.Abstractions;

namespace Khalkos3D.RenderTests;

/// <summary>
/// More than one light, against a real driver.
///
/// <para>The claims worth pinning are the ones a single light could not make: that the shader sums
/// what it is given rather than lighting from the last one set, that a point light falls off and
/// stops, that the key light still behaves exactly as it did for callers who never heard of any of
/// this, and that going past the maximum drops the extras instead of doing something inventive.</para>
/// </summary>
[Collection(GlCollection.Name)]
public class LightTests(GlFixture gl, ITestOutputHelper output)
{
    private const int Size = 96;
    private static readonly Vector4 Backdrop = new(0f, 0f, 0f, 1f);

    private static Scene Box() =>
        Scene.FromMesh(MeshWelder.FromTriangleSoup(TestBox.Corners(new Vector3(2f)), out _),
                       new Material { BaseColor = new Vector4(0.8f, 0.8f, 0.8f, 1f), Roughness = 0.6f },
                       up: UpAxis.Z);

    /// <summary>No environment, so what is measured is the lights and only the lights.</summary>
    private static RenderSettings Dark(params Light[] lights) => new()
    {
        ClearColor = Backdrop,
        Up = UpAxis.Z,
        Environment = Khalkos3D.Environment.Flat(Vector3.Zero),
        Lights = lights,
    };

    [Fact]
    public void Two_lights_are_brighter_than_either_of_them()
    {
        var left = Light.Directional(new Vector3(1f, 0f, -0.3f), new Vector3(2.5f));
        var right = Light.Directional(new Vector3(-1f, 0f, -0.3f), new Vector3(2.5f));

        var one = Lit(Render(Box(), Dark(left)));
        var other = Lit(Render(Box(), Dark(right)));
        var both = Lit(Render(Box(), Dark(left, right)));

        output.WriteLine($"left {one:0.0}, right {other:0.0}, both {both:0.0}");
        Assert.True(both > one + 1f, $"two lights should beat one ({both:0.0} vs {one:0.0})");
        Assert.True(both > other + 1f, $"two lights should beat the other one ({both:0.0} vs {other:0.0})");
    }

    [Fact]
    public void A_light_from_the_other_side_lights_the_side_the_first_one_missed()
    {
        // The sum is not the point here; reaching a surface the first light never touched is. A
        // renderer that kept only the last light set would pass the brightness test above and fail
        // this one — so the two lights go straight down an axis each, leaving the other's face at
        // exactly zero. Angled lights would both touch every visible face a little and prove nothing.
        var key = Light.Directional(new Vector3(-1f, 0f, 0f), new Vector3(2.5f));
        var fill = Light.Directional(new Vector3(0f, -1f, 0f), new Vector3(2.5f));

        var alone = Render(Box(), Dark(key));
        var pair = Render(Box(), Dark(key, fill));

        // Count pixels that were essentially black under the key light and are lit under both.
        var rescued = 0;
        for (var i = 0; i < alone.Length; i += 4)
            if (alone[i] < 12 && alone[i + 1] < 12 && pair[i] > 40) rescued++;

        output.WriteLine($"{rescued} pixels lit only by the second light");
        Assert.True(rescued > 100, $"the fill light should reach what the key light did not ({rescued} pixels)");
    }

    [Fact]
    public void A_point_light_gets_dimmer_with_distance()
    {
        var near = Light.Point(new Vector3(0f, 0f, 3.4f), new Vector3(14f), range: 40f);
        var far = Light.Point(new Vector3(0f, 0f, 9f), new Vector3(14f), range: 40f);

        var close = Lit(Render(Box(), Dark(near)));
        var away = Lit(Render(Box(), Dark(far)));

        output.WriteLine($"near {close:0.0}, far {away:0.0}");
        Assert.True(close > away * 1.5f, $"inverse square should show plainly ({close:0.0} vs {away:0.0})");
    }

    [Fact]
    public void A_point_light_stops_at_its_range()
    {
        // Not merely faint: nothing. A lamp whose falloff never reaches zero touches every fragment in
        // the scene forever, and costs the same as one you can see.
        var outOfReach = Light.Point(new Vector3(0f, 0f, 9f), new Vector3(30f), range: 4f);
        var black = Light.Point(new Vector3(0f, 0f, 9f), Vector3.Zero, range: 40f);

        Assert.Equal(Lit(Render(Box(), Dark(black))), Lit(Render(Box(), Dark(outOfReach))), 3);
    }

    [Fact]
    public void No_lights_listed_means_the_key_light_that_was_always_there()
    {
        // The compatibility claim, and the reason this change is invisible to callers who never set
        // Lights: the frame is identical, not merely similar.
        var settings = RenderSettings.Default with { ClearColor = Backdrop, Up = UpAxis.Z };
        var spelled = settings with
        {
            Lights = [Light.Directional(settings.LightDirection, settings.LightColor)],
        };

        Assert.Equal(Render(Box(), settings), Render(Box(), spelled));
    }

    [Fact]
    public void Past_the_maximum_the_extras_are_dropped_rather_than_wrapped()
    {
        // Eight black lights and four bright ones. If the extras were wrapping into the array, or
        // being blended in, the frame would be lit; if the first eight win, it stays dark.
        var lights = new List<Light>();
        for (var i = 0; i < Light.Max; i++) lights.Add(Light.Directional(new Vector3(0f, 0f, -1f), Vector3.Zero));
        for (var i = 0; i < 4; i++) lights.Add(Light.Directional(new Vector3(-1f, -0.3f, -0.4f), new Vector3(6f)));

        var overflowing = Lit(Render(Box(), Dark([.. lights])));
        var justTheFirst = Lit(Render(Box(), Dark([.. lights.Take(Light.Max)])));

        output.WriteLine($"{lights.Count} lights gave {overflowing:0.000}, the first {Light.Max} gave {justTheFirst:0.000}");
        Assert.Equal(justTheFirst, overflowing, 3);
        Assert.True(overflowing < 1f, "eight black lights should leave the box unlit");
    }

    [Fact]
    public void A_surface_shader_is_lit_by_all_of_them_without_knowing_they_exist()
    {
        // The whole argument for the surface layer holding the lighting: this shader was written
        // before the engine had more than one light, and needs no change to be lit by four.
        var shader = Shader.Surface(
            "void surface(inout Surface s) { s.baseColor.rgb = vec3(0.8, 0.8, 0.8); s.roughness = 0.6; }",
            name: "plain");
        var scene = Scene.FromMesh(MeshWelder.FromTriangleSoup(TestBox.Corners(new Vector3(2f)), out _),
                                   new Material { Shader = shader }, up: UpAxis.Z);

        var key = Light.Directional(new Vector3(-0.9f, -0.2f, -0.4f), new Vector3(2.5f));
        var fill = Light.Directional(new Vector3(0.9f, 0.2f, -0.4f), new Vector3(2.5f));

        var one = Lit(Render(scene, Dark(key)));
        var two = Lit(Render(scene, Dark(key, fill)));

        output.WriteLine($"custom material: one light {one:0.0}, two {two:0.0}");
        Assert.True(two > one + 1f, $"a custom surface should see both lights ({two:0.0} vs {one:0.0})");
    }

    // ---- the harness -------------------------------------------------------------------------

    private byte[] Render(Scene scene, RenderSettings settings)
    {
        Assert.True(gl.GetProcAddress is not null,
            $"these tests need a GL context and there is none: {gl.Unavailable}. " +
            "On a headless Linux machine run them under xvfb-run.");

        using var renderer = GlRenderer.Create(gl.GetProcAddress!, out var error)
            ?? throw new InvalidOperationException($"the renderer would not start: {error}");
        using var target = GlOffscreenTarget.Create(gl.GetProcAddress!, Size, Size, out var targetError)
            ?? throw new InvalidOperationException($"no offscreen target: {targetError}");

        target.Bind();
        renderer.Draw(scene, Camera.Frame(scene.Bounds, scene.Up), Size, Size, settings);
        var pixels = target.ReadPixels();
        target.Unbind();
        return pixels;
    }

    /// <summary>Mean brightness over the whole frame, which is enough to compare lighting setups that
    /// draw the same geometry from the same place.</summary>
    private static float Lit(byte[] pixels)
    {
        long total = 0;
        for (var i = 0; i < pixels.Length; i += 4) total += pixels[i] + pixels[i + 1] + pixels[i + 2];
        return (float)total / (pixels.Length / 4) / 3f;
    }
}
