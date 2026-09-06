using System.Numerics;

namespace Khalkos3D;

/// <summary>
/// What a mesh measures, and what its topology is.
///
/// <para>Lengths are in the mesh's own units, so area is those squared and volume those cubed. Only
/// some formats state what the unit is — a 3MF does, an STL does not — which is why
/// <see cref="Scene"/> carries the distinction rather than this type assuming one.</para>
/// </summary>
/// <param name="Triangles">Triangles in the mesh.</param>
/// <param name="Vertices">Vertices after merging coincident positions — the topological count,
/// which is usually smaller than a render mesh's.</param>
/// <param name="Size">Bounding box dimensions.</param>
/// <param name="SurfaceArea">Total area of every triangle.</param>
/// <param name="Volume">Signed volume enclosed by the surface. Only meaningful when
/// <see cref="IsClosedManifold"/>; the sign is what reveals <see cref="IsInverted"/>.</param>
/// <param name="BoundaryEdges">Edges belonging to exactly one triangle. The surface has a border
/// there rather than continuing — a hole, or an intentionally open sheet.</param>
/// <param name="NonManifoldEdges">Edges shared by three or more triangles, where the surface
/// branches and has no consistent two sides.</param>
/// <param name="MisorientedEdges">Edges whose two triangles traverse them the same way round, so the
/// two disagree about which side faces outward.</param>
/// <param name="DegenerateTriangles">Triangles with no area.</param>
public readonly record struct MeshReport(
    int Triangles,
    int Vertices,
    Vector3 Size,
    float SurfaceArea,
    float Volume,
    int BoundaryEdges,
    int NonManifoldEdges,
    int MisorientedEdges,
    int DegenerateTriangles)
{
    /// <summary>
    /// True when the surface is a closed, consistently oriented 2-manifold: every edge belongs to
    /// exactly two triangles, and those two traverse it in opposite directions.
    ///
    /// <para>The property a great many operations quietly assume. Volume is only defined for such a
    /// surface; so are inside/outside tests, boolean operations, offsetting, and any solid
    /// interpretation of the geometry. A mesh failing it can still be displayed perfectly well — most
    /// of what gets loaded is a surface, not a solid — but anything treating it as enclosing a region
    /// is working on an assumption the geometry does not support.</para>
    /// </summary>
    public bool IsClosedManifold => BoundaryEdges == 0 && NonManifoldEdges == 0 && MisorientedEdges == 0;

    /// <summary>
    /// True when the surface is closed but wound inside out — every face's outward direction points
    /// into the enclosed region rather than away from it.
    ///
    /// <para>Detected from the SIGN of the volume, which is the only reliable way: such a mesh is a
    /// perfectly good closed manifold, and once a renderer flips normals towards the viewer it looks
    /// correct from outside. Nothing else gives it away, and anything reasoning about which side is
    /// inside will get the opposite answer.</para>
    /// </summary>
    public bool IsInverted => Volume < 0f && IsClosedManifold;

    /// <summary>Enclosed volume as a positive number, whichever way the surface is wound.</summary>
    public float AbsoluteVolume => MathF.Abs(Volume);

    /// <summary>A sentence for a status bar.</summary>
    public override string ToString() =>
        $"{Triangles:N0} triangles, {Size.X:0.##} x {Size.Y:0.##} x {Size.Z:0.##}, " +
        (IsClosedManifold ? $"closed manifold, volume {AbsoluteVolume:0.##}" : Problem());

    /// <summary>What is irregular about the topology, or null when nothing is.</summary>
    public string? Problem()
    {
        if (IsClosedManifold) return IsInverted ? "wound inside out" : null;
        var parts = new List<string>(3);
        if (BoundaryEdges > 0)
            parts.Add($"{BoundaryEdges:N0} boundary edge{(BoundaryEdges == 1 ? "" : "s")}");
        if (NonManifoldEdges > 0)
            parts.Add($"{NonManifoldEdges:N0} non-manifold edge{(NonManifoldEdges == 1 ? "" : "s")}");
        if (MisorientedEdges > 0)
            parts.Add($"{MisorientedEdges:N0} edge{(MisorientedEdges == 1 ? "" : "s")} with inconsistent winding");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Measures a mesh and reports its topology.
///
/// <para>A diagnostic rather than a judgement. It says what the geometry IS — closed or bordered,
/// manifold or branching, consistently wound or not — and leaves what that means to the caller,
/// because it depends entirely on what the mesh is for. An open sheet is a defect in something meant
/// to enclose a volume and completely correct in a terrain or a cloth.</para>
///
/// <para><b>Topology is computed on positions, not on indices, and that is the trap.</b> A mesh
/// prepared for RENDERING has extra vertices wherever a hard edge needed a separate normal — see
/// <see cref="MeshWelder"/>, which splits a cube's eight corners into twenty-four. Matching edges by
/// index on such a mesh reports every single edge of a perfectly sound cube as a boundary. So this
/// merges coincident positions first and analyses the result, which is the surface the geometry
/// actually describes.</para>
/// </summary>
public static class MeshAnalysis
{
    /// <summary>Measure a mesh.</summary>
    /// <param name="mesh">Geometry to analyse. Untouched.</param>
    /// <param name="positionTolerance">Positions closer than this are treated as the same point. 0
    /// compares them exactly, which is right for anything that came from one source; raise it for
    /// geometry assembled from separately exported parts, where a shared corner may differ in the
    /// last bit.</param>
    public static MeshReport Analyse(Mesh mesh, float positionTolerance = 0f)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Kind != PrimitiveKind.Triangles)
            return new MeshReport(0, mesh.VertexCount, mesh.Bounds.Size, 0f, 0f, 0, 0, 0, 0);

        // Merge coincident positions to recover the surface's real topology. See the class remarks:
        // skipping this reports every edge of a sound cube as a boundary.
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

        // Directed edge counts. On a closed, consistently oriented surface each edge appears once in
        // each direction; every other pattern is one of the three irregularities reported.
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
            // closed surface these cancel to the enclosed volume, wherever the origin is — and the
            // SIGN is what reveals a surface wound inside out.
            volume += Vector3.Dot(pa, Vector3.Cross(pb, pc)) / 6.0;

            Count(a, b); Count(b, c); Count(c, a);
        }

        int boundary = 0, nonManifold = 0, misoriented = 0;
        var seen = new HashSet<(int, int)>();
        foreach (var ((from, to), forward) in directed)
        {
            var key = from < to ? (from, to) : (to, from);
            if (!seen.Add(key)) continue;

            directed.TryGetValue((to, from), out var backward);
            var total = forward + backward;

            if (total == 1) boundary++;
            else if (total > 2) nonManifold++;
            // Two triangles traversing the edge the same way disagree about which side faces
            // outward. A distinct condition from a boundary, and a distinct repair.
            else if (forward == 2 || backward == 2) misoriented++;
        }

        return new MeshReport(
            triangles, unique, mesh.Bounds.Size,
            (float)area, (float)volume,
            boundary, nonManifold, misoriented, degenerate);

        void Count(int from, int to) =>
            directed[(from, to)] = directed.TryGetValue((from, to), out var n) ? n + 1 : 1;

        float Snap(float value) => MathF.Round(value / positionTolerance) * positionTolerance;
    }

    /// <summary>Measure every mesh in a scene as one surface, with node transforms applied.</summary>
    public static MeshReport Analyse(Scene scene, float positionTolerance = 0f)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // Flattened into one mesh first, because two objects meeting along a shared wall would
        // otherwise be reported as two bordered surfaces when together they are one closed one.
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
    /// Does this fit inside a box of the given size?
    ///
    /// <para>A containment test, and the limits are whatever the caller's are — a working volume, a
    /// shipping carton, a display case, a level's playable area. It is here rather than on
    /// <see cref="BoundingBox"/> because it returns a DIAGNOSTIC rather than a boolean: knowing that
    /// something does not fit is much less useful than knowing by how much, and in which dimension.</para>
    /// </summary>
    /// <param name="bounds">What to fit, in the same units as the limits.</param>
    /// <param name="limits">Maximum extent along each axis.</param>
    /// <param name="up">Which axis is vertical, so the right extent is treated as height.</param>
    /// <param name="allowTurning">Whether the object may be rotated a quarter turn about the up axis,
    /// which swaps its two horizontal extents. True by default: most things that sit on a surface can
    /// be turned on it, and refusing a 200x100 object on a 120x220 area would be wrong.</param>
    /// <returns>Null when it fits, otherwise which dimension it exceeds and by how much.</returns>
    public static string? ExceedsLimits(BoundingBox bounds, Vector3 limits,
                                        UpAxis up = UpAxis.Z, bool allowTurning = true)
    {
        if (bounds.IsEmpty) return null;
        var size = bounds.Size;

        var (planA, planB, height) = up == UpAxis.Z
            ? (size.X, size.Y, size.Z)
            : (size.X, size.Z, size.Y);
        var (limitA, limitB, limitHeight) = up == UpAxis.Z
            ? (limits.X, limits.Y, limits.Z)
            : (limits.X, limits.Z, limits.Y);

        var fitsAsIs = planA <= limitA && planB <= limitB;
        // Turning swaps the two horizontal extents. It cannot make the object shorter, which is why
        // height is compared separately and unconditionally.
        var fitsTurned = allowTurning && planA <= limitB && planB <= limitA;
        var problems = new List<string>(2);

        if (!fitsAsIs && !fitsTurned)
            problems.Add($"footprint {planA:0.#} x {planB:0.#} exceeds the {limitA:0.#} x {limitB:0.#} limit");
        if (height > limitHeight)
            problems.Add($"height {height:0.#} exceeds the {limitHeight:0.#} limit by {height - limitHeight:0.#}");

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
