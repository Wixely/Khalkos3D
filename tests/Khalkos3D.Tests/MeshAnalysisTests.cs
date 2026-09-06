using System.Numerics;
using Xunit;

namespace Khalkos3D.Tests;

/// <summary>
/// Measuring a mesh and reporting its topology — closed or bordered, manifold or branching,
/// consistently wound or not. All answerable without a GPU.
/// </summary>
public class MeshAnalysisTests
{
    private static Mesh Soup(Vector3[] corners) => new()
    {
        Positions = corners,
        Indices = [.. Enumerable.Range(0, corners.Length)],
    };

    // ---- measurement ------------------------------------------------------------------------

    [Fact]
    public void A_box_measures_its_own_volume_and_surface_area()
    {
        var report = MeshAnalysis.Analyse(Soup(TestShapes.Box(new Vector3(10, 20, 30))));

        Assert.Equal(12, report.Triangles);
        Assert.Equal(new Vector3(10, 20, 30), report.Size);
        Assert.Equal(10f * 20f * 30f, report.Volume, 1);
        // 2(lw + wh + lh)
        Assert.Equal(2f * (10 * 20 + 20 * 30 + 10 * 30), report.SurfaceArea, 1);
    }

    [Fact]
    public void Topology_is_computed_on_POSITIONS_so_a_render_ready_box_is_still_closed()
    {
        // THE TRAP THIS CLASS EXISTS TO AVOID, and the reason it merges positions before looking at
        // edges. A mesh prepared for RENDERING has extra vertices wherever a hard edge needed its own
        // normal — MeshWelder splits a cube's eight corners into twenty-four — so matching edges by
        // INDEX reports every edge of a perfectly sound cube as a hole. A viewer doing that would
        // tell its user their file is broken when it is not.
        var rendered = MeshWelder.FromTriangleSoup(TestShapes.Box(new Vector3(10, 10, 10)), out var weld);
        Assert.Equal(24, weld.VerticesAfter);   // the split really did happen

        var report = MeshAnalysis.Analyse(rendered);

        Assert.True(report.IsClosedManifold, $"a sound box was reported as {report.Problem()}");
        Assert.Equal(8, report.Vertices);       // topologically it is still eight corners
        Assert.Equal(0, report.BoundaryEdges);
    }

    [Fact]
    public void An_empty_scene_measures_nothing_rather_than_throwing()
    {
        var report = MeshAnalysis.Analyse(new Scene());

        Assert.Equal(0, report.Triangles);
        Assert.Equal(Vector3.Zero, report.Size);
        Assert.Equal(0f, report.Volume);
    }

    // ---- topology ---------------------------------------------------------------------------

    [Fact]
    public void A_hole_is_found_and_counted_in_edges()
    {
        // One face removed. The four edges around the opening now belong to one triangle each, so the
        // surface has a border there rather than continuing.
        var corners = TestShapes.Box(new Vector3(10, 10, 10)).ToList();
        corners.RemoveRange(0, 6);              // drop the +Z face's two triangles

        var report = MeshAnalysis.Analyse(Soup([.. corners]));

        Assert.False(report.IsClosedManifold);
        Assert.Equal(4, report.BoundaryEdges);
        Assert.Contains("boundary edge", report.Problem());
    }

    [Fact]
    public void A_mesh_wound_inside_out_is_detected_from_the_sign_of_its_volume()
    {
        // The only reliable way, and worth stating: a consistently inverted mesh is a perfectly good
        // closed manifold, and once a renderer flips normals towards the viewer it looks correct from
        // outside. Nothing but the volume's sign gives it away — and anything reasoning about which
        // side is inside will get the opposite answer.
        var corners = TestShapes.Box(new Vector3(10, 10, 10));
        for (var i = 0; i + 2 < corners.Length; i += 3)
            (corners[i + 1], corners[i + 2]) = (corners[i + 2], corners[i + 1]);

        var report = MeshAnalysis.Analyse(Soup(corners));

        Assert.True(report.IsClosedManifold, "an inverted box is still closed");
        Assert.True(report.IsInverted);
        Assert.True(report.Volume < 0f);
        Assert.Equal(1000f, report.AbsoluteVolume, 1);
        Assert.Equal("wound inside out", report.Problem());
    }

    [Fact]
    public void One_reversed_triangle_is_reported_as_inconsistent_winding_not_as_a_hole()
    {
        // A different defect from a hole and fixed differently, so it gets its own count: the two
        // triangles either side of these edges disagree about which way is out.
        var corners = TestShapes.Box(new Vector3(10, 10, 10));
        (corners[1], corners[2]) = (corners[2], corners[1]);   // reverse a single triangle

        var report = MeshAnalysis.Analyse(Soup(corners));

        Assert.False(report.IsClosedManifold);
        Assert.Equal(0, report.BoundaryEdges);
        Assert.True(report.MisorientedEdges > 0, "a reversed triangle was not detected");
        Assert.Contains("inconsistent winding", report.Problem());
    }

    [Fact]
    public void An_edge_shared_by_three_triangles_is_non_manifold()
    {
        // A fin: two triangles forming a surface, plus a third hanging off the shared edge. Nothing
        // is bordered, and the surface still has no consistent two sides.
        Vector3[] fin =
        [
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0),
            new(0, 0, 0), new(1, 0, 0), new(0, 0, 1),
            new(0, 0, 0), new(1, 0, 0), new(0, -1, 1),
        ];

        var report = MeshAnalysis.Analyse(Soup(fin));

        Assert.True(report.NonManifoldEdges > 0, "three triangles on one edge were not detected");
        Assert.False(report.IsClosedManifold);
        Assert.Contains("non-manifold", report.Problem());
    }

    [Fact]
    public void Degenerate_triangles_are_counted_and_do_not_corrupt_the_measurements()
    {
        // Exported meshes are full of zero-area triangles. They must not contribute area, must not
        // create phantom edges, and must not make a sound surface look irregular.
        var corners = TestShapes.Box(new Vector3(10, 10, 10)).ToList();
        corners.AddRange([new Vector3(3, 3, 3), new Vector3(3, 3, 3), new Vector3(3, 3, 3)]);

        var report = MeshAnalysis.Analyse(Soup([.. corners]));

        Assert.Equal(1, report.DegenerateTriangles);
        Assert.True(report.IsClosedManifold, $"a degenerate triangle broke the analysis: {report.Problem()}");
        Assert.Equal(600f, report.SurfaceArea, 1);
        Assert.Equal(1000f, report.Volume, 1);
    }

    [Fact]
    public void A_sound_box_reports_no_problem_at_all()
    {
        var report = MeshAnalysis.Analyse(Soup(TestShapes.Box(new Vector3(5, 5, 5))));

        Assert.True(report.IsClosedManifold);
        Assert.False(report.IsInverted);
        Assert.Null(report.Problem());
        Assert.Contains("closed manifold", report.ToString());
    }

    // ---- section planes ---------------------------------------------------------------------

    [Fact]
    public void A_section_at_zero_and_one_sits_at_the_extremes_of_the_model()
    {
        // The 0..1 range is what lets a viewer offer a slider without knowing the scale, so the ends
        // have to land exactly on the model rather than approximately.
        var bounds = new BoundingBox(new Vector3(-5, -5, -5), new Vector3(5, 5, 5));

        var near = bounds.SectionAt(Vector3.UnitZ, 0f);
        var far = bounds.SectionAt(Vector3.UnitZ, 1f);
        var middle = bounds.SectionAt(Vector3.UnitZ, 0.5f);

        Assert.Equal(5f, near.D, 3);      // dot(n,p) + D = 0 at z = -5
        Assert.Equal(-5f, far.D, 3);
        Assert.Equal(0f, middle.D, 3);
    }

    [Fact]
    public void A_diagonal_cut_spans_the_corners_rather_than_one_axis()
    {
        // Taken from the box's corners, not from Size: a diagonal spans further than any single
        // axis, and measuring along one would stop the cut short of the model.
        var bounds = new BoundingBox(Vector3.Zero, new Vector3(10, 10, 10));
        var diagonal = Vector3.Normalize(Vector3.One);

        var start = bounds.SectionAt(diagonal, 0f);
        var end = bounds.SectionAt(diagonal, 1f);

        // The extent along (1,1,1)/sqrt(3) is 10*sqrt(3) = 17.32, not 10.
        Assert.Equal(10f * MathF.Sqrt(3f), MathF.Abs(end.D - start.D), 2);
    }

    [Fact]
    public void Sectioning_an_empty_model_produces_a_usable_plane_rather_than_a_nan()
    {
        var plane = BoundingBox.Empty.SectionAt(Vector3.UnitZ, 0.5f);

        Assert.False(float.IsNaN(plane.D));
        Assert.Equal(1f, plane.Normal.Length(), 4);
    }

    [Fact]
    public void A_zero_normal_does_not_produce_a_degenerate_plane()
    {
        var plane = new BoundingBox(Vector3.Zero, Vector3.One).SectionAt(Vector3.Zero, 0.5f);

        Assert.Equal(1f, plane.Normal.Length(), 4);
        Assert.False(float.IsNaN(plane.D));
    }

    // ---- containment ------------------------------------------------------------------------

    private static BoundingBox Extent(float x, float y, float z) =>
        new(Vector3.Zero, new Vector3(x, y, z));

    [Fact]
    public void Something_that_fits_within_the_limits_reports_nothing()
    {
        Assert.Null(MeshAnalysis.ExceedsLimits(Extent(200, 190, 200), new Vector3(220, 220, 250)));
    }

    [Fact]
    public void Something_that_only_fits_when_turned_still_counts_as_fitting()
    {
        // Most things resting on a surface can be turned on it, so the two horizontal extents are
        // compared both ways round. Refusing a 200x100 object on a 120x220 area would be wrong.
        Assert.Null(MeshAnalysis.ExceedsLimits(Extent(200, 100, 50), new Vector3(120, 220, 250)));
    }

    [Fact]
    public void Height_cannot_be_traded_for_footprint()
    {
        // Turning an object changes which horizontal axis is which; it does not make it shorter. So
        // height is compared directly, and the message says by how much.
        var problem = MeshAnalysis.ExceedsLimits(Extent(50, 50, 400), new Vector3(220, 220, 250));

        Assert.NotNull(problem);
        Assert.Contains("height", problem);
        Assert.Contains("150", problem);   // 400 - 250
    }

    [Fact]
    public void Something_too_wide_in_both_orientations_is_reported()
    {
        var problem = MeshAnalysis.ExceedsLimits(Extent(300, 300, 10), new Vector3(220, 220, 250));

        Assert.NotNull(problem);
        Assert.Contains("footprint", problem);
    }

    [Fact]
    public void The_up_axis_decides_which_extent_is_the_height()
    {
        // The same object against the same limits, diagnosed differently depending on which axis is
        // up — which is the whole reason the axis is a parameter. Something 400 long in Y is 400 tall
        // to a Y-up model and 400 WIDE to a Z-up one, and those are different problems.
        //
        // The first version of this test asserted the Z-up case fits, which was simply wrong: 400
        // across a 220 limit does not fit whichever way you turn it.
        var thing = Extent(50, 400, 50);
        var limits = new Vector3(220, 220, 500);

        var asZUp = MeshAnalysis.ExceedsLimits(thing, limits, UpAxis.Z);
        var asYUp = MeshAnalysis.ExceedsLimits(thing, limits, UpAxis.Y);

        Assert.Contains("footprint", asZUp);   // 400 lies flat across the region
        Assert.DoesNotContain("height", asZUp);
        Assert.Contains("height", asYUp);      // 400 stands up out of it
        Assert.DoesNotContain("footprint", asYUp);
    }
}
