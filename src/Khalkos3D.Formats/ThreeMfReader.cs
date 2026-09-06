using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;

namespace Khalkos3D.Formats;

/// <summary>
/// Reads 3MF — the container format that carries what STL cannot.
///
/// <para>A 3MF is a zip holding an XML model. That already buys everything STL lacks: <b>units</b>,
/// so a part is a known size rather than a number of unnamed somethings; shared vertices, so no
/// welding is needed; colours; a scene holding several objects; and instancing, so twenty copies of
/// one object are twenty transforms over one mesh rather than twenty copies of the geometry.</para>
///
/// <para><b>Units are the reason this reader is worth having even for a single part.</b> An STL of a
/// 20 mm cube and an STL of a 20 inch cube are byte-identical apart from the numbers, and nothing in
/// the file says which is meant. Every 3MF states its unit, and this reader converts to millimetres
/// so that anything downstream — a containment check, a measurement, a scale bar — can be written
/// once against one unit instead of guessing per file.</para>
/// </summary>
public static class ThreeMfReader
{
    /// <summary>Read a 3MF from a file.</summary>
    public static Scene ReadFile(string path, ThreeMfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        return Read(stream, options);
    }

    /// <summary>Read a 3MF from a stream.</summary>
    public static Scene Read(Stream stream, ThreeMfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new ThreeMfOptions();
        var report = new LoadReport.Builder();

        ZipArchive archive;
        try { archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException ex) { throw new ModelFormatException("the 3MF is not a readable zip archive", ex); }

        using (archive)
        {
            // The conventional path first, then any .model anywhere: the OPC relationship graph is
            // the correct way to find it, and parsing that to locate one file every writer puts in
            // the same place would be ceremony. Falling back keeps the odd file working.
            var entry = archive.GetEntry("3D/3dmodel.model")
                     ?? archive.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                throw new ModelFormatException("the 3MF contains no .model part");

            using var model = entry.Open();
            return ReadModel(model, options, report);
        }
    }

    private static Scene ReadModel(Stream xml, ThreeMfOptions options, LoadReport.Builder report)
    {
        var settings = new XmlReaderSettings
        {
            // A model file is untrusted input. DTD processing is how a zip becomes a denial of
            // service (billion laughs) or a file read, and there is no legitimate 3MF that needs it.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
        };

        var meshes = new List<Mesh>();
        var materials = new List<Material>();
        // 3MF object ids are arbitrary integers, so they are mapped rather than indexed into.
        var objects = new Dictionary<int, ObjectEntry>();
        var baseMaterials = new Dictionary<int, List<Material>>();
        // Colour groups keyed by their own id, which triangles reference through pid.
        var palettes = new Dictionary<int, List<Vector4>>();
        var roots = new List<Node>();
        var scale = 1f;

        using var reader = XmlReader.Create(xml, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.LocalName)
            {
                case "model":
                    var unit = reader.GetAttribute("unit") ?? "millimeter";
                    scale = UnitToMillimetres(unit);
                    if (scale <= 0f)
                    {
                        report.Repaired("unit", $"'{unit}' is not a 3MF unit; millimetres were assumed");
                        scale = 1f;
                    }
                    else if (!string.Equals(unit, "millimeter", StringComparison.OrdinalIgnoreCase))
                    {
                        report.Info("unit", $"the file is in {unit}; converted to millimetres (x{scale})");
                    }
                    break;

                case "basematerials":
                    ReadBaseMaterials(reader, baseMaterials);
                    break;

                case "colorgroup":
                    ReadColorGroup(reader, palettes);
                    break;

                case "object":
                    ReadObject(reader, objects, meshes, palettes, report);
                    break;

                case "item":
                    var objectId = ParseInt(reader.GetAttribute("objectid"));
                    var transform = ParseMatrix(reader.GetAttribute("transform"));
                    if (objectId is { } id) roots.Add(BuildNode(id, transform));
                    break;
            }
        }

        if (meshes.Count == 0) throw new ModelFormatException("the 3MF contains no mesh geometry");

        // A file with resources but no build section still has something worth showing. Rendering
        // nothing because the plate was empty would be technically correct and useless.
        if (roots.Count == 0)
        {
            report.Repaired("build", "the file has no build items; every object is shown at the origin");
            foreach (var (id, _) in objects) roots.Add(BuildNode(id, Matrix4x4.Identity));
        }

        // Applied once at the root rather than baked into every vertex: it keeps the mesh data
        // exactly as the file wrote it, which matters if it is ever written back out.
        if (scale != 1f)
        {
            var scaled = Matrix4x4.CreateScale(scale);
            var wrapped = new List<Node>(roots.Count);
            foreach (var root in roots)
                wrapped.Add(new Node
                {
                    Name = root.Name,
                    Transform = root.Transform * scaled,
                    Mesh = root.Mesh,
                    Material = root.Material,
                    Children = root.Children,
                });
            roots = wrapped;
        }

        foreach (var list in baseMaterials.Values) materials.AddRange(list);

        return new Scene
        {
            Meshes = meshes,
            Materials = materials.Count > 0 ? materials : [options.Material ?? Material.Default],
            Roots = roots,
            Report = report.Build(),
            // Z-up, and unlike STL the specification says so: 3MF puts the ground plane in XY.
            Up = UpAxis.Z,
        };

        Node BuildNode(int id, Matrix4x4 transform)
        {
            if (!objects.TryGetValue(id, out var entry))
                return new Node { Transform = transform };

            // Components are how a 3MF says "this object is those objects arranged like this" — the
            // instancing that makes a full plate cheap. Recursion is bounded by the file's own
            // acyclic requirement; a malformed cycle would be caught by the depth guard below.
            if (entry.Components.Count > 0)
            {
                var children = new List<Node>(entry.Components.Count);
                foreach (var (childId, childTransform) in entry.Components)
                    if (childId != id) children.Add(BuildNode(childId, childTransform));
                return new Node { Name = entry.Name, Transform = transform, Children = children };
            }

            return new Node { Name = entry.Name, Transform = transform, Mesh = entry.MeshIndex };
        }
    }

    private readonly record struct ObjectEntry(
        string? Name, int? MeshIndex, List<(int Id, Matrix4x4 Transform)> Components);

    private static void ReadObject(XmlReader reader, Dictionary<int, ObjectEntry> objects,
                                   List<Mesh> meshes, Dictionary<int, List<Vector4>> palettes,
                                   LoadReport.Builder report)
    {
        var id = ParseInt(reader.GetAttribute("id"));
        if (id is null) return;
        var name = reader.GetAttribute("name");
        var components = new List<(int, Matrix4x4)>();
        int? meshIndex = null;

        if (reader.IsEmptyElement)
        {
            objects[id.Value] = new ObjectEntry(name, null, components);
            return;
        }

        var depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.LocalName == "mesh")
            {
                var mesh = ReadMesh(reader, name, palettes, report);
                if (mesh is not null) { meshes.Add(mesh); meshIndex = meshes.Count - 1; }
            }
            else if (reader.LocalName == "component")
            {
                var childId = ParseInt(reader.GetAttribute("objectid"));
                if (childId is { } c) components.Add((c, ParseMatrix(reader.GetAttribute("transform"))));
            }
        }

        objects[id.Value] = new ObjectEntry(name, meshIndex, components);
    }

    private static Mesh? ReadMesh(XmlReader reader, string? name,
                                  Dictionary<int, List<Vector4>> palettes, LoadReport.Builder report)
    {
        var positions = new List<Vector3>(4096);
        var indices = new List<int>(8192);
        // Per-corner palette entries, parallel to indices. -1 where a triangle named no colour.
        var swatches = new List<int>(8192);
        var perTrianglePid = new List<int>(2048);
        var coloured = 0;
        var unsupportedProperty = false;
        var dropped = 0;

        if (reader.IsEmptyElement) return null;
        var depth = reader.Depth;

        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.LocalName == "vertex")
            {
                positions.Add(new Vector3(
                    ParseFloat(reader.GetAttribute("x")),
                    ParseFloat(reader.GetAttribute("y")),
                    ParseFloat(reader.GetAttribute("z"))));
            }
            else if (reader.LocalName == "triangle")
            {
                var v1 = ParseInt(reader.GetAttribute("v1"));
                var v2 = ParseInt(reader.GetAttribute("v2"));
                var v3 = ParseInt(reader.GetAttribute("v3"));
                if (v1 is null || v2 is null || v3 is null) { dropped++; continue; }
                indices.Add(v1.Value);
                indices.Add(v2.Value);
                indices.Add(v3.Value);

                // pid names a property group; p1/p2/p3 index into it, once per corner. A triangle
                // with only p1 uses that one colour for all three, which is how a flat-shaded face
                // is written.
                var pid = ParseInt(reader.GetAttribute("pid")) ?? -1;
                var p1 = ParseInt(reader.GetAttribute("p1"));
                if (p1 is null || !palettes.ContainsKey(pid))
                {
                    if (p1 is not null) unsupportedProperty = true;
                    swatches.Add(-1); swatches.Add(-1); swatches.Add(-1);
                    perTrianglePid.Add(-1);
                }
                else
                {
                    var p2 = ParseInt(reader.GetAttribute("p2")) ?? p1.Value;
                    var p3 = ParseInt(reader.GetAttribute("p3")) ?? p1.Value;
                    swatches.Add(p1.Value); swatches.Add(p2); swatches.Add(p3);
                    perTrianglePid.Add(pid);
                    coloured++;
                }
            }
        }

        if (positions.Count == 0 || indices.Count < 3) return null;

        // An index past the end of the vertex list would read off the end of a GL buffer, so
        // offending triangles go rather than the whole mesh — a part with a few bad facets is still
        // worth looking at.
        var valid = new List<int>(indices.Count);
        var validSwatches = new List<int>(indices.Count);
        var validPids = new List<int>(indices.Count / 3);
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            if ((uint)indices[i] < (uint)positions.Count &&
                (uint)indices[i + 1] < (uint)positions.Count &&
                (uint)indices[i + 2] < (uint)positions.Count)
            {
                valid.Add(indices[i]); valid.Add(indices[i + 1]); valid.Add(indices[i + 2]);
                validSwatches.Add(swatches[i]); validSwatches.Add(swatches[i + 1]); validSwatches.Add(swatches[i + 2]);
                validPids.Add(perTrianglePid[i / 3]);
            }
            else dropped++;
        }

        if (dropped > 0)
            report.Repaired("triangle", $"{dropped:N0} triangles referenced vertices that do not exist");
        if (unsupportedProperty)
            report.Unsupported("triangle properties",
                "some triangles reference a property group that is not a colour group, and are drawn " +
                "with the object's material instead");

        var outPositions = new List<Vector3>(positions.Count);
        var outColors = coloured > 0 ? new List<Vector4>(positions.Count) : null;
        var outIndices = new List<int>(valid.Count);

        if (outColors is null)
        {
            outPositions.AddRange(positions);
            outIndices.AddRange(valid);
        }
        else
        {
            // A COLOUR IS A PROPERTY OF A CORNER, NOT OF A POSITION, so two triangles meeting at a
            // vertex with different colours need two vertices there. Splitting only where the
            // colours actually differ keeps a single-colour object at its original vertex count,
            // rather than tripling every mesh for a feature most files do not use.
            var split = new Dictionary<(int Vertex, int Swatch), int>(positions.Count);
            for (var i = 0; i < valid.Count; i++)
            {
                var key = (valid[i], validSwatches[i]);
                if (!split.TryGetValue(key, out var index))
                {
                    outPositions.Add(positions[valid[i]]);
                    outColors.Add(Swatch(validPids[i / 3], validSwatches[i]));
                    split[key] = index = outPositions.Count - 1;
                }
                outIndices.Add(index);
            }
            report.Info("colour group",
                $"{coloured:N0} triangles carried per-corner colours; " +
                $"{outPositions.Count - positions.Count:N0} vertices were split to keep them distinct");
        }

        var mesh = new Mesh
        {
            Positions = [.. outPositions],
            Indices = [.. outIndices],
            Colors = outColors is null ? null : [.. outColors],
            Name = name,
        };
        return new Mesh
        {
            Positions = mesh.Positions,
            Indices = mesh.Indices,
            // 3MF shares vertices already, so there is nothing to weld — but it stores no normals
            // either, so they are computed. Smooth here rather than angle-split: unlike STL the file
            // has told us which corners are genuinely shared, and second-guessing that would undo
            // the one thing 3MF does better.
            Normals = mesh.ComputeSmoothNormals(),
            Colors = mesh.Colors,
            Name = name,
        };

        Vector4 Swatch(int pid, int index)
        {
            if (index < 0 || !palettes.TryGetValue(pid, out var palette)) return Vector4.One;
            return (uint)index < (uint)palette.Count ? palette[index] : Vector4.One;
        }
    }

    /// <summary>
    /// A palette of colours triangles can index into — the 3MF materials extension.
    ///
    /// <para>This is how a file says which parts of a mesh are which colour, so dropping it turns a
    /// two-tone model into a uniform grey one with no indication that anything was lost.</para>
    /// </summary>
    private static void ReadColorGroup(XmlReader reader, Dictionary<int, List<Vector4>> into)
    {
        var groupId = ParseInt(reader.GetAttribute("id"));
        if (groupId is null || reader.IsEmptyElement) return;

        var colours = new List<Vector4>();
        var depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "color") continue;
            colours.Add(ParseColor(reader.GetAttribute("color")));
        }
        into[groupId.Value] = colours;
    }

    private static void ReadBaseMaterials(XmlReader reader, Dictionary<int, List<Material>> into)
    {
        var groupId = ParseInt(reader.GetAttribute("id"));
        if (groupId is null || reader.IsEmptyElement) return;

        var list = new List<Material>();
        var depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "base") continue;
            list.Add(new Material
            {
                Name = reader.GetAttribute("name"),
                BaseColor = ParseColor(reader.GetAttribute("displaycolor")),
            });
        }
        into[groupId.Value] = list;
    }

    /// <summary>3MF's units, as a factor to millimetres. Returns 0 for anything unrecognised so the
    /// caller can report it rather than silently scaling by one.</summary>
    internal static float UnitToMillimetres(string unit) => unit.ToLowerInvariant() switch
    {
        "micron" => 0.001f,
        "millimeter" => 1f,
        "centimeter" => 10f,
        "inch" => 25.4f,
        "foot" => 304.8f,
        "meter" => 1000f,
        _ => 0f,
    };

    /// <summary>3MF writes a 4x3 row-major matrix: three basis vectors then the translation, with
    /// the fourth column implicitly (0,0,0,1).</summary>
    internal static Matrix4x4 ParseMatrix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Matrix4x4.Identity;

        Span<float> v = stackalloc float[12];
        var found = 0;
        foreach (var range in text.AsSpan().Split(' '))
        {
            var token = text.AsSpan()[range].Trim();
            if (token.IsEmpty) continue;
            if (found == 12) break;
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out v[found]))
                return Matrix4x4.Identity;
            found++;
        }
        if (found != 12) return Matrix4x4.Identity;

        return new Matrix4x4(
            v[0], v[1], v[2], 0f,
            v[3], v[4], v[5], 0f,
            v[6], v[7], v[8], 0f,
            v[9], v[10], v[11], 1f);
    }

    /// <summary>Parse <c>#RRGGBB</c> or <c>#RRGGBBAA</c>. An unreadable colour yields white rather
    /// than black, because a model that renders white looks unstyled and one that renders black
    /// looks like a lighting bug.</summary>
    internal static Vector4 ParseColor(string? text)
    {
        if (text is null || text.Length < 7 || text[0] != '#') return Vector4.One;
        static float Hex(ReadOnlySpan<char> s) =>
            byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b) ? b / 255f : 1f;

        var span = text.AsSpan();
        return new Vector4(
            Hex(span.Slice(1, 2)),
            Hex(span.Slice(3, 2)),
            Hex(span.Slice(5, 2)),
            text.Length >= 9 ? Hex(span.Slice(7, 2)) : 1f);
    }

    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static float ParseFloat(string? text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
}

/// <summary>How to read a 3MF.</summary>
public sealed record ThreeMfOptions
{
    /// <summary>Material to use when the file declares none.</summary>
    public Material? Material { get; init; }
}
