using System.Numerics;
using System.Text;
using Khalkos3D.Formats;
using Xunit;

namespace Khalkos3D.Tests;

/// <summary>OBJ, 3MF and glTF, plus the sniffing that picks between them.</summary>
public class FormatTests
{
    // ---- OBJ ------------------------------------------------------------------------------

    private static Scene Obj(string text) =>
        ObjReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Obj_indices_are_one_based()
    {
        // Off by one on every triangle is the classic OBJ bug, and it does not crash — it silently
        // draws the wrong vertices, which on a dense mesh looks like noise rather than like an
        // indexing error.
        var scene = Obj("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

        Assert.Equal(1, scene.TriangleCount);
        Assert.Equal(new Vector3(1, 1, 0), scene.Bounds.Size);
    }

    [Fact]
    public void Obj_negative_indices_count_back_from_the_most_recent_vertex()
    {
        // Rare, legal, and the thing most readers crash on.
        var scene = Obj("v 0 0 0\nv 2 0 0\nv 0 3 0\nf -3 -2 -1\n");

        Assert.Equal(1, scene.TriangleCount);
        Assert.Equal(new Vector3(2, 3, 0), scene.Bounds.Size);
    }

    [Fact]
    public void An_obj_quad_becomes_two_triangles()
    {
        var scene = Obj("v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n");

        Assert.Equal(2, scene.TriangleCount);
        Assert.Contains(scene.Report.Notes, n => n.Feature == "polygon");
    }

    [Fact]
    public void An_obj_with_texture_coordinates_keeps_them_and_flips_v()
    {
        // OBJ's texture origin is bottom-left and every image this engine uploads is top-row-first.
        // Flipping at the parse means nothing downstream has to know.
        var scene = Obj("v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nf 1/1 2/2 3/3\n");

        var uvs = scene.Meshes[0].Uvs;
        Assert.NotNull(uvs);
        Assert.Equal(new Vector2(0, 1), uvs![0]);
        Assert.Equal(new Vector2(0, 0), uvs[2]);
    }

    [Fact]
    public void An_obj_material_library_is_reported_rather_than_silently_ignored()
    {
        var scene = Obj("mtllib thing.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl red\nf 1 2 3\n");

        Assert.True(scene.Report.HasUnsupported);
        Assert.Contains(scene.Report.Notes, n => n.Feature == "mtllib");
    }

    // ---- 3MF ------------------------------------------------------------------------------

    [Fact]
    public void A_3mf_reads_its_geometry_without_needing_to_be_welded()
    {
        var scene = ThreeMfReader.Read(new MemoryStream(TestFiles.ThreeMf(TestShapes.Box(new Vector3(10, 20, 30)))));

        Assert.Equal(12, scene.TriangleCount);
        Assert.Equal(new Vector3(10, 20, 30), scene.Bounds.Size);
    }

    [Fact]
    public void An_inch_3mf_is_converted_to_millimetres()
    {
        // THE REASON 3MF IS WORTH READING AT ALL. An STL of a 20 mm cube and of a 20 inch cube are
        // byte-identical apart from the numbers; a 3MF says which it means, so a build volume or a
        // measurement can be written once against one unit.
        var scene = ThreeMfReader.Read(new MemoryStream(TestFiles.ThreeMf(TestShapes.Box(Vector3.One), "inch")));

        Assert.Equal(25.4f, scene.Bounds.Size.X, 3);
        Assert.Contains(scene.Report.Notes, n => n.Feature == "unit");
    }

    [Theory]
    [InlineData("micron", 0.001f)]
    [InlineData("millimeter", 1f)]
    [InlineData("centimeter", 10f)]
    [InlineData("inch", 25.4f)]
    [InlineData("foot", 304.8f)]
    [InlineData("meter", 1000f)]
    public void Every_3mf_unit_converts(string unit, float expected) =>
        Assert.Equal(expected, ThreeMfReader.UnitToMillimetres(unit));

    [Fact]
    public void An_unknown_3mf_unit_is_repaired_to_millimetres_rather_than_scaling_by_zero()
    {
        var scene = ThreeMfReader.Read(new MemoryStream(TestFiles.ThreeMf(TestShapes.Box(Vector3.One), "furlong")));

        Assert.Equal(Vector3.One, scene.Bounds.Size);
        Assert.True(scene.Report.HasRepairs);
    }

    [Fact]
    public void A_3mf_build_item_transform_places_the_object()
    {
        // The 4x3 row-major form, which is 3MF's own and not the same layout as glTF's.
        var moved = """<item objectid="1" transform="1 0 0 0 1 0 0 0 1 100 0 0"/>""";
        var scene = ThreeMfReader.Read(new MemoryStream(
            TestFiles.ThreeMf(TestShapes.Box(Vector3.One), build: moved)));

        Assert.Equal(100f, scene.Bounds.Min.X, 3);
        Assert.Equal(101f, scene.Bounds.Max.X, 3);
    }

    [Fact]
    public void A_3mf_that_is_not_a_zip_is_refused_with_a_sentence_about_the_file()
    {
        var ex = Assert.Throws<ModelFormatException>(() =>
            ThreeMfReader.Read(new MemoryStream("not a zip at all"u8.ToArray())));
        Assert.Contains("zip", ex.Message);
    }

    [Fact]
    public void A_3mf_colour_group_reaches_the_mesh_as_vertex_colours()
    {
        // How a multi-colour print says which parts are which filament. Dropping it turns a two-tone
        // model into a uniform grey one with nothing to indicate anything was lost.
        const string palette = """<colorgroup id="5"><color color="#FF0000"/><color color="#0000FF"/></colorgroup>""";
        var scene = ThreeMfReader.Read(new MemoryStream(TestFiles.ThreeMf(
            TestShapes.Box(Vector3.One), extraResources: palette,
            trianglePropertyAttributes: " pid=\"5\" p1=\"0\"")));

        var colors = scene.Meshes[0].Colors;
        Assert.NotNull(colors);
        Assert.All(colors!, c => Assert.Equal(new Vector4(1, 0, 0, 1), c));
        Assert.Contains(scene.Report.Notes, n => n.Feature == "colour group");
    }

    [Fact]
    public void Corners_are_split_only_where_the_colours_actually_differ()
    {
        // A colour belongs to a CORNER, not to a position, so two triangles meeting at a vertex in
        // different colours need two vertices there. Splitting unconditionally would triple every
        // mesh for a feature most files never use.
        const string palette = """<colorgroup id="5"><color color="#FF0000"/></colorgroup>""";
        var uniform = ThreeMfReader.Read(new MemoryStream(TestFiles.ThreeMf(
            TestShapes.Box(Vector3.One), extraResources: palette,
            trianglePropertyAttributes: " pid=\"5\" p1=\"0\"")));

        // Every triangle names the same swatch, so no vertex needs splitting beyond what the file
        // already had.
        var plain = ThreeMfReader.Read(new MemoryStream(TestFiles.ThreeMf(TestShapes.Box(Vector3.One))));
        Assert.Equal(plain.Meshes[0].VertexCount, uniform.Meshes[0].VertexCount);
        Assert.Equal(12, uniform.TriangleCount);
    }

    [Fact]
    public void A_property_group_that_is_not_a_colour_is_reported_rather_than_guessed_at()
    {
        var scene = ThreeMfReader.Read(new MemoryStream(TestFiles.ThreeMf(
            TestShapes.Box(Vector3.One), trianglePropertyAttributes: " pid=\"99\" p1=\"0\"")));

        Assert.Null(scene.Meshes[0].Colors);
        Assert.True(scene.Report.HasUnsupported);
        Assert.Contains(scene.Report.Notes, n => n.Feature == "triangle properties");
    }

    // ---- glTF -----------------------------------------------------------------------------

    private static readonly Vector3[] TrianglePositions =
        [new(0, 0, 0), new(2, 0, 0), new(0, 4, 0)];
    private static readonly ushort[] TriangleIndices = [0, 1, 2];

    [Fact]
    public void A_glb_reads_its_geometry_from_the_binary_chunk()
    {
        var scene = GltfReader.Read(TestFiles.Glb(TrianglePositions, TriangleIndices));

        Assert.Equal(1, scene.TriangleCount);
        Assert.Equal(new Vector3(2, 4, 0), scene.Bounds.Size);
        Assert.Equal("test", scene.Meshes[0].Name);
    }

    [Fact]
    public void A_glb_with_no_normals_gets_computed_ones()
    {
        var scene = GltfReader.Read(TestFiles.Glb(TrianglePositions, TriangleIndices));

        Assert.NotNull(scene.Meshes[0].Normals);
        foreach (var n in scene.Meshes[0].Normals!)
            Assert.Equal(1f, n.Length(), 4);
    }

    [Fact]
    public void A_required_extension_is_refused_loudly_rather_than_rendered_wrongly()
    {
        // THE RULE THIS READER IS BUILT AROUND. Draco changes how the buffers are ENCODED, so
        // reading them as plain arrays yields geometry that is not simplified but meaningless. A
        // viewer that displayed the result would tell its user their file is broken when it is not.
        var ex = Assert.Throws<ModelFormatException>(() =>
            GltfReader.Read(TestFiles.Glb(TrianglePositions, TriangleIndices,
                                          requiredExtension: "KHR_draco_mesh_compression")));

        Assert.Contains("KHR_draco_mesh_compression", ex.Message);
        Assert.Contains("buffers are encoded", ex.Message);
    }

    [Fact]
    public void A_node_transform_places_the_mesh()
    {
        var scene = GltfReader.Read(TestFiles.Glb(TrianglePositions, TriangleIndices,
            extraJson: """, "translation": [10, 0, 0]"""));

        Assert.Equal(10f, scene.Bounds.Min.X, 3);
        Assert.Equal(12f, scene.Bounds.Max.X, 3);
    }

    [Fact]
    public void A_glb_of_the_wrong_version_says_so()
    {
        var glb = TestFiles.Glb(TrianglePositions, TriangleIndices);
        glb[4] = 1;   // version 1

        var ex = Assert.Throws<ModelFormatException>(() => GltfReader.Read(glb));
        Assert.Contains("version 1", ex.Message);
    }

    // ---- detection ------------------------------------------------------------------------

    [Theory]
    [InlineData("stl")]
    [InlineData(null)]
    public void A_binary_stl_is_detected_from_its_bytes(string? extension)
    {
        var bytes = TestShapes.BinaryStl(TestShapes.Box(Vector3.One));
        Assert.Equal(ModelFormat.Stl, ModelReader.Detect(bytes, extension));
    }

    [Fact]
    public void An_obj_is_not_mistaken_for_an_stl()
    {
        // The ordering trap: "is this NOT ascii STL" is true of an OBJ too, so a detector that used
        // the STL test as its catch-all would claim every OBJ file.
        var obj = Encoding.UTF8.GetBytes("# exported\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        Assert.Equal(ModelFormat.Obj, ModelReader.Detect(obj, null));
        Assert.Equal(ModelFormat.Obj, ModelReader.Detect(obj, ".obj"));
    }

    [Fact]
    public void Each_container_is_recognised_by_its_magic_number()
    {
        Assert.Equal(ModelFormat.Gltf, ModelReader.Detect(TestFiles.Glb(TrianglePositions, TriangleIndices)));
        Assert.Equal(ModelFormat.ThreeMf, ModelReader.Detect(TestFiles.ThreeMf(TestShapes.Box(Vector3.One))));
        Assert.Equal(ModelFormat.Gltf, ModelReader.Detect("""  { "asset": {"version":"2.0"} }"""u8));
        Assert.Equal(ModelFormat.Stl, ModelReader.Detect(TestShapes.AsciiStl(TestShapes.Box(Vector3.One))));
    }

    [Fact]
    public void The_bytes_win_when_the_extension_disagrees_with_them()
    {
        // Downloads land as .txt and people rename things. A file's name is a claim; its bytes are
        // a fact.
        var glb = TestFiles.Glb(TrianglePositions, TriangleIndices);
        Assert.Equal(ModelFormat.Gltf, ModelReader.Detect(glb, ".stl"));
    }

    [Fact]
    public void Something_that_is_no_format_at_all_is_unknown_rather_than_guessed()
    {
        Assert.Equal(ModelFormat.Unknown, ModelReader.Detect("hello, this is prose"u8, null));
    }

    [Fact]
    public void Every_format_produces_the_same_scene_shape()
    {
        // The whole common-code claim, asserted once: four parsers, one type, and a caller that
        // never learns which one ran.
        var box = TestShapes.Box(new Vector3(2, 2, 2));
        Scene[] scenes =
        [
            ModelReader.Read(TestShapes.BinaryStl(box), "stl"),
            ModelReader.Read(TestShapes.AsciiStl(box), "stl"),
            ModelReader.Read(TestFiles.ThreeMf(box), "3mf"),
        ];

        foreach (var scene in scenes)
        {
            Assert.Equal(12, scene.TriangleCount);
            Assert.Equal(new Vector3(2, 2, 2), scene.Bounds.Size);
            Assert.NotEmpty(scene.Draws());
            foreach (var (mesh, _, material) in scene.Draws())
            {
                Assert.Null(mesh.Validate());
                Assert.NotNull(material);
            }
        }
    }
}
