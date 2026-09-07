using System.Numerics;
using Khalkos3D;
using Khalkos3D.Demo;
using Khalkos3D.DesktopDemo;
using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

// The desktop host: a window, a mouse, a keyboard. Every decision about what a frame contains lives
// in Khalkos3D.Demo and is shared with Android — this file is the platform tax.

var modelPath = args.Length > 0 ? args[0] : null;
if (modelPath is not null && !File.Exists(modelPath))
{
    Console.Error.WriteLine($"no such file: {modelPath}");
    return 1;
}

// NAMED RATHER THAN DISCOVERED. Silk.NET finds its backends by scanning assemblies, which works
// under `dotnet run` and is the first thing a trimmed or ahead-of-time build takes away. Two lines
// to say GLFW out loud, and this host stops depending on that scan.
//
// It is not what fixed the published build, and the distinction is worth keeping: that failed with
// "GlfwPlatform - not applicable" — the platform was found and the NATIVE glfw3 beside it was not.
// See the publish flags in .github/workflows/ci.yml, which keep the natives out of the bundle so
// they land next to the executable where Silk.NET looks for them.
GlfwWindowing.RegisterPlatform();
GlfwInput.RegisterPlatform();

// Still when it came from a file, turning when it is the built-in scene. The host does not branch
// on which: DemoViewer takes one type and drives the clock itself.
AnimatedScene model;
try
{
    model = modelPath is null ? DemoScene.Showcase() : DemoScene.Load(modelPath);
}
catch (Exception ex) when (ex is ModelFormatException or IOException)
{
    Console.Error.WriteLine($"could not open {modelPath}: {ex.Message}");
    return 1;
}

using var viewer = new DemoViewer();

var options = WindowOptions.Default with
{
    Size = new Vector2D<int>(1280, 800),
    Title = "Khalkos3D",
    // 3.3 core is the floor the engine targets on the desktop; it compiles "#version 330 core"
    // here and "#version 300 es" on Android, choosing by asking the driver rather than by
    // guessing from the platform.
    API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
    VSync = true,
};

using var window = Window.Create(options);

// Drag state. Held here rather than in the shared viewer because "which button is held" is a
// desktop idea; Android expresses the same intent with a finger count.
var dragging = false;
var panning = false;
var last = Vector2.Zero;

window.Load += () =>
{
    // The window wears the same icon as the executable. Set here rather than in the options because
    // there is no window to put an icon on until it has been created.
    if (WindowIcon.Sizes() is { Length: > 0 } icons) window.SetWindowIcon(icons);

    var context = window.GLContext;
    if (context is null)
    {
        Console.Error.WriteLine("the window has no GL context");
        window.Close();
        return;
    }

    if (!viewer.Initialise(name => context.TryGetProcAddress(name, out var address) ? address : 0))
    {
        Console.Error.WriteLine($"Khalkos3D would not start: {viewer.Error}");
        window.Close();
        return;
    }

    viewer.Resize(window.Size.X, window.Size.Y);
    viewer.Show(model);

    Console.WriteLine($"driver     {viewer.Driver}");
    Console.WriteLine($"showing    {modelPath ?? "the built-in showcase"}");
    Console.WriteLine($"triangles  {viewer.TriangleCount:N0} (model and ground)");

    // The report is printed whether or not anything went wrong. A loader that only speaks up on
    // failure trains people not to read it, and the same goes for the shaders the scene carries.
    foreach (var note in model.Report.Notes) Console.WriteLine($"note       {note}");
    foreach (var note in viewer.ShaderNotes) Console.WriteLine($"shader     {note}");

    Console.WriteLine();
    Console.WriteLine("drag to orbit, right-drag or shift-drag to pan, wheel to zoom");
    Console.WriteLine("W wireframe   N normals   B backfaces   R reset view   Esc quit");

    var input = window.CreateInput();

    foreach (var mouse in input.Mice)
    {
        mouse.MouseDown += (_, button) =>
        {
            if (button == MouseButton.Left) dragging = true;
            else if (button is MouseButton.Right or MouseButton.Middle) panning = true;
        };
        mouse.MouseUp += (_, button) =>
        {
            if (button == MouseButton.Left) dragging = false;
            else if (button is MouseButton.Right or MouseButton.Middle) panning = false;
        };
        mouse.MouseMove += (_, position) =>
        {
            var delta = position - last;
            last = position;
            if (panning) viewer.Pan(delta.X, delta.Y);
            else if (dragging) viewer.Drag(delta.X, delta.Y);
        };
        mouse.Scroll += (_, wheel) => viewer.Zoom(wheel.Y);
    }

    foreach (var keyboard in input.Keyboards)
    {
        keyboard.KeyDown += (board, key, _) =>
        {
            // Shift turns a left-drag into a pan, for a trackpad with one button.
            switch (key)
            {
                case Key.W: Console.WriteLine(viewer.Toggle(DemoView.Wireframe)); break;
                case Key.N: Console.WriteLine(viewer.Toggle(DemoView.Normals)); break;
                case Key.B: Console.WriteLine(viewer.Toggle(DemoView.Backfaces)); break;
                case Key.R: viewer.Reset(); break;
                case Key.Escape: window.Close(); break;
                case Key.ShiftLeft or Key.ShiftRight: panning = dragging; break;
            }
        };
        keyboard.KeyUp += (_, key, _) =>
        {
            if (key is Key.ShiftLeft or Key.ShiftRight) panning = false;
        };
    }
};

window.FramebufferResize += size => viewer.Resize(size.X, size.Y);
window.Render += _ => viewer.Render();

// Disposed while the context is still current: GL objects outlive their context otherwise, and
// the driver is entitled to complain about it on exit.
window.Closing += viewer.Dispose;

window.Run();
return 0;
