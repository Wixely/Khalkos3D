using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Khalkos3D.Tests;

/// <summary>Container formats built in code, so the suite carries no binary fixtures.</summary>
internal static class TestFiles
{
    /// <summary>A 3MF holding one mesh, in whatever unit is named.</summary>
    internal static byte[] ThreeMf(ReadOnlySpan<Vector3> corners, string unit = "millimeter",
                                   string? extraResources = null, string? build = null,
                                   string? trianglePropertyAttributes = null)
    {
        // The corners are written as shared vertices with sequential triangles — 3MF's own shape,
        // and the reason it needs no welding.
        var xml = new StringBuilder();
        xml.Append(CultureInfo.InvariantCulture,
            $"""<?xml version="1.0" encoding="UTF-8"?><model unit="{unit}" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02"><resources>""");
        if (extraResources is not null) xml.Append(extraResources);
        xml.Append("""<object id="1" type="model"><mesh><vertices>""");
        foreach (var p in corners)
            xml.Append(CultureInfo.InvariantCulture, $"""<vertex x="{p.X:R}" y="{p.Y:R}" z="{p.Z:R}"/>""");
        xml.Append("</vertices><triangles>");
        for (var i = 0; i + 2 < corners.Length; i += 3)
            xml.Append(CultureInfo.InvariantCulture,
                $"""<triangle v1="{i}" v2="{i + 1}" v3="{i + 2}"{trianglePropertyAttributes}/>""");
        xml.Append("</triangles></mesh></object></resources><build>");
        xml.Append(build ?? """<item objectid="1"/>""");
        xml.Append("</build></model>");

        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("3D/3dmodel.model");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(xml.ToString());
        }
        return buffer.ToArray();
    }

    /// <summary>A GLB holding one indexed triangle mesh, optionally declaring a required
    /// extension.</summary>
    internal static byte[] Glb(ReadOnlySpan<Vector3> positions, ReadOnlySpan<ushort> indices,
                               string? requiredExtension = null, string? extraJson = null,
                               string? topLevel = null, string? materials = null)
    {
        // Positions first, then indices, each aligned to 4 bytes as the specification wants.
        var positionBytes = positions.Length * 12;
        var indexBytes = indices.Length * 2;
        var indexOffset = (positionBytes + 3) / 4 * 4;
        var bin = new byte[(indexOffset + indexBytes + 3) / 4 * 4];

        for (var i = 0; i < positions.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(i * 12), positions[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(i * 12 + 4), positions[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(i * 12 + 8), positions[i].Z);
        }
        for (var i = 0; i < indices.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bin.AsSpan(indexOffset + i * 2), indices[i]);

        var min = positions.Length == 0 ? Vector3.Zero : positions[0];
        var max = min;
        foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }

        var required = requiredExtension is null ? "" : $"""  "extensionsRequired": ["{requiredExtension}"],""";
        var json = $$"""
        {
          "asset": { "version": "2.0" },
        {{required}}
          "buffers": [ { "byteLength": {{bin.Length}} } ],
          "bufferViews": [
            { "buffer": 0, "byteOffset": 0, "byteLength": {{positionBytes}} },
            { "buffer": 0, "byteOffset": {{indexOffset}}, "byteLength": {{indexBytes}} }
          ],
          "accessors": [
            { "bufferView": 0, "componentType": 5126, "count": {{positions.Length}}, "type": "VEC3",
              "min": [{{F(min.X)}},{{F(min.Y)}},{{F(min.Z)}}], "max": [{{F(max.X)}},{{F(max.Y)}},{{F(max.Z)}}] },
            { "bufferView": 1, "componentType": 5123, "count": {{indices.Length}}, "type": "SCALAR" }
          ],
          "meshes": [ { "name": "test", "primitives": [ { "attributes": { "POSITION": 0 }, "indices": 1{{materials}} } ] } ],
        {{topLevel}}
          "nodes": [ { "mesh": 0{{extraJson}} } ],
          "scenes": [ { "nodes": [0] } ],
          "scene": 0
        }
        """;

        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var jsonPadded = (jsonBytes.Length + 3) / 4 * 4;

        var glb = new byte[12 + 8 + jsonPadded + 8 + bin.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);            // "glTF"
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);  // "JSON"
        jsonBytes.CopyTo(glb.AsSpan(20));
        // JSON chunks pad with spaces, not zeroes — a parser reading the declared length would
        // otherwise choke on the NULs.
        for (var i = 20 + jsonBytes.Length; i < 20 + jsonPadded; i++) glb[i] = (byte)' ';

        var binChunk = 20 + jsonPadded;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binChunk), (uint)bin.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binChunk + 4), 0x004E4942);  // "BIN"
        bin.CopyTo(glb.AsSpan(binChunk + 8));
        return glb;

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
