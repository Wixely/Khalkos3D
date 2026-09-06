using System.Numerics;

namespace Khalkos3D;

/// <summary>
/// Works out the tangent frame a normal map needs.
///
/// <para>A normal map stores directions in TANGENT SPACE — relative to the surface, along the
/// texture's own axes — which is what lets one map be reused on any part of any model. Turning those
/// into world-space normals needs to know where the texture's U and V axes point on the surface, and
/// that is what a tangent is. Without one, a normal map cannot be applied at all.</para>
///
/// <para>glTF files often supply tangents; plenty do not, and no other format this engine reads has
/// the concept. So they are derived from the geometry and the UVs, by the standard method: solve for
/// the direction in which U increases across each triangle, accumulate per vertex, then straighten
/// against the normal.</para>
/// </summary>
public static class MeshTangents
{
    /// <summary>
    /// Tangents for a mesh, as xyz plus a handedness in w.
    ///
    /// <para><b>The w is not padding.</b> It records whether the texture is mirrored on this part of
    /// the surface, which is extremely common — a symmetric model usually maps both halves to the
    /// same texture region, so one half's bitangent points the opposite way. Storing the sign and
    /// multiplying by it is what stops the lighting on the mirrored half coming out inverted, which
    /// looks like the surface detail is punched in rather than raised.</para>
    /// </summary>
    /// <returns>One per vertex, or null when the mesh has no UVs or no normals — in which case a
    /// normal map could not be applied anyway.</returns>
    public static Vector4[]? Generate(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Kind != PrimitiveKind.Triangles) return null;
        if (mesh.Uvs is not { } uvs || mesh.Normals is not { } normals) return null;
        if (uvs.Length != mesh.VertexCount || normals.Length != mesh.VertexCount) return null;

        var tangents = new Vector3[mesh.VertexCount];
        var bitangents = new Vector3[mesh.VertexCount];

        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int ia = mesh.Indices[i], ib = mesh.Indices[i + 1], ic = mesh.Indices[i + 2];

            var edge1 = mesh.Positions[ib] - mesh.Positions[ia];
            var edge2 = mesh.Positions[ic] - mesh.Positions[ia];
            var duv1 = uvs[ib] - uvs[ia];
            var duv2 = uvs[ic] - uvs[ia];

            var determinant = duv1.X * duv2.Y - duv2.X * duv1.Y;
            // Degenerate in texture space: the three corners share a UV line, so "the direction U
            // increases in" is undefined here. Contributing nothing is right — neighbouring
            // triangles will supply the frame for these vertices.
            if (MathF.Abs(determinant) < 1e-12f) continue;
            var r = 1f / determinant;

            var tangent = (edge1 * duv2.Y - edge2 * duv1.Y) * r;
            var bitangent = (edge2 * duv1.X - edge1 * duv2.X) * r;

            tangents[ia] += tangent; tangents[ib] += tangent; tangents[ic] += tangent;
            bitangents[ia] += bitangent; bitangents[ib] += bitangent; bitangents[ic] += bitangent;
        }

        var result = new Vector4[mesh.VertexCount];
        for (var i = 0; i < result.Length; i++)
        {
            var n = normals[i];
            var t = tangents[i];

            // Gram-Schmidt: remove the part of the tangent that points along the normal, leaving one
            // that genuinely lies in the surface. The accumulated sum rarely does on its own, because
            // neighbouring triangles are not coplanar.
            var straightened = t - n * Vector3.Dot(n, t);
            var length = straightened.Length();
            if (length < 1e-8f)
            {
                // No usable tangent — every triangle touching this vertex was degenerate in UV space.
                // Any perpendicular will do: the normal map will be wrong here, but the alternative
                // is a NaN that propagates through the whole lighting calculation.
                straightened = Vector3.Cross(n, MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
                length = straightened.Length();
                if (length < 1e-8f) { result[i] = new Vector4(1, 0, 0, 1); continue; }
            }
            straightened /= length;

            // Negative when the texture is mirrored across this vertex. See the remarks.
            var handedness = Vector3.Dot(Vector3.Cross(n, straightened), bitangents[i]) < 0f ? -1f : 1f;
            result[i] = new Vector4(straightened, handedness);
        }
        return result;
    }

    /// <summary>The same mesh with tangents attached, or unchanged when it already has them or
    /// cannot have them.</summary>
    public static Mesh WithTangents(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Tangents is not null) return mesh;
        if (Generate(mesh) is not { } tangents) return mesh;

        return new Mesh
        {
            Positions = mesh.Positions,
            Indices = mesh.Indices,
            Normals = mesh.Normals,
            Uvs = mesh.Uvs,
            Colors = mesh.Colors,
            Tangents = tangents,
            Kind = mesh.Kind,
            Name = mesh.Name,
        };
    }
}
