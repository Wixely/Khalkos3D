using Khalkos3D;
using Khalkos3D.Formats;

namespace Khalkos3D.WasmProbe;

/// <summary>
/// Every reader, linked into a browser app and called.
/// </summary>
public static class Probe
{
    /// <summary>
    /// Called FROM Main, and that is load-bearing rather than tidy.
    ///
    /// <para>A wasm publish trims, and a method nothing reaches is a method ILLink deletes — along
    /// with the whole assembly. An empty Main left the bundle with no Khalkos3D in it at all, which
    /// the check in CI correctly reported as a failure.</para>
    ///
    /// <para>So this doubles as a trimming gate: every reader has to survive being linked into a
    /// browser app, which is a property a consumer cares about and nothing else here tests.</para>
    /// </summary>
    public static void Main()
    {
        // A one-triangle ASCII STL, so the call is real rather than a throw the trimmer might still
        // see through.
        byte[] bytes = [0x73, 0x6F, 0x6C, 0x69, 0x64, 0x20, 0x78, 0x0A];

        try
        {
            var scene = ModelReader.Read(bytes, "stl");
            Console.WriteLine($"{scene.TriangleCount} {scene.Bounds} {BoundingBox.Empty.IsEmpty}");
            Console.WriteLine(MeshWelder.Weld(scene.Meshes[0], out _).VertexCount);
        }
        catch (ModelFormatException e)
        {
            Console.WriteLine(e.Message);
        }

        // Referenced so every reader is rooted; never executed.
        Console.WriteLine(string.Join(',',
            nameof(StlReader), nameof(ObjReader), nameof(ThreeMfReader), nameof(GltfReader)));

        if (bytes.Length > 1000)
        {
            _ = StlReader.Read(new MemoryStream(bytes));
            _ = ObjReader.Read(new MemoryStream(bytes));
            _ = ThreeMfReader.Read(new MemoryStream(bytes));
            _ = GltfReader.Read(bytes);
        }
    }
}
