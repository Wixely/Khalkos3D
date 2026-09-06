using System.Numerics;
using StannumFab.Gl;
using Xunit;
using Xunit.Abstractions;

namespace StannumFab.RenderTests;

/// <summary>
/// The renderer, against a real driver.
///
/// <para><b>These assert PROPERTIES rather than compare against reference images</b>, and that is a
/// deliberate first step rather than a shortcut. A committed reference PNG is exact and brittle: it
/// fails on any driver whose rasterisation rounds differently, which on this project's six targets
/// means it fails for reasons that are not defects. "The box occludes what is behind it", "the model
/// is inside the frame", "an inverted facet still shades" hold on every conforming driver, and each
/// one names a specific way rendering goes wrong.</para>
///
/// <para>Image comparison still belongs in the plan — it catches whole classes of subtle regression
/// these cannot. It needs a tolerance calibrated against more than one driver first.</para>
/// </summary>
[Collection(GlCollection.Name)]
public class RenderTests(GlFixture gl, ITestOutputHelper output)
{
    private const int Size = 128;
    private static readonly Vector4 Backdrop = new(0f, 0f, 0f, 1f);

    /// <summary>Render a scene and hand back the pixels, or skip when this machine has no GL.</summary>
    private Frame Render(Scene scene, Camera? camera = null, RenderSettings? settings = null)
    {
        RequireGl();

        var renderer = GlRenderer.Create(gl.GetProcAddress!, out var error);
        Assert.True(renderer is not null, $"the renderer would not start: {error}");
        using (renderer)
        {
            output.WriteLine($"driver: {renderer!.Renderer} — {renderer.Version} ({renderer.Dialect})");

            using var target = GlOffscreenTarget.Create(gl.GetProcAddress!, Size, Size, out var targetError);
            Assert.True(target is not null, $"no offscreen target: {targetError}");

            target!.Bind();
            var view = camera ?? Camera.Frame(scene.Bounds, scene.Up);
            renderer.Draw(scene, view, Size, Size,
                settings ?? new RenderSettings { ClearColor = Backdrop });
            var pixels = target.ReadPixels();
            target.Unbind();
            return new Frame(pixels, Size, Size);
        }
    }

    private static Scene Box(Vector3 size, Material? material = null) =>
        Scene.FromMesh(MeshWelder.FromTriangleSoup(TestBox.Corners(size), out _), material, up: UpAxis.Z);

    // ---- the basics -------------------------------------------------------------------------

    [Fact]
    public void A_box_is_drawn_and_is_not_the_background()
    {
        var frame = Render(Box(new Vector3(10, 10, 10)));
        var centre = frame.At(Size / 2, Size / 2);

        Assert.True(centre.Lit, $"the middle of the frame is still the backdrop ({centre}) — nothing drew");
        Assert.True(frame.LitFraction is > 0.15f and < 0.85f,
            $"{frame.LitFraction:P0} of the frame is covered; a framed box should fill much of it but not all");
    }

    [Fact]
    public void Framing_keeps_the_whole_model_inside_the_viewport()
    {
        // The property that makes Camera.Frame worth having: open any file, see all of it. Fitting
        // the bounding SPHERE rather than the box is what makes this hold at every orbit angle
        // rather than most of them.
        var scene = Box(new Vector3(30, 4, 12));

        foreach (var yaw in new[] { 0f, 0.7f, 1.6f, 2.4f, 3.9f, 5.5f })
        {
            var frame = Render(scene, Camera.Frame(scene.Bounds, scene.Up, yaw, pitch: 0.4f));
            Assert.True(frame.BorderIsClear,
                $"at yaw {yaw:0.0} the model touches the frame edge, so part of it is cut off");
            Assert.True(frame.At(Size / 2, Size / 2).Lit, $"at yaw {yaw:0.0} nothing is in the middle");
        }
    }

    [Fact]
    public void The_near_box_hides_the_far_one()
    {
        // Depth testing, asserted the only way that is meaningful: without it the draw order decides
        // what is visible, so the FAR box would win whenever it happened to be submitted second.
        var near = MeshWelder.FromTriangleSoup(TestBox.Corners(new Vector3(6, 6, 6)), out _);
        var far = MeshWelder.FromTriangleSoup(TestBox.Corners(new Vector3(6, 6, 6)), out _);

        var scene = new Scene
        {
            Meshes = [near, far],
            Materials = [Red, Green],
            Up = UpAxis.Z,
            Roots =
            [
                // The far one is drawn FIRST and is further away; the near one must still win.
                new Node { Mesh = 1, Material = 1, Transform = Matrix4x4.CreateTranslation(0, 40, 0) },
                new Node { Mesh = 0, Material = 0, Transform = Matrix4x4.CreateTranslation(0, -40, 0) },
            ],
        };

        // Looking down -Y so the two are directly one behind the other.
        var camera = new Camera(new Vector3(0, -120, 0), Vector3.Zero, Vector3.UnitZ,
                                Camera.DefaultFieldOfView, 1f, 400f);
        var centre = Render(scene, camera).At(Size / 2, Size / 2);

        Assert.True(centre.R > centre.G,
            $"the middle is {centre}, which is the FAR box — depth testing is not working");
    }

    [Fact]
    public void A_material_colour_reaches_the_screen()
    {
        var centre = Render(Box(new Vector3(10, 10, 10), Red)).At(Size / 2, Size / 2);

        Assert.True(centre.R > centre.G + 40 && centre.R > centre.B + 40,
            $"a red material rendered as {centre}");
    }

    [Fact]
    public void An_inverted_facet_still_shades_instead_of_going_black()
    {
        // Real STL files contain reversed triangles constantly and slicers print them fine, so a
        // viewer that renders them as black holes is reporting its own limitation as the user's bug.
        // The shader flips the normal towards the viewer for exactly this.
        var corners = TestBox.Corners(new Vector3(10, 10, 10));
        for (var i = 0; i + 2 < corners.Length; i += 3)
            (corners[i + 1], corners[i + 2]) = (corners[i + 2], corners[i + 1]);   // reverse every winding

        var scene = Scene.FromMesh(MeshWelder.FromTriangleSoup(corners, out _), Red, up: UpAxis.Z);
        var centre = Render(scene).At(Size / 2, Size / 2);

        Assert.True(centre.Lit, $"an inverted box rendered as {centre}");
        Assert.True(centre.R > centre.G + 40, $"an inverted box lost its colour: {centre}");
    }

    // ---- debug views ------------------------------------------------------------------------

    [Fact]
    public void The_normals_view_ignores_the_material_entirely()
    {
        // Asserted by COMPARING TWO MATERIALS rather than by inspecting a colour, and the first
        // attempt at this got it wrong in an instructive way: it checked that the result was not
        // red-dominant, but a surface facing +X encodes to (255,128,128), which is red-dominant and
        // entirely correct. The property that actually distinguishes a normals view is that the
        // material makes no difference to it.
        var debug = new RenderSettings { ClearColor = Backdrop, ShowNormals = true };
        var red = Render(Box(new Vector3(10, 10, 10), Red), settings: debug);
        var green = Render(Box(new Vector3(10, 10, 10), Green), settings: debug);

        var a = red.At(Size / 2, Size / 2);
        var b = green.At(Size / 2, Size / 2);
        Assert.True(a.Lit, $"the normals view rendered {a}");
        Assert.Equal(a, b);

        // And it is genuinely encoding a direction: a box shows several faces, so several distinct
        // colours, where a lit render of one flat colour would not.
        Assert.True(red.DistinctColors > 2,
            $"the normals view produced {red.DistinctColors} colours; it is not showing per-face directions");
    }

    [Fact]
    public void Wireframe_covers_far_less_of_the_frame_than_a_solid_does()
    {
        // glPolygonMode does not exist in GLES or WebGL2, so this is drawn from an edge index buffer
        // instead. Asserting coverage rather than exact pixels is what makes the check meaningful on
        // every driver: lines rasterise slightly differently everywhere.
        var scene = Box(new Vector3(10, 10, 10));
        var solid = Render(scene).LitFraction;
        var wire = Render(scene, settings: new RenderSettings { ClearColor = Backdrop, Wireframe = true }).LitFraction;

        output.WriteLine($"solid {solid:P1}, wireframe {wire:P1}");
        Assert.True(wire > 0.001f, "the wireframe drew nothing at all");
        Assert.True(wire < solid * 0.5f, $"wireframe covered {wire:P1} against solid {solid:P1} — it is not edges");
    }

    [Fact]
    public void An_unlit_material_shows_its_colour_exactly()
    {
        // The point of unlit: what goes in comes out. No lamp, no tone mapping, no gamma — because a
        // grid line's colour is a colour rather than a luminance to be compressed, and a shaded grid
        // is dim on one side of the model and bright on the other.
        var unlit = new Material { BaseColor = new Vector4(0.9f, 0.05f, 0.05f, 1f), Unlit = true };
        var centre = Render(Box(new Vector3(10, 10, 10), unlit)).At(Size / 2, Size / 2);

        Assert.InRange(centre.R, 227, 233);
        Assert.InRange(centre.G, 10, 16);
        Assert.InRange(centre.B, 10, 16);
    }

    [Fact]
    public void A_grid_renders_as_lines_in_its_own_colours()
    {
        // Scenery goes through the same upload, cache and draw call as a model — there is no private
        // "draw the grid" path to drift from the one that draws everything else.
        var grid = Shapes.Grid(50f, 10f, UpAxis.Z);
        var scene = Scene.FromMesh(grid, Shapes.LineMaterial, up: UpAxis.Z);
        var frame = Render(scene);

        Assert.True(frame.LitFraction > 0.005f, "the grid drew nothing");
        // Thin lines over a wide plane: a solid would cover far more.
        Assert.True(frame.LitFraction < 0.4f, $"the grid covered {frame.LitFraction:P0}, which is not lines");
        // Two line colours went in — minor and major — so more than one must come out.
        Assert.True(frame.DistinctColors > 1, "every grid line came out the same colour");
    }

    [Fact]
    public void Backfaces_are_highlighted_so_an_inverted_facet_can_be_found()
    {
        // The counterpart to the shader flipping normals towards the viewer. That flip is what stops
        // an inverted facet appearing as a black hole in a part the slicer would print fine — and it
        // also HIDES the inversion, so this is the switch that shows it.
        var corners = TestBox.Corners(new Vector3(10, 10, 10));
        for (var i = 0; i + 2 < corners.Length; i += 3)
            (corners[i + 1], corners[i + 2]) = (corners[i + 2], corners[i + 1]);
        var inverted = Scene.FromMesh(MeshWelder.FromTriangleSoup(corners, out _), Red, up: UpAxis.Z);
        var healthy = Box(new Vector3(10, 10, 10), Red);

        var debug = new RenderSettings { ClearColor = Backdrop, HighlightBackfaces = true };
        var flagged = Render(inverted, settings: debug).At(Size / 2, Size / 2);
        var fine = Render(healthy, settings: debug).At(Size / 2, Size / 2);

        // Magenta, which nothing in a real material is.
        Assert.True(flagged.R > 200 && flagged.G < 60 && flagged.B > 180,
            $"an inverted box was not flagged: {flagged}");
        Assert.False(fine.R > 200 && fine.G < 60 && fine.B > 180,
            $"a correctly wound box was flagged as inverted: {fine}");
    }

    // ---- resource handling ------------------------------------------------------------------

    [Fact]
    public void One_mesh_drawn_many_times_is_uploaded_once()
    {
        // The instancing claim, made concrete: a plate holding twenty copies of a bracket is twenty
        // transforms over one upload, because the scene graph already said they share a mesh.
        RequireGl();

        var mesh = MeshWelder.FromTriangleSoup(TestBox.Corners(new Vector3(4, 4, 4)), out _);
        var nodes = new List<Node>();
        for (var i = 0; i < 20; i++)
            nodes.Add(new Node { Mesh = 0, Transform = Matrix4x4.CreateTranslation(i * 6f, 0, 0) });
        var scene = new Scene { Meshes = [mesh], Roots = nodes, Up = UpAxis.Z };

        using var renderer = GlRenderer.Create(gl.GetProcAddress!, out var error);
        Assert.True(renderer is not null, error);
        using var target = GlOffscreenTarget.Create(gl.GetProcAddress!, Size, Size, out _);
        target!.Bind();

        renderer!.Draw(scene, Camera.Frame(scene.Bounds, scene.Up), Size, Size,
                       new RenderSettings { ClearColor = Backdrop });
        Assert.Equal(1, renderer.CachedMeshes);

        renderer.Draw(scene, Camera.Frame(scene.Bounds, scene.Up), Size, Size,
                      new RenderSettings { ClearColor = Backdrop });
        Assert.Equal(1, renderer.CachedMeshes);

        renderer.Forget(mesh);
        Assert.Equal(0, renderer.CachedMeshes);
        target.Unbind();
    }

    [Fact]
    public void The_renderer_reports_which_driver_answered()
    {
        // Not decoration. Every rendering defect in the related CupriFace work passed CI and looked
        // correct on one desktop driver; two were found only by a person running it on a phone. The
        // renderer string is the first question worth answering when a report arrives.
        RequireGl();

        using var renderer = GlRenderer.Create(gl.GetProcAddress!, out var error);
        Assert.True(renderer is not null, error);
        output.WriteLine($"{renderer!.Renderer} — {renderer.Version} ({renderer.Dialect})");

        Assert.NotEmpty(renderer.Renderer);
        Assert.NotEmpty(renderer.Version);
    }

    /// <summary>
    /// Refuse to run without a context, rather than passing vacuously.
    ///
    /// <para>xunit 2 has no dynamic skip, and the obvious workaround — probing for GL at discovery
    /// time — would create a second context while the fixture is making its own, which on Windows
    /// collides inside GLFW. So this fails, loudly and with the fix in the message. A suite that
    /// silently passed when it had rendered nothing would be worse than one that cannot start.</para>
    /// </summary>
    private void RequireGl() => Assert.True(gl.GetProcAddress is not null,
        $"these tests need a GL context and there is none: {gl.Unavailable}. " +
        "On a headless Linux machine run them under xvfb-run.");

    // ---- helpers ----------------------------------------------------------------------------

    private static readonly Material Red = new() { BaseColor = new Vector4(0.9f, 0.05f, 0.05f, 1f), Roughness = 0.6f };
    private static readonly Material Green = new() { BaseColor = new Vector4(0.05f, 0.9f, 0.05f, 1f), Roughness = 0.6f };

    private readonly record struct Pixel(byte R, byte G, byte B, byte A)
    {
        /// <summary>Anything meaningfully brighter than the black backdrop.</summary>
        internal bool Lit => R > 12 || G > 12 || B > 12;
        public override string ToString() => $"rgba({R},{G},{B},{A})";
    }

    private sealed class Frame(byte[] pixels, int width, int height)
    {
        internal Pixel At(int x, int y)
        {
            var i = (y * width + x) * 4;
            return new Pixel(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
        }

        /// <summary>Fraction of the frame that is not the backdrop.</summary>
        internal float LitFraction
        {
            get
            {
                var lit = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] > 12 || pixels[i + 1] > 12 || pixels[i + 2] > 12) lit++;
                return (float)lit / (width * height);
            }
        }

        /// <summary>How many distinct non-backdrop colours appear. A flat-shaded box shows one per
        /// visible face; a single colour means the shading collapsed.</summary>
        internal int DistinctColors
        {
            get
            {
                var seen = new HashSet<int>();
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] > 12 || pixels[i + 1] > 12 || pixels[i + 2] > 12)
                        seen.Add((pixels[i] << 16) | (pixels[i + 1] << 8) | pixels[i + 2]);
                return seen.Count;
            }
        }

        /// <summary>True when nothing is drawn in the outermost ring — the model is fully inside the
        /// frame rather than clipped by it.</summary>
        internal bool BorderIsClear
        {
            get
            {
                for (var x = 0; x < width; x++)
                    if (At(x, 0).Lit || At(x, height - 1).Lit) return false;
                for (var y = 0; y < height; y++)
                    if (At(0, y).Lit || At(width - 1, y).Lit) return false;
                return true;
            }
        }
    }
}

/// <summary>A box as unindexed triangle corners, outward-wound. Duplicated from the unit-test suite
/// rather than shared, because these two projects must be independently runnable.</summary>
internal static class TestBox
{
    internal static Vector3[] Corners(Vector3 size)
    {
        float x = size.X / 2, y = size.Y / 2, z = size.Z / 2;
        Vector3 v0 = new(-x, -y, -z), v1 = new(x, -y, -z), v2 = new(x, y, -z), v3 = new(-x, y, -z);
        Vector3 v4 = new(-x, -y, z), v5 = new(x, -y, z), v6 = new(x, y, z), v7 = new(-x, y, z);
        return
        [
            v4, v5, v6,  v4, v6, v7,
            v0, v3, v2,  v0, v2, v1,
            v1, v2, v6,  v1, v6, v5,
            v0, v4, v7,  v0, v7, v3,
            v3, v7, v6,  v3, v6, v2,
            v0, v1, v5,  v0, v5, v4,
        ];
    }
}
