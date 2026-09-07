using System.Diagnostics;
using System.Net;

// Publishes the browser demo if asked, serves the result, and opens a browser at it.
//
//   khalkos3d-serve --publish            the interpreted build, seconds
//   khalkos3d-serve --publish --llvm     Mono's AOT compiler through LLVM, minutes
//   khalkos3d-serve <folder> [--port n]  serve something already published
//
// ONE COMMAND RATHER THAN TWO CHAINED WITH &&, because the editor entries that call this run in
// whatever shell the machine has, and && is not a thing in Windows PowerShell 5.1. Stops on Ctrl+C.

var publish = args.Contains("--publish");
var llvm = args.Contains("--llvm");
var port = 8123;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var chosen)) port = chosen;

var root = RepositoryRoot();
var bundle = Path.Combine(root, "samples", "Khalkos3D.BrowserDemo", "bin", "Release",
                          "net10.0-browser", "browser-wasm", "AppBundle");

var explicitFolder = args.FirstOrDefault(a => !a.StartsWith('-') && a != port.ToString());
var folder = Path.GetFullPath(explicitFolder ?? bundle);

if (publish && !Publish(root, llvm)) return 1;

if (!Directory.Exists(folder))
{
    Console.Error.WriteLine($"nothing to serve: {folder} does not exist");
    Console.Error.WriteLine("pass --publish to build it first, or name a folder to serve");
    return 1;
}

var url = $"http://localhost:{port}/";

using var listener = new HttpListener();
listener.Prefixes.Add(url);

try
{
    listener.Start();
}
catch (HttpListenerException e)
{
    Console.Error.WriteLine($"could not listen on {url}: {e.Message}");
    Console.Error.WriteLine("something else may be using the port; pass another one as the second argument");
    return 1;
}

Console.WriteLine($"serving  {folder}");
Console.WriteLine($"at       {url}");
Console.WriteLine("Ctrl+C to stop");

Open(url);

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); listener.Stop(); };

while (!stopping.IsCancellationRequested)
{
    HttpListenerContext context;
    try { context = await listener.GetContextAsync(); }
    catch (HttpListenerException) { break; }         // Stop() was called
    catch (ObjectDisposedException) { break; }

    _ = Serve(context, folder);
}

return 0;

/// <summary>
/// The repository, found by walking up for the solution rather than assumed to be the working
/// directory — an editor launches this from wherever it feels like.
/// </summary>
static string RepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Khalkos3D.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? Directory.GetCurrentDirectory();
}

/// <summary>
/// Publish the browser demo, optionally through Mono's LLVM AOT compiler.
///
/// <para>AOT happens on PUBLISH and never on a build, which is the whole reason this program exists
/// rather than a `dotnet run` on the demo: there is no way to run an AOT-compiled wasm app without
/// publishing it first and serving what came out.</para>
/// </summary>
static bool Publish(string root, bool llvm)
{
    var project = Path.Combine(root, "samples", "Khalkos3D.BrowserDemo");
    var arguments = $"publish \"{project}\" -c Release";
    if (llvm) arguments += " -p:RunAOTCompilation=true -p:WasmEnableLLVM=true";

    Console.WriteLine(llvm
        ? "publishing through Mono's LLVM AOT compiler — this takes minutes"
        : "publishing");
    Console.WriteLine($"  dotnet {arguments}");

    var process = Process.Start(new ProcessStartInfo("dotnet", arguments) { UseShellExecute = false });
    if (process is null)
    {
        Console.Error.WriteLine("could not start dotnet");
        return false;
    }

    process.WaitForExit();
    if (process.ExitCode == 0) return true;

    Console.Error.WriteLine($"the publish failed with exit code {process.ExitCode}; nothing to serve");
    return false;
}

static async Task Serve(HttpListenerContext context, string root)
{
    var relative = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
    if (relative.Length == 0) relative = "index.html";

    var path = Path.GetFullPath(Path.Combine(root, relative));

    // A served folder is a served folder and nothing above it. Cheap, and the alternative is handing
    // out the developer's home directory to anything that can spell "..".
    if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
    {
        context.Response.StatusCode = 404;
        context.Response.Close();
        Console.WriteLine($"404 {relative}");
        return;
    }

    try
    {
        context.Response.ContentType = ContentType(Path.GetExtension(path));
        // Nothing is cached: the point of this server is to look at what was just published, and a
        // browser holding on to the previous build is a confusing way to spend an afternoon.
        context.Response.Headers["Cache-Control"] = "no-store";

        await using var file = File.OpenRead(path);
        context.Response.ContentLength64 = file.Length;
        await file.CopyToAsync(context.Response.OutputStream);
    }
    catch (HttpListenerException)
    {
        // The tab went away mid-transfer. Not this program's problem.
    }
    finally
    {
        context.Response.Close();
    }
}

/// <summary>
/// The handful of types a .NET wasm bundle is made of.
///
/// <para><c>application/wasm</c> is the one that matters rather than a nicety: the runtime compiles
/// the module as it streams, and a browser given the wrong type refuses to and falls back — or on
/// some versions simply fails.</para>
/// </summary>
static string ContentType(string extension) => extension.ToLowerInvariant() switch
{
    ".html" => "text/html; charset=utf-8",
    ".js" or ".mjs" => "text/javascript; charset=utf-8",
    ".json" => "application/json; charset=utf-8",
    ".wasm" => "application/wasm",
    ".css" => "text/css; charset=utf-8",
    ".svg" => "image/svg+xml",
    ".png" => "image/png",
    ".ico" => "image/x-icon",
    ".stl" or ".glb" or ".dat" or ".blat" or ".webcil" or ".pdb" => "application/octet-stream",
    _ => "application/octet-stream",
};

/// <summary>Open the default browser, on whichever of the three this is.</summary>
static void Open(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
    catch (Exception e)
    {
        // Not a failure: the server is up and the address is on screen.
        Console.WriteLine($"could not open a browser ({e.Message}); go to {url}");
    }
}
