using System.Numerics;
using StannumFab.Formats;
using Xunit;

namespace StannumFab.Tests;

/// <summary>Textures, tangents and the environment — everything M4 added that can be checked without
/// a GPU.</summary>
public class SurfaceTests
{
    private static readonly Vector3[] Triangle = [new(0, 0, 0), new(2, 0, 0), new(0, 4, 0)];
    private static readonly ushort[] TriangleIndices = [0, 1, 2];

    // ---- samplers ---------------------------------------------------------------------------

    private const string OneTexture = """
          "images": [ { "uri": "colour.png" } ],
          "samplers": [ { "wrapS": 33071, "wrapT": 33648, "magFilter": 9728, "minFilter": 9729 } ],
          "textures": [ { "source": 0, "sampler": 0 } ],
          "materials": [ { "pbrMetallicRoughness": { "baseColorTexture": { "index": 0 } } } ],
        """;

    [Fact]
    public void Sampler_settings_are_read_rather_than_defaulted()
    {
        // WRAP MODE IS THE ONE THAT BITES. A texture authored to tile, sampled with clamping, does
        // not look like a wrapping bug — it looks like a broken UV unwrap, with one stretched edge
        // texel smeared across most of the model. Defaulting everything to REPEAT would be right most
        // of the time and silently wrong the rest.
        var scene = GltfReader.Read(TestFiles.Glb(Triangle, TriangleIndices,
            topLevel: OneTexture, materials: ", \"material\": 0"));

        var texture = Assert.Single(scene.Textures);
        Assert.Equal(TextureWrap.ClampToEdge, texture.WrapS);
        Assert.Equal(TextureWrap.MirroredRepeat, texture.WrapT);
        Assert.Equal(TextureFilter.Nearest, texture.Magnify);
        Assert.Equal(TextureFilter.Linear, texture.Minify);
        // minFilter 9729 is plain LINEAR, which is one of the two non-mipmapped filters.
        Assert.False(texture.Mipmaps);
    }

    [Fact]
    public void A_texture_with_no_sampler_gets_the_specifications_defaults()
    {
        const string bare = """
              "images": [ { "uri": "c.png" } ],
              "textures": [ { "source": 0 } ],
              "materials": [ { "pbrMetallicRoughness": { "baseColorTexture": { "index": 0 } } } ],
            """;
        var scene = GltfReader.Read(TestFiles.Glb(Triangle, TriangleIndices,
            topLevel: bare, materials: ", \"material\": 0"));

        var texture = Assert.Single(scene.Textures);
        Assert.Equal(TextureWrap.Repeat, texture.WrapS);
        Assert.Equal(TextureWrap.Repeat, texture.WrapT);
        Assert.True(texture.Mipmaps);
    }

    [Fact]
    public void A_material_points_at_a_texture_not_straight_at_an_image()
    {
        // The indirection exists because one image can be sampled two ways in the same file, and one
        // sampler can serve many images. Collapsing them would make either sharing impossible.
        var scene = GltfReader.Read(TestFiles.Glb(Triangle, TriangleIndices,
            topLevel: OneTexture, materials: ", \"material\": 0"));

        var material = Assert.Single(scene.Materials);
        Assert.Equal(0, material.BaseColorTexture);
        Assert.Equal(0, scene.Textures[material.BaseColorTexture!.Value].Image);
    }

    [Fact]
    public void Textures_with_no_decoder_are_reported_and_the_geometry_still_loads()
    {
        // Not bundling an image codec is a deliberate trade — see docs/LICENSING.md — so skipping
        // textures has to be a fully supported outcome rather than a failure.
        var scene = GltfReader.Read(TestFiles.Glb(Triangle, TriangleIndices,
            topLevel: OneTexture, materials: ", \"material\": 0"));

        Assert.Equal(1, scene.TriangleCount);
        Assert.True(scene.Report.HasUnsupported);
        Assert.Contains(scene.Report.Notes, n => n.Feature == "textures" && n.Detail.Contains("decoder"));
    }

    // ---- tangents ---------------------------------------------------------------------------

    /// <summary>A quad in the XY plane with the UVs a texture would have.</summary>
    private static Mesh Quad(Vector2[]? uvs = null) => new()
    {
        Positions = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)],
        Normals = [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)],
        Uvs = uvs ?? [new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
        Indices = [0, 1, 2, 0, 2, 3],
    };

    [Fact]
    public void A_tangent_lies_in_the_surface_and_points_along_u()
    {
        var tangents = MeshTangents.Generate(Quad());

        Assert.NotNull(tangents);
        foreach (var t in tangents!)
        {
            var direction = new Vector3(t.X, t.Y, t.Z);
            Assert.Equal(1f, direction.Length(), 4);
            // Perpendicular to the surface normal, which is what Gram-Schmidt is for: the raw
            // accumulated sum rarely is, because neighbouring triangles are not coplanar.
            Assert.Equal(0f, Vector3.Dot(direction, new Vector3(0, 0, 1)), 4);
            // U increases along +X on this quad.
            Assert.Equal(1f, direction.X, 3);
        }
    }

    [Fact]
    public void Handedness_records_a_mirrored_texture()
    {
        // A symmetric model usually maps both halves to the same texture region, so one half is
        // mirrored. Without the sign, its surface detail comes out punched in rather than raised —
        // which reads as the normal map being wrong rather than as a missing bit of bookkeeping.
        var normal = MeshTangents.Generate(Quad())!;
        var mirrored = MeshTangents.Generate(Quad([new(1, 0), new(0, 0), new(0, 1), new(1, 1)]))!;

        Assert.All(normal, t => Assert.Equal(1f, t.W));
        Assert.All(mirrored, t => Assert.Equal(-1f, t.W));
    }

    [Fact]
    public void Degenerate_texture_coordinates_produce_a_usable_frame_rather_than_a_nan()
    {
        // Every corner on one UV point: "the direction U increases in" is undefined. A NaN here would
        // propagate through the whole lighting calculation and take the draw with it.
        var collapsed = Quad([Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero]);
        var tangents = MeshTangents.Generate(collapsed);

        Assert.NotNull(tangents);
        foreach (var t in tangents!)
        {
            Assert.False(float.IsNaN(t.X) || float.IsNaN(t.Y) || float.IsNaN(t.Z) || float.IsNaN(t.W));
            Assert.Equal(1f, new Vector3(t.X, t.Y, t.Z).Length(), 3);
        }
    }

    [Fact]
    public void A_mesh_without_texture_coordinates_gets_no_tangents_rather_than_wrong_ones()
    {
        // A tangent frame derived from nothing would be arbitrary, and a normal map applied against
        // an arbitrary basis makes the lighting swim as the model turns. Null is the honest answer,
        // and the renderer skips the map rather than applying it wrongly.
        var noUvs = new Mesh
        {
            Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            Normals = [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)],
            Indices = [0, 1, 2],
        };

        Assert.Null(MeshTangents.Generate(noUvs));
        Assert.Same(noUvs, MeshTangents.WithTangents(noUvs));
    }

    [Fact]
    public void Attaching_tangents_leaves_a_mesh_a_renderer_can_trust()
    {
        var withTangents = MeshTangents.WithTangents(Quad());

        Assert.NotNull(withTangents.Tangents);
        Assert.Null(withTangents.Validate());
        // Idempotent: a mesh that already has them is returned untouched rather than recomputed.
        Assert.Same(withTangents, MeshTangents.WithTangents(withTangents));
    }

    // ---- environment ------------------------------------------------------------------------

    [Fact]
    public void The_environment_is_brightest_overhead_and_darkest_below()
    {
        // What a room actually looks like, and what stops a model appearing to float in a void.
        var environment = Environment.Studio;

        var sky = environment.Sample(Vector3.UnitY, UpAxis.Y);
        var side = environment.Sample(Vector3.UnitX, UpAxis.Y);
        var floor = environment.Sample(-Vector3.UnitY, UpAxis.Y);

        Assert.True(Luminance(sky) > Luminance(side), "the sky is not brighter than the horizon");
        Assert.True(Luminance(side) > Luminance(floor), "the horizon is not brighter than the ground");

        static float Luminance(Vector3 c) => c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f;
    }

    [Fact]
    public void The_gradient_runs_along_whichever_axis_is_up()
    {
        // A Z-up printed part lit by a Y-up gradient is lit from the side, which looks like a lamp in
        // the wrong place rather than like a wrong setting.
        var environment = Environment.Studio;

        Assert.Equal(environment.Sample(Vector3.UnitY, UpAxis.Y), environment.Sample(Vector3.UnitZ, UpAxis.Z));
        Assert.NotEqual(environment.Sample(Vector3.UnitZ, UpAxis.Y), environment.Sample(Vector3.UnitZ, UpAxis.Z));
    }

    [Fact]
    public void Turning_the_environment_off_leaves_nothing_to_reflect()
    {
        // The setting that makes "a metal renders black" reproducible on purpose, for a technical
        // view where a flat read matters more than a plausible one.
        var dark = Environment.Studio with { Intensity = 0f };
        Assert.Equal(Vector3.Zero, dark.Sample(Vector3.UnitY, UpAxis.Y));

        // And a negative intensity is clamped rather than inverting the room.
        var nonsense = Environment.Studio with { Intensity = -5f };
        Assert.Equal(Vector3.Zero, nonsense.Sample(Vector3.UnitY, UpAxis.Y));
    }
}
