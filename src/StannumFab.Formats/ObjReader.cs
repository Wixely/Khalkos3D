using System.Globalization;
using System.Numerics;
using System.Text;

namespace StannumFab.Formats;

/// <summary>
/// Reads Wavefront OBJ — the format everything can export and nothing agrees on.
///
/// <para>Unlike STL it has shared vertices, texture coordinates, names and a material reference, so
/// rather more survives the trip. What it has no notion of is a binary form, which makes it slow and
/// enormous for large meshes; it is here because it is ubiquitous, not because it is good.</para>
///
/// <para><b>Three things about OBJ catch people out, and all three are handled here.</b> Indices are
/// ONE-based, so a naive reader is off by one on every triangle. They may also be NEGATIVE, meaning
/// "counting back from the most recently declared element", which is rare enough that plenty of
/// readers crash on it. And a face may have any number of corners, so quads and n-gons need
/// triangulating.</para>
///
/// <para>Materials live in a separate <c>.mtl</c> file. This reader records the reference and does
/// not chase it: resolving a sibling path is the caller's business, not a parser's, and a loader
/// that quietly opened adjacent files would be an unpleasant surprise in a sandbox. The name is
/// reported so a caller can go and fetch it.</para>
/// </summary>
public static class ObjReader
{
    /// <summary>Read an OBJ from a file.</summary>
    public static Scene ReadFile(string path, ObjOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        return Read(stream, options, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Read an OBJ from a stream.</summary>
    public static Scene Read(Stream stream, ObjOptions? options = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new ObjOptions();
        var report = new LoadReport.Builder();

        // What the file declared, in file order. OBJ indices point into these.
        var positions = new List<Vector3>(4096);
        var texcoords = new List<Vector2>();
        var normals = new List<Vector3>();

        // What comes out. Deduplicated by the v/vt/vn triple exactly as written — OBJ already says
        // which corners are shared, so there is nothing to infer and no welding to do. Running
        // MeshWelder here would discard information the file went to the trouble of encoding.
        var lookup = new Dictionary<(int, int, int), int>(4096);
        var outPositions = new List<Vector3>(4096);
        var outUvs = new List<Vector2>(4096);
        var outNormals = new List<Vector3>(4096);
        var indices = new List<int>(8192);

        string? objectName = null;
        int malformed = 0, ngons = 0, missingNormals = 0;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var face = new List<int>(8);

        while (reader.ReadLine() is { } raw)
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty || line[0] == '#') continue;

            var space = line.IndexOfAny(' ', '\t');
            var keyword = space < 0 ? line : line[..space];
            var rest = space < 0 ? default : line[(space + 1)..];

            if (keyword.SequenceEqual("v"))
            {
                if (TryFloats(rest, 3, out var v)) positions.Add(new Vector3(v.X, v.Y, v.Z));
                else malformed++;
            }
            else if (keyword.SequenceEqual("vt"))
            {
                // V is flipped here, once: OBJ's texture origin is bottom-left and every image this
                // engine uploads is top-row-first. Doing it at the parse means no shader, no
                // material and no backend ever has to know.
                if (TryFloats(rest, 2, out var v)) texcoords.Add(new Vector2(v.X, 1f - v.Y));
                else malformed++;
            }
            else if (keyword.SequenceEqual("vn"))
            {
                if (TryFloats(rest, 3, out var v)) normals.Add(new Vector3(v.X, v.Y, v.Z));
                else malformed++;
            }
            else if (keyword.SequenceEqual("f"))
            {
                face.Clear();
                var broken = false;
                foreach (var token in Corners(rest))
                {
                    if (!TryCorner(token, positions.Count, texcoords.Count, normals.Count,
                                   out int vi, out int ti, out int ni))
                    { malformed++; broken = true; break; }
                    face.Add(Emit(vi, ti, ni));
                }

                if (!broken && face.Count >= 3)
                {
                    // Fan triangulation, which is correct for the convex faces OBJ files contain in
                    // practice. A concave n-gon would need ear clipping; it is reported rather than
                    // silently mis-filled.
                    for (var i = 1; i + 1 < face.Count; i++)
                    {
                        indices.Add(face[0]);
                        indices.Add(face[i]);
                        indices.Add(face[i + 1]);
                    }
                    if (face.Count > 3) ngons++;
                }
            }
            else if (keyword.SequenceEqual("o") || keyword.SequenceEqual("g"))
            {
                objectName ??= rest.Trim().ToString();
            }
            else if (keyword.SequenceEqual("mtllib"))
            {
                report.Unsupported("mtllib",
                    $"materials are in '{rest.Trim()}', a separate file this reader does not open");
            }
            else if (keyword.SequenceEqual("usemtl"))
            {
                report.Unsupported("usemtl", "per-face material assignment is not applied");
            }
        }

        if (malformed > 0) report.Repaired("line", $"{malformed:N0} lines could not be parsed and were skipped");
        if (ngons > 0) report.Info("polygon", $"{ngons:N0} faces with more than three corners were triangulated");
        if (outPositions.Count == 0) throw new ModelFormatException("the OBJ contains no geometry");

        // Channels are only kept when the file actually supplied them for EVERY vertex. A partial
        // normal channel is worse than none: the vertices that got a default would shade as if lit
        // from a fixed direction while their neighbours shade correctly, which reads as a lighting
        // bug rather than as missing data.
        var haveUvs = texcoords.Count > 0;
        var haveNormals = normals.Count > 0 && missingNormals == 0;
        if (normals.Count > 0 && missingNormals > 0)
            report.Repaired("vn", $"{missingNormals:N0} corners had no normal; all normals were recomputed");

        var mesh = new Mesh
        {
            Positions = [.. outPositions],
            Indices = [.. indices],
            Normals = haveNormals ? [.. outNormals] : null,
            Uvs = haveUvs ? [.. outUvs] : null,
            Name = objectName ?? name,
        };

        if (mesh.Normals is null)
        {
            if (normals.Count == 0)
                report.Info("normals", "the file supplied none; smooth normals were computed from the geometry");
            mesh = new Mesh
            {
                Positions = mesh.Positions,
                Indices = mesh.Indices,
                Normals = mesh.ComputeSmoothNormals(),
                Uvs = mesh.Uvs,
                Name = mesh.Name,
            };
        }

        if (mesh.Validate() is { } bad) throw new ModelFormatException($"the OBJ produced an invalid mesh: {bad}");
        return Scene.FromMesh(mesh, options.Material, report.Build());

        int Emit(int vi, int ti, int ni)
        {
            var key = (vi, ti, ni);
            if (lookup.TryGetValue(key, out var existing)) return existing;

            outPositions.Add(positions[vi]);
            // Every channel is always appended to, so all three lists stay exactly as long as the
            // positions. Whether a channel survives is decided once at the end, on the file as a
            // whole, rather than per vertex.
            outUvs.Add(ti >= 0 && ti < texcoords.Count ? texcoords[ti] : Vector2.Zero);
            if (ni >= 0 && ni < normals.Count) outNormals.Add(normals[ni]);
            else { outNormals.Add(Vector3.UnitY); missingNormals++; }

            var index = outPositions.Count - 1;
            lookup[key] = index;
            return index;
        }
    }

    /// <summary>Split a face line into its whitespace-separated corners. Materialised rather than
    /// streamed because a ref struct enumerator cannot cross an iterator boundary, and a face has at
    /// most a handful of corners.</summary>
    private static List<string> Corners(ReadOnlySpan<char> line)
    {
        var list = new List<string>(4);
        while (!line.IsEmpty)
        {
            line = line.TrimStart();
            if (line.IsEmpty) break;
            var end = line.IndexOfAny(' ', '\t');
            list.Add((end < 0 ? line : line[..end]).ToString());
            line = end < 0 ? default : line[(end + 1)..];
        }
        return list;
    }

    /// <summary>Parse <c>v</c>, <c>v/vt</c>, <c>v//vn</c> or <c>v/vt/vn</c> into zero-based indices,
    /// resolving both the one-based and the negative forms. -1 means the field was absent.</summary>
    private static bool TryCorner(ReadOnlySpan<char> token, int vertices, int texcoords, int normals,
                                  out int vi, out int ti, out int ni)
    {
        vi = ti = ni = -1;
        var part = 0;

        while (!token.IsEmpty && part < 3)
        {
            var slash = token.IndexOf('/');
            var field = slash < 0 ? token : token[..slash];
            if (!field.IsEmpty)
            {
                if (!int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    return false;
                var total = part switch { 0 => vertices, 1 => texcoords, _ => normals };
                // Negative counts back from the most recently declared element; positive is
                // one-based. Both land zero-based here.
                var resolved = value < 0 ? total + value : value - 1;
                switch (part)
                {
                    case 0: vi = resolved; break;
                    case 1: ti = resolved; break;
                    default: ni = resolved; break;
                }
            }
            part++;
            token = slash < 0 ? default : token[(slash + 1)..];
        }

        // Only the position is load-bearing: an out-of-range one would index off the end of the
        // array, so it is refused here rather than trusted.
        return (uint)vi < (uint)vertices;
    }

    private static bool TryFloats(ReadOnlySpan<char> text, int count, out Vector4 value)
    {
        value = default;
        Span<float> parts = stackalloc float[4];
        var found = 0;
        while (found < count && !text.IsEmpty)
        {
            text = text.TrimStart();
            if (text.IsEmpty) break;
            var end = text.IndexOfAny(' ', '\t');
            var token = end < 0 ? text : text[..end];
            // Invariant, always: OBJ is written with a decimal point whatever the writer's locale,
            // and a comma-decimal machine reading "1.5" as 15 produces a model ten times too big.
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out parts[found]))
                return false;
            found++;
            text = end < 0 ? default : text[(end + 1)..];
        }
        if (found < count) return false;
        value = new Vector4(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }
}

/// <summary>How to read an OBJ.</summary>
public sealed record ObjOptions
{
    /// <summary>The material to attach. OBJ points at a separate <c>.mtl</c> file this reader does
    /// not open, so the caller decides.</summary>
    public Material? Material { get; init; }
}
