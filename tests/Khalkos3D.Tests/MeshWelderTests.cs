using System.Numerics;
using Xunit;

namespace Khalkos3D.Tests;

/// <summary>
/// Welding, which every format that stores triangle soup depends on: an STL has no shared vertices
/// at all, so everything downstream — memory, upload time, vertex shading — is decided here.
/// </summary>
public class MeshWelderTests
{
    [Fact]
    public void A_box_welds_to_twenty_four_vertices_not_eight()
    {
        // THE ASSERTION THAT MATTERS, and the one a naive welder fails. A box has 8 corners, so 8
        // is the tempting answer — and it is wrong. Each corner is shared by three faces meeting at
        // ninety degrees, which is far past any sane smoothing angle, so each must stay split or the
        // corner shades round. 8 corners x 3 faces = 24.
        var mesh = MeshWelder.FromTriangleSoup(TestShapes.Box(new Vector3(10, 20, 30)), out var report);

        Assert.Equal(36, report.VerticesBefore);
        Assert.Equal(24, mesh.VertexCount);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(36, mesh.Indices.Length);
        // 24 = 8 corners created first-come + 16 extras forced by the angle. The invariant worth
        // holding on to: vertices after welding equals unique positions plus hard-edge splits.
        Assert.Equal(16, report.HardEdgeSplits);
        Assert.Equal(8 + report.HardEdgeSplits, mesh.VertexCount);
    }

    [Fact]
    public void Every_welded_box_normal_stays_axis_aligned()
    {
        // The consequence of the split, and what a user would actually see: each face keeps its own
        // flat normal. A welder that merged the corners would average three perpendicular normals
        // into a diagonal and light the box like a sphere.
        var mesh = MeshWelder.FromTriangleSoup(TestShapes.Box(Vector3.One), out _);

        foreach (var n in mesh.Normals!)
        {
            var longest = MathF.Max(MathF.Abs(n.X), MathF.Max(MathF.Abs(n.Y), MathF.Abs(n.Z)));
            Assert.True(longest > 0.999f,
                $"normal {n} is not axis-aligned, so a face was smoothed into its neighbour");
        }
    }

    [Fact]
    public void A_smoothing_angle_of_one_eighty_welds_everything()
    {
        // The escape hatch: told that nothing is a hard edge, the same box collapses to its eight
        // real corners. Proves the split is driven by the angle rather than by an inability to merge.
        var mesh = MeshWelder.FromTriangleSoup(TestShapes.Box(Vector3.One), out var report, smoothingAngle: 180f);

        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(0, report.HardEdgeSplits);
        Assert.Equal(12, mesh.TriangleCount);
    }

    [Fact]
    public void A_flat_sheet_welds_completely_because_nothing_is_sharp()
    {
        // Two coplanar triangles sharing an edge: zero degrees between them, so the shared corners
        // merge at any threshold. 6 corners in, 4 out.
        Vector3[] quad =
        [
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0),
            new(0, 0, 0), new(1, 1, 0), new(0, 1, 0),
        ];
        var mesh = MeshWelder.FromTriangleSoup(quad, out var report);

        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(0, report.HardEdgeSplits);
        foreach (var n in mesh.Normals!) Assert.True(MathF.Abs(n.Z - 1f) < 1e-5f, $"expected +Z, got {n}");
    }

    [Fact]
    public void A_uv_seam_is_never_welded_across()
    {
        // Two triangles sharing an edge in space but not in texture space — the definition of a
        // seam. Merging them would wrap the texture around the model instead of across it, and the
        // artefact is a smeared band rather than anything that looks like a bug.
        Vector3[] positions =
        [
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0),
            new(0, 0, 0), new(1, 1, 0), new(0, 1, 0),
        ];
        Vector2[] uvs =
        [
            new(0, 0), new(1, 0), new(1, 1),
            new(0.5f, 0.5f), new(0, 0), new(0, 1),   // the shared corners disagree
        ];
        var indices = new int[6];
        for (var i = 0; i < 6; i++) indices[i] = i;

        var welded = MeshWelder.Weld(new Mesh { Positions = positions, Indices = indices, Uvs = uvs }, out _);

        Assert.Equal(6, welded.VertexCount);
        Assert.Null(welded.Validate());
    }

    [Fact]
    public void Welding_leaves_a_mesh_a_renderer_can_trust()
    {
        var mesh = MeshWelder.FromTriangleSoup(TestShapes.Box(new Vector3(3, 4, 5)), out _);

        Assert.Null(mesh.Validate());
        Assert.Equal(new Vector3(3, 4, 5), mesh.Bounds.Size);
        Assert.Equal(new Vector3(1.5f, 2f, 2.5f), mesh.Bounds.Center);
    }

    [Fact]
    public void A_degenerate_triangle_produces_a_usable_normal_rather_than_a_nan()
    {
        // Zero-area triangles are common in exported STL. Their cross product is the zero vector, so
        // a naive normalise yields NaN — which propagates through the vertex shader into the depth
        // buffer and takes the whole draw call with it, silently.
        Vector3[] degenerate = [new(1, 1, 1), new(1, 1, 1), new(1, 1, 1)];
        var mesh = MeshWelder.FromTriangleSoup(degenerate, out _);

        foreach (var n in mesh.Normals!)
        {
            Assert.False(float.IsNaN(n.X) || float.IsNaN(n.Y) || float.IsNaN(n.Z), $"NaN normal {n}");
            Assert.True(MathF.Abs(n.Length() - 1f) < 1e-5f, $"normal {n} is not unit length");
        }
    }

    [Fact]
    public void Lines_and_points_are_passed_through_rather_than_reinterpreted()
    {
        // A toolpath has no faces, so it has no angles to compare. Welding it as if it did would
        // silently turn a G-code preview into nonsense.
        var mesh = new Mesh
        {
            Positions = [new(0, 0, 0), new(1, 0, 0), new(1, 0, 0), new(2, 0, 0)],
            Indices = [0, 1, 2, 3],
            Kind = PrimitiveKind.Lines,
        };
        var result = MeshWelder.Weld(mesh, out var report);

        Assert.Same(mesh, result);
        Assert.Equal(4, report.VerticesAfter);
    }
}
