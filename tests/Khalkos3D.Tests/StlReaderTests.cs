using System.Globalization;
using System.Numerics;
using System.Text;
using Khalkos3D.Formats;
using Xunit;

namespace Khalkos3D.Tests;

/// <summary>
/// The STL reader. Most of these pin a specific way real files break rather than the happy path,
/// because the happy path is twenty lines and the failures are what cost time.
/// </summary>
public class StlReaderTests
{
    private static Scene Read(byte[] bytes, StlOptions? options = null) =>
        StlReader.Read(new MemoryStream(bytes), options);

    [Fact]
    public void A_binary_box_reads_with_the_right_size_and_triangle_count()
    {
        var scene = Read(TestShapes.BinaryStl(TestShapes.Box(new Vector3(10, 20, 30))));

        Assert.Equal(12, scene.TriangleCount);
        Assert.Equal(new Vector3(10, 20, 30), scene.Bounds.Size);
        Assert.Equal(24, scene.Meshes[0].VertexCount);
    }

    [Fact]
    public void An_ascii_box_reads_identically_to_the_binary_one()
    {
        var corners = TestShapes.Box(new Vector3(10, 20, 30));
        var binary = Read(TestShapes.BinaryStl(corners));
        var ascii = Read(TestShapes.AsciiStl(corners));

        Assert.Equal(binary.TriangleCount, ascii.TriangleCount);
        Assert.Equal(binary.Bounds.Size, ascii.Bounds.Size);
        Assert.Equal(binary.Meshes[0].VertexCount, ascii.Meshes[0].VertexCount);
    }

    [Fact]
    public void A_binary_file_whose_header_begins_solid_is_still_read_as_binary()
    {
        // THE TRAP THIS READER EXISTS TO AVOID. Several popular exporters write "solid <name>" into
        // the 80-byte header of a BINARY file, where any bytes are legal. Sniffing the first five
        // characters — the obvious test, and the one most implementations use — misreads those files
        // as ASCII and then fails on text that was never there.
        var bytes = TestShapes.BinaryStl(TestShapes.Box(Vector3.One), header: "solid exported_by_something");

        Assert.True(StlReader.IsBinary(bytes));
        Assert.Equal(12, Read(bytes).TriangleCount);
    }

    [Fact]
    public void An_ascii_file_is_not_mistaken_for_binary()
    {
        var bytes = TestShapes.AsciiStl(TestShapes.Box(Vector3.One));

        Assert.False(StlReader.IsBinary(bytes));
        Assert.Equal(12, Read(bytes).TriangleCount);
    }

    [Fact]
    public void A_comma_decimal_culture_does_not_change_the_size_of_the_model()
    {
        // A machine set to a comma-decimal locale reading "1.5" with the current culture gets 15 —
        // a model ten times too big, with nothing to indicate why. STL is always written with a
        // decimal point regardless of who wrote it, so parsing must be invariant.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var scene = Read(TestShapes.AsciiStl(TestShapes.Box(new Vector3(1.5f, 2.5f, 3.5f))));
            Assert.Equal(new Vector3(1.5f, 2.5f, 3.5f), scene.Bounds.Size);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void A_zeroed_facet_normal_does_not_produce_a_black_model()
    {
        // Real exporters write (0,0,0) normals constantly. A reader that trusted them would hand the
        // renderer zero-length normals, and every lighting calculation downstream would return zero.
        // The winding is the source of truth here, not the stored normal.
        var scene = Read(TestShapes.BinaryStl(TestShapes.Box(Vector3.One)));

        foreach (var n in scene.Meshes[0].Normals!)
            Assert.True(MathF.Abs(n.Length() - 1f) < 1e-5f, $"normal {n} is not unit length");
    }

    [Fact]
    public void A_header_claiming_more_triangles_than_the_file_holds_is_repaired_and_reported()
    {
        // Truncated downloads and interrupted writes. Trusting the count reads off the end of the
        // buffer; silently trusting the file leaves the user wondering where their model went.
        var bytes = TestShapes.BinaryStl(TestShapes.Box(Vector3.One));
        var truncated = bytes[..(bytes.Length - 50 * 3)];      // three facets short

        var scene = StlReader.Read(new MemoryStream(truncated));

        Assert.Equal(9, scene.TriangleCount);
        Assert.True(scene.Report.HasRepairs);
        Assert.Contains(scene.Report.Notes, n => n.Feature == "triangle count");
    }

    [Fact]
    public void A_stray_vertex_at_the_end_of_an_ascii_file_does_not_make_a_partial_triangle()
    {
        var text = Encoding.UTF8.GetString(TestShapes.AsciiStl(TestShapes.Box(Vector3.One)));
        var broken = text.Replace("endsolid test", "  facet normal 0 0 0\n    outer loop\n      vertex 9 9 9\n");

        var scene = StlReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(broken)));

        Assert.Equal(12, scene.TriangleCount);
        Assert.Null(scene.Meshes[0].Validate());
        Assert.Contains(scene.Report.Notes, n => n.Feature == "facet");
    }

    [Fact]
    public void Per_facet_colour_is_recovered_when_a_file_marks_it_valid()
    {
        // Pure red in the 15-bit packing, top bit set to mark it present.
        const ushort red = 0x8000 | 0x1F;
        var scene = Read(TestShapes.BinaryStl(TestShapes.Box(Vector3.One), attribute: red));

        var colors = scene.Meshes[0].Colors;
        Assert.NotNull(colors);
        Assert.All(colors!, c => Assert.Equal(new Vector4(1, 0, 0, 1), c));
    }

    [Fact]
    public void An_uncoloured_file_gets_no_colours_rather_than_uniform_black()
    {
        // The attribute word is officially unused, so most files leave it zero. Reading that as a
        // colour would paint every model black — and black is exactly what an unlit shading bug
        // looks like, which is the worst possible way to be wrong.
        var scene = Read(TestShapes.BinaryStl(TestShapes.Box(Vector3.One)));

        Assert.Null(scene.Meshes[0].Colors);
    }

    [Fact]
    public void Welding_can_be_turned_off_and_then_every_triangle_keeps_its_own_corners()
    {
        var scene = Read(TestShapes.BinaryStl(TestShapes.Box(Vector3.One)),
                         new StlOptions { Weld = false });

        Assert.Equal(36, scene.Meshes[0].VertexCount);
        Assert.Null(scene.Meshes[0].Validate());
    }

    [Fact]
    public void An_empty_file_is_refused_loudly_rather_than_producing_an_empty_model()
    {
        // An empty scene renders as nothing, which is indistinguishable from a rendering bug. Better
        // to say the file had no triangles.
        Assert.Throws<ModelFormatException>(() => Read(TestShapes.BinaryStl([])));
    }

    [Fact]
    public void The_report_says_the_material_was_invented()
    {
        // STL stores no material. Anyone comparing this viewport against another tool deserves to
        // know the colour they are looking at is ours.
        var scene = Read(TestShapes.BinaryStl(TestShapes.Box(Vector3.One)));

        Assert.Contains(scene.Report.Notes, n => n.Feature == "material");
        Assert.False(scene.Report.HasUnsupported);
    }
}
