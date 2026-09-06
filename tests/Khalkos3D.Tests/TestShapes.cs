using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Khalkos3D.Tests;

/// <summary>
/// Geometry and files built in code rather than checked in as binaries.
///
/// <para>A box is the right shape to test welding against because its answer is counter-intuitive
/// and exact: eight corners, but twenty-four vertices after a correct weld, because every corner is
/// shared by three faces that meet at ninety degrees and must stay split to shade flat. A test that
/// expected eight would be asserting the bug.</para>
/// </summary>
internal static class TestShapes
{
    /// <summary>A box from the origin to <paramref name="size"/>, as unindexed triangle corners with
    /// outward-facing winding — 12 triangles, 36 vertices, exactly what an STL contains.</summary>
    internal static Vector3[] Box(Vector3 size)
    {
        float x = size.X, y = size.Y, z = size.Z;
        Vector3 v0 = new(0, 0, 0), v1 = new(x, 0, 0), v2 = new(x, y, 0), v3 = new(0, y, 0);
        Vector3 v4 = new(0, 0, z), v5 = new(x, 0, z), v6 = new(x, y, z), v7 = new(0, y, z);

        return
        [
            v4, v5, v6,  v4, v6, v7,   // +Z
            v0, v3, v2,  v0, v2, v1,   // -Z
            v1, v2, v6,  v1, v6, v5,   // +X
            v0, v4, v7,  v0, v7, v3,   // -X
            v3, v7, v6,  v3, v6, v2,   // +Y
            v0, v1, v5,  v0, v5, v4,   // -Y
        ];
    }

    /// <summary>A binary STL. <paramref name="header"/> fills the 80-byte header — pass "solid …" to
    /// reproduce the exporters whose binary files begin with the ASCII keyword.</summary>
    internal static byte[] BinaryStl(ReadOnlySpan<Vector3> corners, string header = "Khalkos3D test",
                                     ushort attribute = 0)
    {
        var triangles = corners.Length / 3;
        var bytes = new byte[80 + 4 + triangles * 50];

        var headerBytes = Encoding.ASCII.GetBytes(header);
        headerBytes.AsSpan(0, Math.Min(80, headerBytes.Length)).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), (uint)triangles);

        var at = 84;
        for (var t = 0; t < triangles; t++)
        {
            // Normal left as zero on purpose: real files often do, and the reader must not depend
            // on it. Geometry decides the normals.
            at += 12;
            for (var v = 0; v < 3; v++)
            {
                var p = corners[t * 3 + v];
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), p.X);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 4), p.Y);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 8), p.Z);
                at += 12;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), attribute);
            at += 2;
        }
        return bytes;
    }

    /// <summary>An ASCII STL.</summary>
    internal static byte[] AsciiStl(ReadOnlySpan<Vector3> corners, string name = "test")
    {
        var text = new StringBuilder();
        text.Append("solid ").Append(name).Append('\n');
        for (var t = 0; t + 2 < corners.Length; t += 3)
        {
            text.Append("  facet normal 0 0 0\n    outer loop\n");
            for (var v = 0; v < 3; v++)
            {
                var p = corners[t + v];
                text.Append(CultureInfo.InvariantCulture,
                    $"      vertex {p.X:R} {p.Y:R} {p.Z:R}\n");
            }
            text.Append("    endloop\n  endfacet\n");
        }
        text.Append("endsolid ").Append(name).Append('\n');
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
