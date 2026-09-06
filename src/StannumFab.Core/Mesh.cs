using System.Numerics;

namespace StannumFab;

/// <summary>What the indices describe. Only triangles are rendered today; the others exist so a
/// loader can carry what a file actually contained rather than silently converting it.</summary>
public enum PrimitiveKind
{
    /// <summary>Every three indices are one triangle.</summary>
    Triangles,
    /// <summary>Every two indices are one line segment. Toolpaths and wireframes.</summary>
    Lines,
    /// <summary>Every index is one point. Scans and point clouds.</summary>
    Points,
}

/// <summary>
/// Geometry, as parallel channels rather than one fat interleaved vertex.
///
/// <para><b>Channels, because the alternative is expensive in exactly the case that matters.</b> A
/// fixed vertex struct carrying position, normal, UV and colour costs 48 bytes whether or not a file
/// supplied any of them — and the format this engine exists to open first, STL, supplies only
/// position and a per-facet normal. On a ten-million-triangle print that difference is well over a
/// gigabyte of zeroes. A null channel here costs one null reference.</para>
///
/// <para>The layout also maps one-to-one onto GL attribute buffers, so the renderer uploads what is
/// present and binds nothing for what is not — no repacking step, and no shader permutation for
/// "this file had no UVs".</para>
///
/// <para><b>Always indexed.</b> A loader that receives unindexed data (STL again) indexes it, which
/// is what <see cref="MeshWelder"/> is for. Downstream code therefore never has two paths.</para>
/// </summary>
public sealed class Mesh
{
    /// <summary>Vertex positions. The only required channel — a mesh with no positions is not a
    /// mesh.</summary>
    public required Vector3[] Positions { get; init; }

    /// <summary>Indices into every channel. Length is a multiple of 3 for
    /// <see cref="PrimitiveKind.Triangles"/>.</summary>
    public required int[] Indices { get; init; }

    /// <summary>Per-vertex normals, or null when the file supplied none and none were computed.
    /// A renderer without them can still shade flat from the face plane.</summary>
    public Vector3[]? Normals { get; init; }

    /// <summary>Texture coordinates, or null. Only the first UV set; a second is rare outside
    /// lightmapping, which this engine does not do.</summary>
    public Vector2[]? Uvs { get; init; }

    /// <summary>Per-vertex colour, straight (not premultiplied) RGBA in 0..1, or null. Carried
    /// because 3MF, PLY and coloured scans use it and dropping it silently would be the kind of
    /// quiet loss this engine is meant not to inflict.</summary>
    public Vector4[]? Colors { get; init; }

    /// <summary>Tangent frame for normal mapping: xyz along the texture's U axis, w the handedness
    /// that says whether the texture is mirrored here. Null unless a normal map needs it — see
    /// <see cref="MeshTangents"/>.</summary>
    public Vector4[]? Tangents { get; init; }

    /// <summary>What the indices describe.</summary>
    public PrimitiveKind Kind { get; init; } = PrimitiveKind.Triangles;

    /// <summary>Optional name from the file, for diagnostics and for pick lists.</summary>
    public string? Name { get; init; }

    /// <summary>Vertices in this mesh.</summary>
    public int VertexCount => Positions.Length;

    /// <summary>Triangles, or 0 when this is not a triangle mesh.</summary>
    public int TriangleCount => Kind == PrimitiveKind.Triangles ? Indices.Length / 3 : 0;

    /// <summary>The axis-aligned bounds in this mesh's own space, computed once on first ask.
    /// Every viewer needs it immediately — to frame the camera — and recomputing it per frame over
    /// millions of vertices is a real cost.</summary>
    public BoundingBox Bounds => _bounds ??= BoundingBox.FromPoints(Positions);
    private BoundingBox? _bounds;

    /// <summary>
    /// Check the invariants a renderer relies on, and say precisely which one failed.
    ///
    /// <para>Called by the loaders before they hand a mesh back, so a malformed file fails at the
    /// parse with a sentence about the file rather than deep inside a draw call with a driver error.
    /// An out-of-range index is the common one and is genuinely dangerous: GL will happily read
    /// whatever is past the end of a buffer.</para>
    /// </summary>
    /// <returns>Null when the mesh is sound, otherwise the reason it is not.</returns>
    public string? Validate()
    {
        if (Positions.Length == 0) return "the mesh has no vertices";

        var perPrimitive = Kind switch { PrimitiveKind.Triangles => 3, PrimitiveKind.Lines => 2, _ => 1 };
        if (Indices.Length % perPrimitive != 0)
            return $"{Indices.Length} indices is not a whole number of {Kind}";

        foreach (var i in Indices)
            if ((uint)i >= (uint)Positions.Length)
                return $"index {i} is outside the {Positions.Length} vertices";

        if (Normals is { } n && n.Length != Positions.Length)
            return $"{n.Length} normals for {Positions.Length} vertices";
        if (Uvs is { } t && t.Length != Positions.Length)
            return $"{t.Length} texture coordinates for {Positions.Length} vertices";
        if (Colors is { } c && c.Length != Positions.Length)
            return $"{c.Length} colours for {Positions.Length} vertices";
        if (Tangents is { } g && g.Length != Positions.Length)
            return $"{g.Length} tangents for {Positions.Length} vertices";

        return null;
    }

    /// <summary>
    /// Smooth per-vertex normals, area-weighted, for a mesh that has none.
    ///
    /// <para>Area weighting rather than a plain average: an unweighted mean lets a fan of tiny
    /// slivers outvote the one large face a vertex actually belongs to, which shows up as shading
    /// that ripples along a tessellated curve. The cross product's length IS twice the triangle
    /// area, so weighting costs nothing — it is simply not normalising early.</para>
    ///
    /// <para><b>This will smooth a cube's corners.</b> That is correct for an organic model and
    /// wrong for a mechanical one, which is why loaders that know the geometry is faceted — STL —
    /// weld with an angle threshold instead of calling this. See <see cref="MeshWelder"/>.</para>
    /// </summary>
    public Vector3[] ComputeSmoothNormals()
    {
        var normals = new Vector3[Positions.Length];
        if (Kind != PrimitiveKind.Triangles) return normals;

        for (var i = 0; i + 2 < Indices.Length; i += 3)
        {
            int a = Indices[i], b = Indices[i + 1], c = Indices[i + 2];
            var face = Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
            normals[a] += face;
            normals[b] += face;
            normals[c] += face;
        }

        for (var i = 0; i < normals.Length; i++)
        {
            var length = normals[i].Length();
            // A vertex used only by degenerate triangles has no defined normal. Up is a lie, but it
            // is a stable lie that shades rather than producing NaNs that propagate into the depth
            // buffer and take the whole draw with them.
            normals[i] = length > 1e-12f ? normals[i] / length : Vector3.UnitY;
        }
        return normals;
    }
}
