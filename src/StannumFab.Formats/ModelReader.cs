using System.Text;

namespace StannumFab.Formats;

/// <summary>The formats this library reads.</summary>
public enum ModelFormat
{
    /// <summary>Not recognised.</summary>
    Unknown,
    /// <summary>STL, ASCII or binary.</summary>
    Stl,
    /// <summary>Wavefront OBJ.</summary>
    Obj,
    /// <summary>3MF.</summary>
    ThreeMf,
    /// <summary>glTF 2.0, JSON or the GLB container.</summary>
    Gltf,
}

/// <summary>
/// Opens a model without being told what it is.
///
/// <para>Extension first, then content — and content is what decides when they disagree, because a
/// file's name is a claim and its bytes are a fact. Downloads land as <c>model.txt</c>, users rename
/// things, and a <c>.stl</c> that is really an OBJ is not rare.</para>
/// </summary>
public static class ModelReader
{
    /// <summary>Open a model file, working out the format from its name and its bytes.</summary>
    public static Scene ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var format = Detect(Peek(path), Path.GetExtension(path));
        return format switch
        {
            ModelFormat.Stl => StlReader.ReadFile(path),
            ModelFormat.Obj => ObjReader.ReadFile(path),
            ModelFormat.ThreeMf => ThreeMfReader.ReadFile(path),
            ModelFormat.Gltf => GltfReader.ReadFile(path),
            _ => throw new ModelFormatException(
                $"'{Path.GetFileName(path)}' is not a format this library reads (STL, OBJ, 3MF or glTF)"),
        };
    }

    /// <summary>Open a model from bytes. <paramref name="extension"/> is a hint — with or without
    /// the dot — used only when the content is ambiguous.</summary>
    public static Scene Read(byte[] bytes, string? extension = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var format = Detect(bytes, extension);
        return format switch
        {
            ModelFormat.Stl => StlReader.Read(new MemoryStream(bytes)),
            ModelFormat.Obj => ObjReader.Read(new MemoryStream(bytes)),
            ModelFormat.ThreeMf => ThreeMfReader.Read(new MemoryStream(bytes)),
            ModelFormat.Gltf => GltfReader.Read(bytes),
            _ => throw new ModelFormatException("the bytes are not a format this library reads"),
        };
    }

    /// <summary>
    /// Work out what a file is.
    /// </summary>
    /// <param name="head">The start of the file. 512 bytes is plenty; more is not consulted.</param>
    /// <param name="extension">Optional filename extension, with or without the leading dot.</param>
    public static ModelFormat Detect(ReadOnlySpan<byte> head, string? extension = null)
    {
        // Unambiguous magic numbers first — these cannot be anything else.
        if (head.Length >= 4)
        {
            if (head[..4].SequenceEqual("glTF"u8)) return ModelFormat.Gltf;

            // A zip. 3MF is the only zip container this library reads, so with a matching extension
            // it is decided; without one it is still the best guess and the reader will say so if
            // there is no .model part inside.
            if (head[0] == 'P' && head[1] == 'K' && (head[2] == 3 || head[2] == 5 || head[2] == 7))
                return ModelFormat.ThreeMf;
        }

        var normalised = extension?.TrimStart('.').ToLowerInvariant();

        // JSON glTF: the only text format here that starts with a brace.
        var text = head[..Math.Min(head.Length, 512)];
        var start = text;
        while (!start.IsEmpty && (start[0] == ' ' || start[0] == '\n' || start[0] == '\r' || start[0] == '\t'))
            start = start[1..];
        if (!start.IsEmpty && start[0] == '{') return ModelFormat.Gltf;

        // THE TEXT FORMATS ARE TESTED BEFORE THE BINARY ONE, and the order is load-bearing.
        // StlReader.IsBinary answers "is this NOT ASCII STL", so it says yes to an OBJ file as
        // readily as to a binary STL — using it as the catch-all here would claim every OBJ.
        var ascii = Encoding.ASCII.GetString(text);
        if (ascii.StartsWith("solid", StringComparison.OrdinalIgnoreCase) &&
            ascii.Contains("facet", StringComparison.OrdinalIgnoreCase))
            return ModelFormat.Stl;

        // OBJ has no magic at all, so it is recognised by shape: a "v " or "f " beginning some line
        // near the start of the file.
        foreach (var line in ascii.AsSpan().EnumerateLines())
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("v ") || trimmed.StartsWith("vn ") ||
                trimmed.StartsWith("vt ") || trimmed.StartsWith("f "))
                return ModelFormat.Obj;
        }

        // Nothing textual matched. A binary STL's header is 80 bytes of anything followed by a
        // count, so there is no signature to find — what there is instead is the absence of text:
        // control bytes in the first stretch of the file rule out every format above.
        var binaryish = false;
        foreach (var b in text[..Math.Min(text.Length, 128)])
            if (b == 0 || (b < 9 && b != 0) || (b > 13 && b < 32)) { binaryish = true; break; }
        if (binaryish && normalised is not ("gltf" or "3mf" or "obj")) return ModelFormat.Stl;

        // Nothing in the content decided it, so fall back to what the name claimed.
        return normalised switch
        {
            "stl" => ModelFormat.Stl,
            "obj" => ModelFormat.Obj,
            "3mf" => ModelFormat.ThreeMf,
            "gltf" or "glb" => ModelFormat.Gltf,
            _ => ModelFormat.Unknown,
        };
    }

    private static byte[] Peek(string path)
    {
        using var stream = File.OpenRead(path);
        // Enough for every magic number and for the text sniffs. Detect never uses the buffer's
        // LENGTH as a fact about the file, which is what lets a fixed peek work on a 2 GB print.
        var buffer = new byte[(int)Math.Min(512, stream.Length)];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
