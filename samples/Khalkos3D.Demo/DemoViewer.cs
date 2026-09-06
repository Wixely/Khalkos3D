using System.Numerics;
using Khalkos3D.Gl;

namespace Khalkos3D.Demo;

/// <summary>Debug views a viewer can turn on. Each maps to one flag in <see cref="RenderSettings"/>.</summary>
public enum DemoView
{
    /// <summary>Triangle edges instead of filled faces.</summary>
    Wireframe,
    /// <summary>Shade by surface normal instead of by material.</summary>
    Normals,
    /// <summary>Paint back-facing triangles in a warning colour.</summary>
    Backfaces,
}

/// <summary>
/// The whole demo, minus the window.
///
/// <para><b>This class is the point of the sample.</b> Everything a 3D viewer actually does — hold a
/// scene, turn drags into camera moves, decide what a frame looks like, issue it — happens here, in
/// code that compiles once and runs unchanged on Windows, Linux, macOS and Android. What each host
/// adds is a window, an input source and a swap: a hundred lines on the desktop, rather fewer on
/// Android. If the shared part were thin and the hosts were fat, the engine would not be delivering
/// what it claims.</para>
///
/// <para>Nothing here creates a GL context. It is handed a proc-address function by whoever already
/// made one current, which is the same contract <see cref="GlRenderer"/> itself takes and the reason
/// the engine needs no windowing library.</para>
/// </summary>
public sealed class DemoViewer : IDisposable
{
    private readonly OrbitController _orbit = new();
    private GlRenderer? _renderer;
    private Scene _scene = new();
    private BoundingBox _focus = BoundingBox.Empty;
    private RenderSettings _settings = RenderSettings.Default with
    {
        // The demo owns the whole surface, so it clears. A host compositing this over its own
        // content would leave ClearColor null instead and draw on top of what is already there.
        ClearColor = new Vector4(0.055f, 0.063f, 0.078f, 1f),
    };

    private int _width = 1, _height = 1;

    /// <summary>Why the renderer would not start, or null when it did.</summary>
    public string? Error { get; private set; }

    /// <summary>What the driver calls itself, once started. Worth printing: on a phone or a virtual
    /// machine it is the fastest way to find out you are on a software rasteriser.</summary>
    public string Driver =>
        _renderer is null ? "no renderer" : $"{_renderer.Renderer} — {_renderer.Version} ({_renderer.Dialect})";

    /// <summary>Triangles in the scene, model and ground together.</summary>
    public int TriangleCount => _scene.TriangleCount;

    /// <summary>Frames drawn, for a host that wants to report progress.</summary>
    public long Frames { get; private set; }

    /// <summary>
    /// Start on a context somebody else has made current. False means the demo cannot run here and
    /// <see cref="Error"/> says why — a host should show that text rather than an empty window.
    /// </summary>
    public bool Initialise(Func<string, nint> getProcAddress)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);

        _renderer = GlRenderer.Create(getProcAddress, out var error);
        Error = _renderer is null ? error ?? "the renderer would not start" : null;
        return _renderer is not null;
    }

    /// <summary>
    /// Show a model. The ground is added here rather than by the caller so that the camera frames
    /// the MODEL: the grid is deliberately wider than its subject, and framing both would push the
    /// thing you came to look at into the distance.
    /// </summary>
    public void Show(Scene model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _focus = model.Bounds;
        _scene = DemoScene.WithGround(model);
        _settings = _settings with { Up = model.Up };
        _orbit.Frame(_focus, model.Up, Aspect);
    }

    /// <summary>The viewport changed size. Safe to call every frame.</summary>
    public void Resize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _orbit.Resize(Aspect);
    }

    /// <summary>
    /// Rotate, from a drag in PIXELS. The controller wants fractions of the viewport — that is what
    /// makes the same gesture turn the model the same amount on a phone and on a large monitor — and
    /// this is the one division that converts.
    /// </summary>
    public void Drag(float dxPixels, float dyPixels) =>
        _orbit.Orbit(dxPixels / _width, dyPixels / _height);

    /// <summary>Slide the view, from a drag in pixels.</summary>
    public void Pan(float dxPixels, float dyPixels) =>
        _orbit.Pan(dxPixels / _width, dyPixels / _height);

    /// <summary>Zoom by wheel notches, or by a pinch expressed as notches.</summary>
    public void Zoom(float ticks) => _orbit.Zoom(ticks);

    /// <summary>Put the camera back where it started.</summary>
    public void Reset() => _orbit.Reset();

    /// <summary>Turn a debug view on or off, and say what the new state is.</summary>
    public string Toggle(DemoView view)
    {
        _settings = view switch
        {
            DemoView.Wireframe => _settings with { Wireframe = !_settings.Wireframe },
            DemoView.Normals => _settings with { ShowNormals = !_settings.ShowNormals },
            DemoView.Backfaces => _settings with { HighlightBackfaces = !_settings.HighlightBackfaces },
            _ => _settings,
        };

        var on = view switch
        {
            DemoView.Wireframe => _settings.Wireframe,
            DemoView.Normals => _settings.ShowNormals,
            DemoView.Backfaces => _settings.HighlightBackfaces,
            _ => false,
        };
        return $"{view}: {(on ? "on" : "off")}";
    }

    /// <summary>Draw one frame. Does nothing before <see cref="Initialise"/> succeeds.</summary>
    public void Render()
    {
        if (_renderer is null) return;
        _renderer.Draw(_scene, _orbit.Camera, _width, _height, _settings);
        Frames++;
    }

    /// <summary>Release GL objects. Must run with the context still current.</summary>
    public void Dispose()
    {
        _renderer?.Dispose();
        _renderer = null;
    }

    private float Aspect => (float)_width / _height;
}
