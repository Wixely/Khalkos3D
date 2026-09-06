using System.Numerics;

namespace Khalkos3D;

/// <summary>
/// What a mesh measures, and whether it is printable.
///
/// <para>Sizes are in the model's own units, which for anything that came through
/// <c>ThreeMfReader</c> are millimetres. An STL says nothing about its units, so it is whatever the
/// author meant — which is the reason a 3MF is worth preferring and the reason a viewer should say
/// which it is reporting.</para>
/// </summary>
/// <param name="Triangles">Triangles in the mesh.</param>
/// <param name="Vertices">Vertices after merging coincident positions — the topological count,
/// which is usually smaller than the render mesh's.</param>
/// <param name="Size">Bounding box dimensions.</param>
/// <param name="SurfaceArea">Total area of every triangle.</param>
/// <param name="Volume">Enclosed volume. Meaningful only when <see cref="IsWatertight"/>; see the
/// remarks on <see cref="MeshAnalysis"/> for what a negative one means.</param>
/// <param name="OpenEdges">Edges belonging to exactly one triangle — holes.</param>
/// <param name="NonManifoldEdges">Edges shared by three or more triangles.</param>
/// <param name="FlippedEdges">Edges whose two triangles traverse them the same way round, meaning
/// the two disagree about which side is outside.</param>
/// <param name="DegenerateTriangles">Triangles with no area.</param>
public readonly record struct MeshReport(
    int Triangles,
    int Vertices,
    Vector3 Size,
    float SurfaceArea,
    float Volume,
    int OpenEdges,
    int NonManifoldEdges,
    int FlippedEdges,
    int DegenerateTriangles)
{
    /// <summary>
    /// True when the mesh is a closed, consistently wound solid — the thing a slicer needs.
    ///
    /// <para>Every edge belongs to exactly two triangles, and those two traverse it in opposite
    /// directions. A mesh failing this may still slice: modern slicers repair aggressively. But it is
    /// the difference between a file that will behave and one that might, and it is the single most
    /// useful thing a viewer can tell someone before they wait four hours for a print.</para>
    /// </summary>
    public bool IsWatertight => OpenEdges == 0 && NonManifoldEdges == 0 && FlippedEdges == 0;

    /// <summary>
    /// True when the winding is inside out throughout — every normal points into the solid rather
    /// than out of it.
    ///
    /// <para>Detected from the SIGN of the volume, which is the only reliable way: a consistently
    /// inverted mesh is perfectly watertight and looks correct from outside once a renderer flips
    /// normals towards the viewer, so nothing else gives it away. A slicer may then fill what should
    /// be hollow and hollow what should be filled.</para>
    /// </summary>
    public bool IsInsideOut => Volume < 0f && IsWatertight;

    /// <summary>Enclosed volume as a positive number, whichever way the mesh is wound.</summary>
    public float AbsoluteVolume => MathF.Abs(Volume);

    /// <summary>A sentence for a status bar.</summary>
    public override string ToString() =>
        $"{Triangles:N0} triangles, {Size.X:0.##} x {Size.Y:0.##} x {Size.Z:0.##}, " +
        (IsWatertight ? $"watertight, volume {AbsoluteVolume:0.##}" : Problem());

    /// <summary>What is wrong, in the order a user would want to fix it, or null when nothing is.</summary>
    public string? Problem()
    {
        if (IsWatertight) return IsInsideOut ? "wound inside out" : null;
        var parts = new List<string>(3);
        if (OpenEdges > 0) parts.Add($"{OpenEdges:N0} open edge{(OpenEdges == 1 ? "" : "s")} (holes)");
        if (NonManifoldEdges > 0) parts.Add($"{NonManifoldEdges:N0} non-manifold edge{(NonManifoldEdges == 1 ? "" : "s")}");
        if (FlippedEdges > 0) parts.Add($"{FlippedEdges:N0} edge{(FlippedEdges == 1 ? "" : "s")} with inconsistent winding");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Measures a mesh and says whether it would print.
///
/// <para><b>Topology is computed on positions, not on indices, and that is the trap.</b> A mesh
/// prepared for RENDERING has extra vertices wherever a hard edge needed a separate normal — see
/// <see cref="MeshWelder"/>, which splits a cube's eight corners into twenty-four. Matching edges by
/// index on such a mesh reports every single edge of a perfectly sound cube as open. So this merges
/// coincident positions first and analyses the result, which is the shape a slicer sees.</para>
/// </summary>
public static class MeshAnalysis
{
    /// <summary>Measure a mesh.</summary>
    /// <param name="mesh">Geometry to analyse. Untouched.</param>
    /// <param name="positionTolerance">Positions closer than this are treated as the same point. 0
    /// compares them exactly, which is right for anything that came from one source; raise it for a
    /// file assembled from separately exported parts, where a shared corner may differ in the last
    /// bit.</param>
    public static MeshReport Analyse(Mesh mesh, float positionTolerance = 0f)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Kind != PrimitiveKind.Triangles)
            return new MeshReport(0, mesh.VertexCount, mesh.Bounds.Size, 0f, 0f, 0, 0, 0, 0);

        // Merge coincident positions to recover the topology a slicer would see. See the class
        // remarks: skipping this reports every edge of a sound cube as a hole.
        var canonical = new int[mesh.VertexCount];
        var lookup = new Dictionary<(float, float, float), int>(mesh.VertexCount);
        var unique = 0;
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var p = mesh.Positions[i];
            var key = positionTolerance > 0f
                ? (Snap(p.X), Snap(p.Y), Snap(p.Z))
                : (p.X, p.Y, p.Z);
            if (!lookup.TryGetValue(key, out var index)) lookup[key] = index = unique++;
            canonical[i] = index;
        }

        // Directed edge counts. An edge of a sound solid appears once in each direction; anything
        // else is one of the three defects reported.
        var directed = new Dictionary<(int, int), int>(mesh.Indices.Length);
        var triangles = 0;
        var degenerate = 0;
        double area = 0, volume = 0;

        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int a = canonical[mesh.Indices[i]],
                b = canonical[mesh.Indices[i + 1]],
                c = canonical[mesh.Indices[i + 2]];
            triangles++;

            Vector3 pa = mesh.Positions[mesh.Indices[i]],
                    pb = mesh.Positions[mesh.Indices[i + 1]],
                    pc = mesh.Positions[mesh.Indices[i + 2]];

            var cross = Vector3.Cross(pb - pa, pc - pa);
            var twiceArea = cross.Length();
            if (twiceArea < 1e-20f || a == b || b == c || a == c) { degenerate++; continue; }
            area += twiceArea * 0.5;

            // The signed volume of the tetrahedron from the origin to this triangle. Summed over a
            // closed surface these cancel to the enclosed volume, whatever the origin — and the SIGN
            // is what reveals a mesh wound inside out.
            volume += Vector3.Dot(pa, Vector3.Cross(pb, pc)) / 6.0;

            Count(a, b); Count(b, c); Count(c, a);
        }

        int open = 0, nonManifold = 0, flipped = 0;
        var seen = new HashSet<(int, int)>();
        foreach (var ((from, to), forward) in directed)
        {
            var key = from < to ? (from, to) : (to, from);
            if (!seen.Add(key)) continue;

            directed.TryGetValue((to, from), out var backward);
            var total = forward + backward;

            if (total == 1) open++;
            else if (total > 2) nonManifold++;
            // Two triangles that traverse the edge the same way disagree about which side is
            // outside. Distinct from a hole, and fixed differently.
            else if (forward == 2 || backward == 2) flipped++;
        }

        return new MeshReport(
            triangles, unique, mesh.Bounds.Size,
            (float)area, (float)volume,
            open, nonManifold, flipped, degenerate);

        void Count(int from, int to) =>
            directed[(from, to)] = directed.TryGetValue((from, to), out var n) ? n + 1 : 1;

        float Snap(float value) => MathF.Round(value / positionTolerance) * positionTolerance;
    }

    /// <summary>Measure every mesh in a scene as one part, with node transforms applied.</summary>
    public static MeshReport Analyse(Scene scene, float positionTolerance = 0f)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // Flattened into one mesh first, because a plate of separate objects sharing a wall would
        // otherwise be reported as two open surfaces when it is one closed solid.
        var positions = new List<Vector3>();
        var indices = new List<int>();
        foreach (var (mesh, world, _) in scene.Draws())
        {
            if (mesh.Kind != PrimitiveKind.Triangles) continue;
            var offset = positions.Count;
            foreach (var p in mesh.Positions) positions.Add(Vector3.Transform(p, world));
            foreach (var i in mesh.Indices) indices.Add(offset + i);
        }

        if (positions.Count == 0) return new MeshReport(0, 0, Vector3.Zero, 0f, 0f, 0, 0, 0, 0);
        return Analyse(new Mesh { Positions = [.. positions], Indices = [.. indices] }, positionTolerance);
    }

    /// <summary>
    /// Does this fit on a bed of the given size?
    /// </summary>
    /// <param name="bounds">The part, in the same units as the bed.</param>
    /// <param name="plate">Bed dimensions along the two ground axes, plus the height limit.</param>
    /// <param name="up">Which axis is up, so the height is compared against the right extent.</param>
    /// <returns>Null when it fits, otherwise which dimension it exceeds and by how much.</returns>
    public static string? ExceedsBuildVolume(BoundingBox bounds, Vector3 plate, UpAxis up = UpAxis.Z)
    {
        if (bounds.IsEmpty) return null;
        var size = bounds.Size;

        // The part can be rotated on the plate, so the two ground extents are compared against the
        // bed either way round. Height cannot be traded for footprint, so it is compared directly.
        var (groundA, groundB, height) = up == UpAxis.Z
            ? (size.X, size.Y, size.Z)
            : (size.X, size.Z, size.Y);
        var (bedA, bedB, limit) = up == UpAxis.Z
            ? (plate.X, plate.Y, plate.Z)
            : (plate.X, plate.Z, plate.Y);

        var fitsSquare = groundA <= bedA && groundB <= bedB;
        var fitsTurned = groundA <= bedB && groundB <= bedA;
        var problems = new List<string>(2);

        if (!fitsSquare && !fitsTurned)
            problems.Add($"footprint {groundA:0.#} x {groundB:0.#} exceeds the {bedA:0.#} x {bedB:0.#} bed");
        if (height > limit)
            problems.Add($"height {height:0.#} exceeds the {limit:0.#} limit by {height - limit:0.#}");

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
