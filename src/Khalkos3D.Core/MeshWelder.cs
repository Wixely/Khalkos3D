using System.Numerics;

namespace Khalkos3D;

/// <summary>What welding achieved. Worth reporting rather than hiding: on a large mesh the ratio is
/// big enough that anyone watching a progress bar deserves to know why the number moved.</summary>
/// <param name="VerticesBefore">Vertices in the input.</param>
/// <param name="VerticesAfter">Vertices in the welded mesh.</param>
/// <param name="HardEdgeSplits">EXTRA vertices created because faces meeting at a shared position
/// exceeded the smoothing angle — the memory that sharpness costs. A box reports 16: eight corners,
/// each needing three vertices, of which two per corner are the extras. Not the number of sharp
/// corners, which is a different question and a less useful one.</param>
public readonly record struct WeldReport(int VerticesBefore, int VerticesAfter, int HardEdgeSplits)
{
    /// <summary>Fraction of vertices removed, 0..1.</summary>
    public float Reduction => VerticesBefore == 0 ? 0f : 1f - (float)VerticesAfter / VerticesBefore;

    /// <inheritdoc/>
    public override string ToString() =>
        $"{VerticesBefore:N0} to {VerticesAfter:N0} vertices ({Reduction:P0} smaller), " +
        $"{HardEdgeSplits:N0} kept sharp";
}

/// <summary>
/// Turns triangle soup into an indexed mesh, keeping sharp edges sharp.
///
/// <para><b>This exists because of STL, and STL is the format this engine opens first.</b> An STL
/// file has no concept of a shared vertex: it is a flat list of facets, each carrying its own three
/// corners, so a cube arrives as 36 vertices rather than 8 and a million-triangle model arrives as
/// three million. Uploading that is three times the memory and three times the vertex shading for
/// nothing.</para>
///
/// <para><b>Welding naively then smoothing is the trap.</b> Merge every coincident position, average
/// the normals, and a cube's corners round off — which is exactly wrong for the mechanical parts
/// that dominate engineering geometry. So the merge is conditional: two corners at the same position are only
/// joined if their faces meet within <c>smoothingAngle</c>. A 90-degree cube corner exceeds any
/// sane threshold and stays three separate vertices, so it renders flat; a finely tessellated
/// cylinder falls well inside it and renders smooth. One pass, both behaviours, no per-model
/// tuning.</para>
///
/// <para>Vertices are also kept apart when their texture coordinates or colours differ, because a
/// UV seam is a real discontinuity: merging across it would stretch the texture around the model
/// instead of wrapping it.</para>
/// </summary>
public static class MeshWelder
{
    /// <summary>The default smoothing angle, in degrees. Chosen so that a cylinder tessellated to
    /// 12 segments (30 degrees between facets) still smooths, while anything more angular does not
    /// — which puts the boundary just past the coarsest curve worth smoothing.</summary>
    public const float DefaultSmoothingAngle = 32f;

    /// <summary>
    /// Weld <paramref name="mesh"/>, merging coincident corners whose faces meet within
    /// <paramref name="smoothingAngle"/> degrees.
    /// </summary>
    /// <param name="mesh">Input. Untouched; a new mesh is returned.</param>
    /// <param name="report">What it achieved.</param>
    /// <param name="smoothingAngle">Above this angle between two faces, their shared corner stays
    /// split and the edge renders sharp. 0 welds nothing but position duplicates that already
    /// agree; 180 welds everything and smooths the whole model.</param>
    /// <param name="positionTolerance">Positions are snapped to this grid before comparison. 0 —
    /// the default — compares them as they are, which is what STL wants: the duplicates in an STL
    /// come from writing the SAME computed vertex three times, so they are usually bit-identical.
    /// Raise it for files that have been through a unit conversion or a lossy round trip.</param>
    /// <returns>An indexed mesh with smooth normals across the welded edges.</returns>
    public static Mesh Weld(Mesh mesh, out WeldReport report,
                            float smoothingAngle = DefaultSmoothingAngle,
                            float positionTolerance = 0f)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.Kind != PrimitiveKind.Triangles)
        {
            // Nothing to do, and pretending otherwise would silently reinterpret a toolpath as
            // geometry. Lines and points have no faces, so they have no angle to compare.
            report = new WeldReport(mesh.VertexCount, mesh.VertexCount, 0);
            return mesh;
        }

        var cosLimit = MathF.Cos(Math.Clamp(smoothingAngle, 0f, 180f) * MathF.PI / 180f);

        var positions = new List<Vector3>(mesh.VertexCount / 2);
        var normals = new List<Vector3>(mesh.VertexCount / 2);      // accumulated, normalised at the end
        var seeds = new List<Vector3>(mesh.VertexCount / 2);        // the face normal that created each vertex
        var uvs = mesh.Uvs is null ? null : new List<Vector2>(mesh.VertexCount / 2);
        var colors = mesh.Colors is null ? null : new List<Vector4>(mesh.VertexCount / 2);
        var indices = new int[mesh.Indices.Length];

        // Quantised position to the output vertices sitting there. A short list per cell: a
        // closed manifold shares each position between about six triangles, so these stay tiny
        // and the linear scan inside one is cheaper than anything cleverer.
        var cells = new Dictionary<(float, float, float), List<int>>(mesh.VertexCount / 4);
        var splits = 0;

        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int ia = mesh.Indices[i], ib = mesh.Indices[i + 1], ic = mesh.Indices[i + 2];
            Vector3 pa = mesh.Positions[ia], pb = mesh.Positions[ib], pc = mesh.Positions[ic];

            // The cross product's length is twice the triangle's area, so keeping it unnormalised
            // here is what makes the final normals area-weighted for free. A degenerate triangle
            // contributes a zero vector and therefore contributes nothing, which is correct.
            var faceArea2 = Vector3.Cross(pb - pa, pc - pa);
            var faceLength = faceArea2.Length();
            var faceUnit = faceLength > 1e-20f ? faceArea2 / faceLength : Vector3.Zero;

            indices[i] = Add(ia, pa, faceArea2, faceUnit);
            indices[i + 1] = Add(ib, pb, faceArea2, faceUnit);
            indices[i + 2] = Add(ic, pc, faceArea2, faceUnit);
        }

        var finalNormals = new Vector3[normals.Count];
        for (var i = 0; i < finalNormals.Length; i++)
        {
            var length = normals[i].Length();
            // Fall back to the seed: a vertex used only by degenerate triangles accumulated nothing,
            // and the face that created it is a better answer than an arbitrary axis.
            finalNormals[i] = length > 1e-20f ? normals[i] / length
                            : seeds[i] != Vector3.Zero ? seeds[i] : Vector3.UnitY;
        }

        report = new WeldReport(mesh.VertexCount, positions.Count, splits);
        return new Mesh
        {
            Positions = [.. positions],
            Indices = indices,
            Normals = finalNormals,
            Uvs = uvs is null ? null : [.. uvs],
            Colors = colors is null ? null : [.. colors],
            Kind = PrimitiveKind.Triangles,
            Name = mesh.Name,
        };

        int Add(int source, Vector3 position, Vector3 weighted, Vector3 unit)
        {
            var key = positionTolerance > 0f
                ? (Snap(position.X), Snap(position.Y), Snap(position.Z))
                : (position.X, position.Y, position.Z);

            if (!cells.TryGetValue(key, out var candidates))
                cells[key] = candidates = new List<int>(4);

            var refusedOnAngle = false;
            foreach (var candidate in candidates)
            {
                // Compared against the SEED — the face normal that first created this vertex — not
                // against the running accumulation, which drifts as faces are added and would make
                // the outcome depend on triangle order in a way nobody could predict.
                if (Vector3.Dot(seeds[candidate], unit) < cosLimit) { refusedOnAngle = true; continue; }
                if (uvs is not null && uvs[candidate] != mesh.Uvs![source]) continue;
                if (colors is not null && colors[candidate] != mesh.Colors![source]) continue;

                normals[candidate] += weighted;
                return candidate;
            }

            // Counted per VERTEX CREATED, not per candidate rejected: a corner with three faces
            // refuses twice on the second vertex and twice again on the third, and reporting four
            // would measure the search rather than the result.
            if (refusedOnAngle) splits++;

            positions.Add(position);
            normals.Add(weighted);
            seeds.Add(unit);
            uvs?.Add(mesh.Uvs![source]);
            colors?.Add(mesh.Colors![source]);
            var index = positions.Count - 1;
            candidates.Add(index);
            return index;
        }

        float Snap(float value) => MathF.Round(value / positionTolerance) * positionTolerance;
    }

    /// <summary>
    /// Build an indexed mesh from unindexed triangle corners — the shape every STL and every
    /// naive OBJ arrives in.
    /// </summary>
    /// <param name="corners">Three per triangle, in order.</param>
    /// <param name="report">What welding achieved.</param>
    /// <param name="smoothingAngle">See <see cref="Weld"/>.</param>
    /// <param name="name">Optional mesh name.</param>
    public static Mesh FromTriangleSoup(ReadOnlySpan<Vector3> corners, out WeldReport report,
                                        float smoothingAngle = DefaultSmoothingAngle,
                                        string? name = null)
    {
        var positions = corners.ToArray();
        var indices = new int[positions.Length];
        for (var i = 0; i < indices.Length; i++) indices[i] = i;
        return Weld(new Mesh { Positions = positions, Indices = indices, Name = name },
                    out report, smoothingAngle);
    }
}
