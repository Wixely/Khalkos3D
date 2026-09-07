using System.Numerics;
using Khalkos3D.Gl;
using Xunit;
using Xunit.Abstractions;

namespace Khalkos3D.RenderTests;

/// <summary>
/// Caller-supplied GLSL, compiled and drawn by a real driver.
///
/// <para><b>The other half of this feature is tested without a GPU</b>, in Khalkos3D.Tests: which
/// source is refused for not being portable is a text question, and answering it on whatever driver
/// happens to be present would be answering the wrong one. What is left for here is what only a
/// driver can settle — that the spliced program compiles, that it draws what the source says, that a
/// custom material still answers the debug views, and that a shader which fails leaves the geometry
/// on screen instead of taking the frame with it.</para>
/// </summary>
[Collection(GlCollection.Name)]
public class ShaderTests(GlFixture gl, ITestOutputHelper output)
{
    private const int Size = 96;
    private static readonly Vector4 Backdrop = new(0f, 0f, 0f, 1f);

    /// <summary>Paint the surface flat red, whatever the material said.</summary>
    private const string Red = """
        void surface(inout Surface s) {
            s.baseColor = vec4(1.0, 0.0, 0.0, 1.0);
            s.metallic = 0.0;
            s.roughness = 1.0;
        }
        """;

    /// <summary>The same, in a colour the caller passes in.</summary>
    private const string Tinted = """
        uniform vec3 uTint;
        void surface(inout Surface s) {
            s.baseColor = vec4(uTint, 1.0);
            s.metallic = 0.0;
            s.roughness = 1.0;
        }
        """;

    private static Scene Box(Material? material = null) =>
        Scene.FromMesh(MeshWelder.FromTriangleSoup(TestBox.Corners(new Vector3(2f)), out _),
                       material, up: UpAxis.Z);

    // ---- it draws ----------------------------------------------------------------------------

    [Fact]
    public void A_surface_shader_replaces_what_the_material_said()
    {
        var plain = new Material { BaseColor = new Vector4(0.1f, 0.1f, 0.9f, 1f), Roughness = 0.5f };
        var shaded = new Material { BaseColor = plain.BaseColor, Roughness = 0.5f, Shader = Shader.Surface(Red, name: "red") };

        var before = Render(Box(plain));
        var after = Render(Box(shaded), out var renderer);
        using (renderer) output.WriteLine(renderer.Prepare(shaded.Shader!).Summary ?? "no notes");

        var lit = Centre(after);
        output.WriteLine($"centre before {Centre(before)}, after {lit}");

        Assert.True(lit.R > 100, "the surface function should have made this red");
        Assert.True(lit.R > lit.B * 2, "and red rather than the blue the material asked for");
        Assert.NotEqual(Centre(before), lit);
    }

    [Fact]
    public void A_vertex_hook_moves_the_geometry()
    {
        // Twice the size in object space, which no material property could do — so a change here is
        // evidence the vertex half of the splice reached the driver.
        var bigger = Shader.Surface(
            "void surface(inout Surface s) {}",
            vertex: "void vertex(inout Vertex v) { v.position *= 2.0; }",
            name: "inflate");

        var plain = Coverage(Render(Box(new Material { Roughness = 0.5f })));
        var moved = Coverage(Render(Box(new Material { Roughness = 0.5f, Shader = bigger })));

        output.WriteLine($"covered {plain:P1} then {moved:P1}");
        Assert.True(moved > plain * 1.5f, $"the displaced box should cover far more of the frame ({plain:P1} -> {moved:P1})");
    }

    [Fact]
    public void A_whole_program_draws_what_it_says_and_nothing_the_engine_would_have_added()
    {
        // No lighting, no tone mapping, no environment: the output is the constant this source
        // writes, which is how a test can tell the engine got out of the way completely.
        var flat = Shader.Program(
            vertex: """
                in vec3 aPos;
                uniform mat4 uMvp;
                void main() { gl_Position = uMvp * vec4(aPos, 1.0); }
                """,
            fragment: """
                out vec4 fragColor;
                void main() { fragColor = vec4(0.0, 1.0, 0.0, 1.0); }
                """,
            name: "flat green");

        var frame = Render(Box(new Material { Shader = flat }), out var renderer);
        using (renderer) Assert.True(renderer.Prepare(flat).Ok, renderer.Prepare(flat).Error);

        var centre = Centre(frame);
        output.WriteLine($"centre {centre}");
        Assert.Equal(0, centre.R);
        Assert.Equal(255, centre.G);
        Assert.Equal(0, centre.B);
    }

    [Fact]
    public void A_caller_uniform_reaches_the_shader()
    {
        var shader = Shader.Surface(Tinted, name: "tinted");
        var green = new Material { Shader = shader, ShaderValues = new Dictionary<string, ShaderValue> { ["uTint"] = new Vector3(0f, 1f, 0f) } };
        var blue = new Material { Shader = shader, ShaderValues = new Dictionary<string, ShaderValue> { ["uTint"] = new Vector3(0f, 0f, 1f) } };

        var first = Centre(Render(Box(green)));
        var second = Centre(Render(Box(blue)));

        output.WriteLine($"green material {first}, blue material {second}");
        Assert.True(first.G > first.B, "the material asking for green should be green");
        Assert.True(second.B > second.G, "and the one asking for blue should be blue");
    }

    [Fact]
    public void One_shader_shared_by_two_materials_is_compiled_once_and_can_be_dropped()
    {
        var shader = Shader.Surface(Tinted, name: "tinted");
        var scene = TwoBoxes(
            new Material { Shader = shader, ShaderValues = new Dictionary<string, ShaderValue> { ["uTint"] = Vector3.UnitX } },
            new Material { Shader = shader, ShaderValues = new Dictionary<string, ShaderValue> { ["uTint"] = Vector3.UnitY } });

        Render(scene, out var renderer);
        using (renderer)
        {
            Assert.Equal(1, renderer.CachedShaders);

            renderer.Forget(shader);
            Assert.Equal(0, renderer.CachedShaders);

            // And it rebuilds, because forgetting is what an editor does after a keystroke.
            Assert.True(renderer.Prepare(shader).Ok);
            Assert.Equal(1, renderer.CachedShaders);
        }
    }

    [Fact]
    public void Two_programs_in_one_scene_each_get_the_frame_they_are_drawn_in()
    {
        // Uniforms belong to a program, not to the context. If the camera and the lights were pushed
        // once per frame instead of once per program, whichever program was not the last one set up
        // would draw with an identity camera — which is a black frame, or a box behind the viewer.
        var scene = TwoBoxes(new Material { BaseColor = new Vector4(0.9f, 0.9f, 0.9f, 1f), Roughness = 0.4f },
                             new Material { Shader = Shader.Surface(Red, name: "red") });

        var frame = Render(scene);
        var (red, neutral) = Populations(frame);

        output.WriteLine($"{red} red pixels from the custom program, {neutral} neutral lit ones from the built-in");
        Assert.True(neutral > 50, "the built-in material should still be lit, not black");
        Assert.True(red > 50, "and the custom one should be red");
    }

    [Fact]
    public void A_custom_surface_still_answers_the_debug_views()
    {
        // The engine keeps the program around a surface hook precisely so this remains true: a shader
        // cannot cost a user the views they diagnose their own model with.
        var settings = new RenderSettings { ClearColor = Backdrop, Up = UpAxis.Z, ShowNormals = true };
        var shaded = Render(Box(new Material { Shader = Shader.Surface(Red, name: "red") }), settings);
        var plain = Render(Box(new Material()), settings);

        // Identical, not merely "not red": a surface hook changes the material and the normals view
        // shows the geometry, so the two have no business differing by a single pixel.
        output.WriteLine($"shaded {Centre(shaded)}, plain {Centre(plain)}");
        Assert.Equal(Centre(plain), Centre(shaded));
    }

    [Fact]
    public void The_surface_sees_object_space_as_well_as_world_space()
    {
        // Both boxes sit entirely on the positive side of the world's X, so a shader reading world
        // space paints them wholly red. Reading OBJECT space, each is half green and half red about
        // its own middle — which is the difference that lets a procedural pattern stay on a thing
        // while it moves rather than sliding across it.
        var split = Shader.Surface("""
            void surface(inout Surface s) {
                s.baseColor.rgb = s.object.x < 0.0 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
            }
            """, name: "split");

        var material = new Material { Shader = split, Unlit = true };
        var mesh = MeshWelder.FromTriangleSoup(TestBox.Corners(new Vector3(1.6f)), out _);
        var scene = new Scene
        {
            Meshes = [mesh],
            Materials = [material],
            Up = UpAxis.Z,
            Roots =
            [
                new Node { Mesh = 0, Material = 0, Transform = Matrix4x4.CreateTranslation(1.4f, 0f, 0f) },
                new Node { Mesh = 0, Material = 0, Transform = Matrix4x4.CreateTranslation(4.2f, 0f, 0f) },
            ],
        };

        var frame = Render(scene);
        int red = 0, green = 0;
        for (var i = 0; i < frame.Length; i += 4)
        {
            if (frame[i] > 150 && frame[i + 1] < 80) red++;
            else if (frame[i + 1] > 150 && frame[i] < 80) green++;
        }

        output.WriteLine($"{red} red, {green} green");
        Assert.True(green > red * 0.3, $"object space should halve each box; world space would leave no green ({green} vs {red})");
    }

    // ---- it fails safely ---------------------------------------------------------------------

    [Fact]
    public void A_shader_the_driver_rejects_is_reported_and_the_geometry_still_draws()
    {
        var broken = Shader.Surface("void surface(inout Surface s) { s.baseColor = notAThing; }", name: "broken");
        var material = new Material { BaseColor = new Vector4(0.1f, 0.1f, 0.9f, 1f), Roughness = 0.5f, Shader = broken };

        var frame = Render(Box(material), out var renderer);
        using (renderer)
        {
            var report = renderer.Prepare(broken);
            output.WriteLine(report.Error);

            Assert.False(report.Ok);
            Assert.Contains("broken", report.Error!);          // the shader's name, so it can be found
            Assert.Contains("shader", report.Summary!);
        }

        // Drawn with the built-in program rather than not drawn: the geometry a user asked to see is
        // still there, and the reason it is the wrong colour is in the report.
        var plain = Render(Box(new Material { BaseColor = new Vector4(0.1f, 0.1f, 0.9f, 1f), Roughness = 0.5f }));
        Assert.Equal(Centre(plain), Centre(frame));
    }

    [Fact]
    public void Source_that_would_not_run_everywhere_never_reaches_the_driver()
    {
        // This one compiles perfectly well on a desktop, which is the entire problem with letting the
        // driver be the judge.
        var desktopOnly = Shader.Surface("""
            #version 330 core
            void surface(inout Surface s) { s.baseColor = vec4(1.0); }
            """, name: "desktop-only");

        Render(Box(new Material()), out var renderer);
        using (renderer)
        {
            var report = renderer.Prepare(desktopOnly);
            output.WriteLine(report.Error);

            Assert.False(report.Ok);
            Assert.Contains("version directive", report.Error!);
        }
    }

    [Fact]
    public void A_failed_shader_is_compiled_once_rather_than_every_frame()
    {
        var broken = Shader.Surface("void surface(inout Surface s) { s.baseColor = notAThing; }", name: "broken");
        var scene = Box(new Material { Shader = broken });

        RequireGl();
        var renderer = GlRenderer.Create(gl.GetProcAddress!, out _)!;
        using (renderer)
        using (var target = GlOffscreenTarget.Create(gl.GetProcAddress!, Size, Size, out _)!)
        {
            target.Bind();
            for (var i = 0; i < 3; i++)
                renderer.Draw(scene, Camera.Frame(scene.Bounds, scene.Up), Size, Size,
                              new RenderSettings { ClearColor = Backdrop, Up = UpAxis.Z });
            target.Unbind();

            Assert.Equal(1, renderer.CachedShaders);
            Assert.False(renderer.Prepare(broken).Ok);
        }
    }

    // ---- the harness -------------------------------------------------------------------------

    private static Scene TwoBoxes(Material left, Material right)
    {
        var mesh = MeshWelder.FromTriangleSoup(TestBox.Corners(new Vector3(1.4f)), out _);
        return new Scene
        {
            Meshes = [mesh],
            Materials = [left, right],
            Up = UpAxis.Z,
            Roots =
            [
                new Node { Mesh = 0, Material = 0, Transform = Matrix4x4.CreateTranslation(-1.4f, 0f, 0f) },
                new Node { Mesh = 0, Material = 1, Transform = Matrix4x4.CreateTranslation(1.4f, 0f, 0f) },
            ],
        };
    }

    /// <summary>Render and clean up: for a test that only wants the pixels.</summary>
    private byte[] Render(Scene scene, RenderSettings? settings = null)
    {
        var pixels = Render(scene, out var renderer, settings);
        renderer.Dispose();
        return pixels;
    }

    /// <summary>Render and hand back the renderer, for a test that wants to ask it what happened.
    /// The caller disposes it, with the context still current.</summary>
    private byte[] Render(Scene scene, out GlRenderer renderer, RenderSettings? settings = null)
    {
        RequireGl();

        renderer = GlRenderer.Create(gl.GetProcAddress!, out var error)
            ?? throw new InvalidOperationException($"the renderer would not start: {error}");

        using var target = GlOffscreenTarget.Create(gl.GetProcAddress!, Size, Size, out var targetError)
            ?? throw new InvalidOperationException($"no offscreen target: {targetError}");

        target.Bind();
        // Straight down the up axis's opposite, far enough out that the whole box is in frame.
        renderer.Draw(scene, Camera.Frame(scene.Bounds, scene.Up), Size, Size,
                      settings ?? new RenderSettings { ClearColor = Backdrop, Up = scene.Up });
        var pixels = target.ReadPixels();
        target.Unbind();
        return pixels;
    }

    private void RequireGl() => Assert.True(gl.GetProcAddress is not null,
        $"these tests need a GL context and there is none: {gl.Unavailable}. " +
        "On a headless Linux machine run them under xvfb-run.");

    private static (int R, int G, int B) Centre(byte[] pixels) => Patch(pixels, 0.5f);

    /// <summary>The mean colour of a small patch at a fraction across the frame, vertically centred.
    /// A mean rather than one pixel: a single sample lands on an edge often enough to be flaky.</summary>
    private static (int R, int G, int B) Patch(byte[] pixels, float across)
    {
        var cx = (int)(Size * across);
        var cy = Size / 2;
        int r = 0, g = 0, b = 0, n = 0;
        for (var y = cy - 4; y <= cy + 4; y++)
            for (var x = cx - 4; x <= cx + 4; x++)
            {
                var i = (y * Size + x) * 4;
                r += pixels[i]; g += pixels[i + 1]; b += pixels[i + 2]; n++;
            }
        return (r / n, g / n, b / n);
    }

    /// <summary>How many pixels read as the custom program's red, and how many as ordinary lit
    /// geometry. Counted over the whole frame rather than sampled at a position, because which box
    /// lands on which side of the picture is the camera's business and not this test's.</summary>
    private static (int Red, int Neutral) Populations(byte[] pixels)
    {
        int red = 0, neutral = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            int r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
            if (r > 80 && r > g * 2 && r > b * 2) red++;
            else if (r > 30 && g > 30 && b > 30) neutral++;
        }
        return (red, neutral);
    }

    /// <summary>Fraction of the frame that is not the backdrop.</summary>
    private static float Coverage(byte[] pixels)
    {
        var lit = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 12 || pixels[i + 1] > 12 || pixels[i + 2] > 12) lit++;
        return (float)lit / (Size * Size);
    }
}
