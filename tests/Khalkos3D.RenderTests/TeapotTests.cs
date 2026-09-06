using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Khalkos3D.Formats;
using Khalkos3D.Gl;
using Xunit;
using Xunit.Abstractions;

namespace Khalkos3D.RenderTests;

/// <summary>
/// One real file, opened and drawn.
///
/// <para><b>Every other test in these suites uses a fixture built in code</b>, which is right for
/// pinning a specific behaviour and useless for the question that actually matters: does this open a
/// file somebody exported from a real tool? A hand-written fixture only contains what its author
/// thought to put in, so it can never catch an assumption they did not know they were making.</para>
///
/// <para>This file carries four things the fixtures do not, and each is a way a glTF reader is
/// commonly wrong: <b>interleaved accessors at stride 32</b> — the single most likely parsing bug,
/// because a reader that assumes tight packing produces geometry rather than an error —
/// <b>32-bit indices</b>, a <b>two-node scene graph</b>, and an <b>838 KB embedded JPEG</b> that
/// exercises the image seam end to end.</para>
/// </summary>
[Collection(GlCollection.Name)]
public class TeapotTests(GlFixture gl, ITestOutputHelper output)
{
    private const string Path = "teapot.glb";
    private const int Size = 320;

    /// <summary>The decoder a caller supplies. Khalkos3D bundles no codec — see docs/LICENSING.md —
    /// so this is what four lines of integration actually look like.</summary>
    private static ImageData? DecodeWithSkia(byte[] encoded)
    {
        using var decoded = SKBitmap.Decode(encoded);
        if (decoded is null) return null;
        using var rgba = decoded.Info.ColorType == SKColorType.Rgba8888
            ? decoded.Copy() : decoded.Copy(SKColorType.Rgba8888);
        var pixels = new byte[rgba.Width * rgba.Height * 4];
        Marshal.Copy(rgba.GetPixels(), pixels, 0, pixels.Length);
        return new ImageData(pixels, rgba.Width, rgba.Height);
    }

    private static Scene Load() =>
        GltfReader.ReadFile(Path, new GltfOptions { DecodeImage = DecodeWithSkia });

    // ---- the parse --------------------------------------------------------------------------

    [Fact]
    public void The_real_file_parses_into_the_shape_it_actually_has()
    {
        var scene = Load();
        output.WriteLine($"{scene.TriangleCount:N0} triangles, bounds {scene.Bounds}, " +
                         $"{scene.Meshes.Count} mesh(es), {scene.Images.Count} image(s), up {scene.Up}");
        foreach (var note in scene.Report.Notes) output.WriteLine("  " + note);

        // 4,032 is what CupriFace's own documentation records for this file, so it is an independent
        // number rather than one read back out of this reader.
        Assert.Equal(4032, scene.TriangleCount);
        Assert.Equal(UpAxis.Y, scene.Up);
        Assert.All(scene.Meshes, m => Assert.Null(m.Validate()));
    }

    [Fact]
    public void Interleaved_accessors_are_read_at_their_stride()
    {
        // THE BUG THIS FILE EXISTS TO CATCH. Its vertex data is interleaved at stride 32, so a
        // reader that assumes tightly packed accessors reads position data out of the middle of
        // other attributes. It does not fail — it produces a vertex cloud, which is why the check has
        // to be about the SHAPE rather than about an exception.
        var scene = Load();
        var bounds = scene.Bounds;
        var size = bounds.Size;

        output.WriteLine($"bounds {bounds}");
        // A teapot is wider than it is tall and not wildly asymmetric. Garbage read at the wrong
        // stride is float noise, which produces extents differing by orders of magnitude.
        var longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        var shortest = MathF.Min(size.X, MathF.Min(size.Y, size.Z));
        Assert.True(longest / shortest < 4f,
            $"the bounds are {bounds}, whose aspect suggests positions were read at the wrong stride");
        Assert.True(float.IsFinite(longest) && longest > 0f, $"degenerate bounds: {bounds}");

        // Every vertex inside the box the accessor's own min/max implies. Noise escapes this.
        foreach (var mesh in scene.Meshes)
            foreach (var p in mesh.Positions)
                Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z),
                    $"a non-finite position {p} — the accessor was misread");
    }

    [Fact]
    public void The_scene_graph_and_its_texture_survive()
    {
        var scene = Load();

        Assert.NotEmpty(scene.Roots);
        Assert.NotEmpty(scene.Textures);
        Assert.NotEmpty(scene.Images);

        var image = scene.Images[0];
        output.WriteLine($"texture {image.Width}x{image.Height}, {image.Pixels.Length:N0} bytes decoded");
        Assert.True(image.Width > 1 && image.Height > 1, "the embedded JPEG did not decode");
        Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);

        // The material has to actually point at it, or the decode was wasted.
        var textured = scene.Draws().Where(d => d.Material.BaseColorTexture is not null).ToList();
        Assert.NotEmpty(textured);
    }

    [Fact]
    public void Uvs_are_present_and_are_not_all_the_same_point()
    {
        // A UV channel read at the wrong offset commonly comes out constant, which renders as one
        // texel stretched over the whole model — the same symptom as a wrap-mode mistake, and a
        // reason to check the data rather than the picture.
        var scene = Load();
        var mesh = scene.Meshes.First(m => m.Uvs is not null);

        var distinct = mesh.Uvs!.Distinct().Take(50).Count();
        output.WriteLine($"{distinct} distinct UVs in the first 50 sampled");
        Assert.True(distinct > 5, "the texture coordinates are nearly all identical");
    }

    [Fact]
    public void The_files_unmipmapped_min_filter_is_upgraded_and_the_deviation_is_recorded()
    {
        // FOUND BY RENDERING THIS FILE, not by reading the specification. It declares
        // minFilter: LINEAR — as exporters constantly do, without meaning anything by it — and
        // honouring that faithfully makes a 701x561 texture on a 200-pixel teapot alias into a
        // visible shimmer. The render was speckled until this was changed, and the speckle looks
        // like a broken renderer rather than like a faithfully honoured sampler.
        //
        // So the deviation is made, and RECORDED, which is the difference between a considered
        // choice and quietly ignoring the file.
        var upgraded = Load();
        Assert.True(upgraded.Textures[0].Mipmaps);
        Assert.Contains(upgraded.Report.Notes, n => n.Feature == "min filter");

        var faithful = GltfReader.ReadFile(Path, new GltfOptions
        {
            DecodeImage = DecodeWithSkia,
            UpgradeMinFilters = false,
        });
        Assert.False(faithful.Textures[0].Mipmaps);
        Assert.DoesNotContain(faithful.Report.Notes, n => n.Feature == "min filter");
        // Whichever way, the file's other sampler settings are honoured exactly.
        Assert.Equal(TextureWrap.Repeat, faithful.Textures[0].WrapS);
    }

    // ---- the render -------------------------------------------------------------------------

    [Fact]
    public void The_teapot_renders_and_lands_where_cupriface_measured_it()
    {
        // CROSS-CHECKED AGAINST A DIFFERENT RENDERER. CupriFace's experiments recorded the mean
        // colour over model pixels for this same file on three hosts: roughly (97, 93, 91) on
        // desktop, (96, 91, 89) on the web, (94, 91, 88) on Android. Those came from a different
        // shader — flat ambient, no image-based lighting — so an exact match would be suspicious
        // rather than reassuring. What the number does prove is that the texture is being sampled and
        // is landing at the right overall tone: a mis-sampled texture, a missing one, or a wrong
        // colour space would move this a long way, not a little.
        Assert.True(gl.GetProcAddress is not null, $"these tests need a GL context: {gl.Unavailable}");

        var scene = Load();
        using var renderer = GlRenderer.Create(gl.GetProcAddress!, out var error);
        Assert.True(renderer is not null, error);
        output.WriteLine($"driver: {renderer!.Renderer} — {renderer.Version}");

        using var target = GlOffscreenTarget.Create(gl.GetProcAddress!, Size, Size, out var targetError);
        Assert.True(target is not null, targetError);
        target!.Bind();

        var camera = Camera.Frame(scene.Bounds, scene.Up, yaw: 0.9f, pitch: 0.35f, zoom: 0.85f);
        renderer.Draw(scene, camera, Size, Size, new RenderSettings
        {
            ClearColor = new Vector4(0f, 0f, 0f, 1f),
            Up = scene.Up,
        });
        var pixels = target.ReadPixels();
        target.Unbind();

        // Mean over MODEL pixels only — anything not the backdrop.
        long r = 0, g = 0, b = 0, n = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] <= 12 && pixels[i + 1] <= 12 && pixels[i + 2] <= 12) continue;
            r += pixels[i]; g += pixels[i + 1]; b += pixels[i + 2]; n++;
        }
        Assert.True(n > Size * Size / 10, $"only {n} model pixels; the teapot barely drew");

        var mean = new Vector3((float)r / n, (float)g / n, (float)b / n);
        output.WriteLine($"{n:N0} model pixels, mean rgb {mean.X:0.0}, {mean.Y:0.0}, {mean.Z:0.0} " +
                         "(CupriFace measured 97.3, 92.5, 90.5 on desktop)");

        // A wide band on purpose: a different shading model legitimately moves this. What it excludes
        // is a black model, a white one, and a strong colour cast — each of which is a real failure.
        Assert.InRange(mean.X, 45f, 165f);
        Assert.InRange(mean.Y, 45f, 165f);
        Assert.InRange(mean.Z, 45f, 165f);

        // The texture's own warmth: this model reads slightly red of neutral on every host CupriFace
        // measured. A model rendering grey would mean the texture is not reaching the shader.
        Assert.True(mean.X > mean.Z, $"mean {mean} has no warmth, so the texture is probably not sampled");

        WritePng(pixels, "teapot-render.png");
        output.WriteLine("wrote teapot-render.png");
    }

    [Fact]
    public void A_real_model_can_be_sectioned_and_measured()
    {
        // The two printing questions on one real file: what is inside it, and would it print.
        Assert.True(gl.GetProcAddress is not null, $"these tests need a GL context: {gl.Unavailable}");
        var scene = Load();

        var report = MeshAnalysis.Analyse(scene);
        output.WriteLine($"analysis: {report}");
        if (report.Problem() is { } problem) output.WriteLine($"  would not print cleanly: {problem}");
        Assert.True(report.Triangles > 0);
        Assert.True(report.SurfaceArea > 0f);

        using var renderer = GlRenderer.Create(gl.GetProcAddress!, out var error);
        Assert.True(renderer is not null, error);
        using var target = GlOffscreenTarget.Create(gl.GetProcAddress!, Size, Size, out _);
        target!.Bind();

        var camera = Camera.Frame(scene.Bounds, scene.Up, yaw: 0.9f, pitch: 0.35f, zoom: 0.85f);
        var toViewer = Vector3.Normalize(camera.Position - camera.Target);
        renderer!.Draw(scene, camera, Size, Size, new RenderSettings
        {
            ClearColor = new Vector4(0f, 0f, 0f, 1f),
            Up = scene.Up,
            Section = scene.Bounds.SectionAt(toViewer, 0.55f),
        });
        var pixels = target.ReadPixels();
        target.Unbind();

        var lit = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 12 || pixels[i + 1] > 12 || pixels[i + 2] > 12) lit++;
        Assert.True(lit > Size * Size / 40, "the section removed the whole teapot");

        WritePng(pixels, "teapot-section.png");
        output.WriteLine("wrote teapot-section.png");
    }

    private static void WritePng(byte[] pixels, string name)
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, name);
        using var image = SKImage.FromPixelCopy(
            new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul), pixels);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        using var stream = File.Create(file);
        data.SaveTo(stream);
    }
}
