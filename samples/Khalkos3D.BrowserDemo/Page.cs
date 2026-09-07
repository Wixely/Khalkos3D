using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Khalkos3D.Formats;

namespace Khalkos3D.BrowserDemo;

/// <summary>
/// What the tab shows: the loaders reading a real model, in WebAssembly.
///
/// <para><b>Everything below is ordinary Khalkos3D.</b> No branch on the platform, no wasm-specific
/// path, no second implementation — the same <see cref="ModelReader"/> a desktop viewer calls. That
/// is the claim this page exists to make, and the reason it opens a file the desktop demo also
/// draws rather than a fixture written to be easy.</para>
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class Page
{
    /// <summary>Write one line into the page. The JavaScript half is in main.js and knows nothing
    /// about models: it appends a name and a value.</summary>
    [JSImport("page.row", "main.js")]
    private static partial void Row(string name, string value, bool bad);

    [JSImport("page.clear", "main.js")]
    private static partial void Clear();

    /// <summary>
    /// Called by main.js once the runtime is up.
    ///
    /// <para>A wasm publish trims, and a method nothing reaches is a method ILLink deletes — along
    /// with the whole assembly. So this doubles as a trimming gate: every reader has to survive being
    /// linked into a browser app, which is a property a consumer cares about and nothing else here
    /// tests.</para>
    /// </summary>
    public static void Main()
    {
        Clear();
        Row("runtime", $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} on {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}", false);

        try
        {
            using var stream = typeof(Page).Assembly.GetManifestResourceStream("Khalkos3D.BrowserDemo.Khalkos3D.stl")
                ?? throw new InvalidOperationException("the logo is not embedded in this assembly");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();

            Row("file", $"Khalkos3D.stl, {bytes.Length:N0} bytes", false);

            var scene = ModelReader.Read(bytes, "stl");
            var mesh = scene.Meshes[0];

            Row("format", $"{ModelReader.Detect(bytes)}, sniffed from the bytes rather than from a name", false);
            Row("triangles", $"{scene.TriangleCount:N0}", false);
            Row("vertices", $"{mesh.Positions.Length:N0} after welding", false);
            Row("bounds", $"{scene.Bounds.Min} .. {scene.Bounds.Max}", false);
            Row("up axis", $"{scene.Up}", false);

            var analysis = MeshAnalysis.Analyse(mesh);
            Row("watertight", analysis.IsClosedManifold ? "yes, a closed manifold" : $"no — {analysis.BoundaryEdges:N0} boundary edges", false);

            foreach (var note in scene.Report.Notes) Row("note", note.ToString() ?? "", false);

            Row("status", "read in this tab, by the same code the desktop demo runs", false);
        }
        catch (Exception e)
        {
            // Shown rather than swallowed: a page that fails silently teaches nobody anything, and
            // the whole point of this one is to find out whether the asset layer works here.
            Row("status", $"{e.GetType().Name}: {e.Message}", true);
        }
    }
}
