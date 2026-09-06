using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace StannumFab.Formats;

/// <summary>
/// Reads STL, in both of its forms, and produces an indexed mesh.
///
/// <para><b>The format that 3D printing actually runs on, and it is barely a format.</b> An STL is a
/// list of triangles. No units, no scene, no materials, no shared vertices, no way to say which way
/// is up. Everything this reader does beyond parsing exists to make up for one of those gaps.</para>
///
/// <para><b>Telling ASCII from binary is the first real problem, and the naive test is wrong.</b>
/// An ASCII STL begins with the word <c>solid</c> — and so do binary files from several popular
/// exporters, which write it into the 80-byte header where anything is legal. Sniffing those five
/// bytes therefore misreads such files as ASCII, and the parse then fails on text that was never
/// there. See <see cref="IsBinary"/> for the three-step test that does work.</para>
/// </summary>
public static class StlReader
{
    private const int BinaryHeader = 80;
    private const int BinaryFacet = 50;   // 12 floats + a 2-byte attribute word

    /// <summary>Read an STL from a file.</summary>
    public static Scene ReadFile(string path, StlOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        return Read(stream, options, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// Read an STL from a stream.
    /// </summary>
    /// <param name="stream">The file. A non-seekable stream is buffered first, because the
    /// ASCII/binary test needs the length.</param>
    /// <param name="options">Welding and colour behaviour.</param>
    /// <param name="name">Name for the resulting mesh, when the file supplies none.</param>
    public static Scene Read(Stream stream, StlOptions? options = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new StlOptions();

        var bytes = ReadAll(stream);
        var report = new LoadReport.Builder();

        var mesh = IsBinary(bytes)
            ? ReadBinary(bytes, options, report, name)
            : ReadAscii(bytes, options, report, name);

        if (mesh.Validate() is { } bad) throw new ModelFormatException($"the STL produced an invalid mesh: {bad}");

        // STL carries no material at all, so one is invented — and said so, because a user comparing
        // this against their slicer's preview deserves to know the colour is ours and not theirs.
        report.Info("material", "STL stores no material; a neutral default was used");
        // Z-up: STL says nothing about orientation, but every CAD package and slicer that writes one
        // puts the build plate in XY. Assuming Y-up here would stand each part on its side.
        return Scene.FromMesh(mesh, options.Material, report.Build(), UpAxis.Z);
    }

    /// <summary>
    /// True when this is a binary STL.
    ///
    /// <para>Length arithmetic first, because it is decisive and the text sniff is not: a binary
    /// file whose header happens to begin "solid" is common enough that several exporters ship it.
    /// A file that is exactly the right length for its own triangle count is binary; nothing else
    /// plausibly is.</para>
    /// </summary>
    internal static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < BinaryHeader + 4) return false;

        // An ASCII STL is REQUIRED to begin with the word "solid". A file that does not is binary,
        // and this branch is decisive in a way the length arithmetic is not: it still holds for a
        // truncated download, where the stored count no longer matches the bytes present.
        var startsWithSolid =
            Encoding.ASCII.GetString(bytes[..5]).Equals("solid", StringComparison.OrdinalIgnoreCase);
        if (!startsWithSolid) return true;

        // It begins "solid", so it is either genuine ASCII or one of the binary files whose 80-byte
        // header happens to start that way. An exact length match settles it.
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[BinaryHeader..]);
        if ((long)BinaryHeader + 4 + (long)count * BinaryFacet == bytes.Length) return true;

        // Still ambiguous — a "solid"-headed binary that was truncated, or padded. Real ASCII always
        // has a "facet" keyword within the first few lines; a binary header almost never contains
        // that byte sequence, and the surrounding bytes are float noise rather than text.
        var head = bytes[..Math.Min(512, bytes.Length)];
        return head.IndexOf("facet"u8) < 0;
    }

    private static Mesh ReadBinary(ReadOnlySpan<byte> bytes, StlOptions options,
                                   LoadReport.Builder report, string? name)
    {
        var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes[BinaryHeader..]), int.MaxValue / 3);
        var available = (bytes.Length - BinaryHeader - 4) / BinaryFacet;
        if (count > available)
        {
            report.Repaired("triangle count",
                $"the header claims {count:N0} triangles but the file holds {available:N0}; the rest were ignored");
            count = available;
        }

        var corners = new Vector3[count * 3];
        Vector4[]? colors = null;
        var offset = BinaryHeader + 4;
        var coloured = 0;

        for (var t = 0; t < count; t++)
        {
            var facet = bytes.Slice(offset, BinaryFacet);
            offset += BinaryFacet;

            // Bytes 0..11 are the facet normal, and it is deliberately ignored. It is redundant
            // with the winding, frequently zero, and frequently WRONG — plenty of exporters emit
            // (0,0,0) or a stale value. Deriving normals from the geometry during welding is both
            // more reliable and what produces smooth shading across curved surfaces.
            for (var v = 0; v < 3; v++)
            {
                var at = 12 + v * 12;
                corners[t * 3 + v] = new Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(facet[at..]),
                    BinaryPrimitives.ReadSingleLittleEndian(facet[(at + 4)..]),
                    BinaryPrimitives.ReadSingleLittleEndian(facet[(at + 8)..]));
            }

            if (!options.ReadFacetColors) continue;

            // The attribute word is officially unused; in practice two conventions squeeze a
            // 15-bit colour into it and mark validity with the top bit. Which way round the bit
            // means "valid" differs between VisCAM and Magics, so the colour is only trusted when
            // some facet actually sets it — a file where every word is 0 gets no colours rather
            // than a model painted uniform black.
            var attribute = BinaryPrimitives.ReadUInt16LittleEndian(facet[48..]);
            if ((attribute & 0x8000) == 0) continue;

            colors ??= new Vector4[corners.Length];
            var colour = new Vector4(
                (attribute & 0x1F) / 31f,
                ((attribute >> 5) & 0x1F) / 31f,
                ((attribute >> 10) & 0x1F) / 31f,
                1f);
            colors[t * 3] = colors[t * 3 + 1] = colors[t * 3 + 2] = colour;
            coloured++;
        }

        if (colors is not null)
            report.Info("facet colour", $"{coloured:N0} of {count:N0} facets carried a 15-bit colour");

        return Build(corners, colors, options, report, name);
    }

    private static Mesh ReadAscii(ReadOnlySpan<byte> bytes, StlOptions options,
                                  LoadReport.Builder report, string? name)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var corners = new List<Vector3>(1024);
        string? solidName = null;
        var malformed = 0;

        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.IsEmpty) continue;

            if (trimmed.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadVector(trimmed[6..], out var position)) corners.Add(position);
                else malformed++;
            }
            else if (solidName is null && trimmed.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            {
                var rest = trimmed[5..].Trim();
                if (!rest.IsEmpty) solidName = rest.ToString();
            }
        }

        if (malformed > 0)
            report.Repaired("vertex", $"{malformed:N0} vertex lines could not be parsed and were skipped");

        // A stray vertex leaves a partial triangle. Dropping it is the only sane recovery — the
        // alternative is an index that runs off the end of the array during draw.
        var whole = corners.Count / 3 * 3;
        if (whole != corners.Count)
        {
            report.Repaired("facet", $"{corners.Count - whole} trailing vertices did not form a triangle");
            corners.RemoveRange(whole, corners.Count - whole);
        }

        return Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(corners), null,
                     options, report, solidName ?? name);
    }

    /// <summary>Parse three floats, invariant-culture. Explicitly invariant because STL is written
    /// with a decimal POINT regardless of the writer's locale, and a machine set to a comma-decimal
    /// locale would otherwise read "1.5" as 15 and silently produce a model ten times too big.</summary>
    private static bool TryReadVector(ReadOnlySpan<char> text, out Vector3 value)
    {
        value = default;
        Span<float> parts = stackalloc float[3];
        var found = 0;

        while (found < 3 && !text.IsEmpty)
        {
            text = text.TrimStart();
            if (text.IsEmpty) break;
            var end = text.IndexOfAny(' ', '\t');
            var token = end < 0 ? text : text[..end];
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out parts[found]))
                return false;
            found++;
            text = end < 0 ? default : text[(end + 1)..];
        }

        if (found < 3) return false;
        value = new Vector3(parts[0], parts[1], parts[2]);
        return true;
    }

    private static Mesh Build(ReadOnlySpan<Vector3> corners, Vector4[]? colors, StlOptions options,
                              LoadReport.Builder report, string? name)
    {
        if (corners.Length == 0) throw new ModelFormatException("the STL contains no triangles");

        var indices = new int[corners.Length];
        for (var i = 0; i < indices.Length; i++) indices[i] = i;
        var soup = new Mesh
        {
            Positions = corners.ToArray(),
            Indices = indices,
            Colors = colors,
            Name = name,
        };

        if (!options.Weld)
        {
            // Unwelded, every triangle keeps its own three corners, so smooth normals cannot be
            // smooth — each vertex belongs to exactly one face and gets that face's normal. The
            // result is correct flat shading, which is occasionally what a caller wants and is why
            // this switch exists.
            return new Mesh
            {
                Positions = soup.Positions,
                Indices = soup.Indices,
                Normals = soup.ComputeSmoothNormals(),
                Colors = colors,
                Name = name,
            };
        }

        var welded = MeshWelder.Weld(soup, out var weld, options.SmoothingAngle, options.PositionTolerance);
        report.Info("welding", weld.ToString());
        return welded;
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment) && segment.Offset == 0)
            return segment.Count == segment.Array!.Length ? segment.Array : memory.ToArray();

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

/// <summary>How to read an STL.</summary>
public sealed record StlOptions
{
    /// <summary>
    /// Merge the duplicated corners STL always contains. On by default, and it is the single most
    /// valuable thing this reader does: an STL stores every vertex once per triangle that touches
    /// it, so a typical solid arrives with about six times more vertices than it has.
    /// </summary>
    public bool Weld { get; init; } = true;

    /// <summary>Faces meeting at more than this angle keep a sharp edge. See
    /// <see cref="MeshWelder.Weld"/> — the default keeps a cube's corners square while letting a
    /// tessellated cylinder shade smoothly.</summary>
    public float SmoothingAngle { get; init; } = MeshWelder.DefaultSmoothingAngle;

    /// <summary>Snap positions to this grid before merging. 0 compares them as written, which is
    /// right for STL: the duplicates come from writing one computed vertex several times, so they
    /// are usually bit-identical.</summary>
    public float PositionTolerance { get; init; }

    /// <summary>Honour the 15-bit per-facet colour some writers pack into the attribute word.
    /// On by default; costs one branch per triangle and recovers colour that would otherwise be
    /// lost without trace.</summary>
    public bool ReadFacetColors { get; init; } = true;

    /// <summary>The material to attach. STL stores none, so this is the caller's choice; null
    /// yields <see cref="Material.Default"/>.</summary>
    public Material? Material { get; init; }
}
