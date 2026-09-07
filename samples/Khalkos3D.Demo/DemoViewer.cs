using System.Diagnostics;
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

    // The animation's clock, owned here rather than passed in by each host. Both hosts already call
    // Render on every vertical blank and neither has anything to say about how long that took, so
    // asking them for a delta would add a line of platform code to buy nothing.
    private readonly Stopwatch _clock = new();

    private GlRenderer? _renderer;
    private AnimatedScene? _model;
    private Scene _scene = new();
    private BoundingBox _focus = BoundingBox.Empty;
    private RenderSettings _settings = RenderSettings.Default with
    {
        // The demo owns the whole surface, so it clears. A host compositing this over its own
        // content would leave ClearColor null instead and draw on top of what is already there.
        ClearColor = new Vector4(0.055f, 0.063f, 0.078f, 1f),

        // Dimmer than the studio default, because the built-in scene is lit by four lamps that move
        // and pulse, and an ambient bright enough to light everything by itself would leave them
        // adding a tint to a picture already finished. Not off: a metal with nothing to reflect
        // renders black, which is the failure the environment exists to prevent.
        Environment = Khalkos3D.Environment.Studio with { Intensity = 0.55f },
    };

    /// <summary>
    /// How much of the fitted distance the demo opens at. Closer than the fit, because the fit
    /// surrounds EVERYTHING — the outer ring of cubes and the haze around them — while the thing
    /// worth looking at is in the middle of that. A viewer framing an unfamiliar file should still
    /// use the whole distance; this one knows what its scene contains.
    ///
    /// <para>0.62 is the tightest value that never cuts anything off, checked by sweeping a whole
    /// revolution of the rings — 33 seconds — rather than by looking at the opening frame. The
    /// difference matters: everything fits at 0.58 for the first second and a half and then does
    /// not. Anything that changes the scene's extent changes this number, which is why it is
    /// measured rather than chosen.</para>
    /// </summary>
    private const float Opening = 0.62f;

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
    /// What the renderer made of the shaders the current scene carries — one line each, whether or
    /// not anything went wrong.
    ///
    /// <para>Reported rather than thrown, and reported even on success, for the same reason the
    /// loader's notes are: a channel that only ever carries bad news is a channel nobody reads. A
    /// shader that failed is named here and its material is drawn with the built-in program, so the
    /// screen shows the model and this says why it is the wrong colour.</para>
    /// </summary>
    public IReadOnlyList<string> ShaderNotes { get; private set; } = [];

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
    /// Show a model, still or moving. The ground is added here rather than by the caller so that the
    /// camera frames the MODEL: the grid is deliberately wider than its subject, and framing both
    /// would push the thing you came to look at into the distance.
    ///
    /// <para>The clock starts here, so the built-in scene begins its first turn when it appears
    /// rather than at some point during startup.</para>
    /// </summary>
    public void Show(AnimatedScene model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model.WithGround();
        _focus = model.Focus;
        _scene = _model.At(0f);
        _settings = _settings with { Up = model.Up, Lights = _model.LightsAt(0f) };
        _orbit.Frame(_focus, model.Up, Aspect, zoom: Opening);
        _clock.Restart();

        // Built here rather than on the first frame that needs one, so a host can print the outcome
        // next to the driver string instead of discovering it mid-animation.
        ShaderNotes = BuildShaders(_scene);
    }

    /// <summary>Compile every shader this scene carries, once each, and say how each went.</summary>
    private IReadOnlyList<string> BuildShaders(Scene scene)
    {
        if (_renderer is null) return [];

        List<string>? notes = null;
        var seen = new HashSet<Shader>(ReferenceEqualityComparer.Instance as IEqualityComparer<Shader>
                                       ?? EqualityComparer<Shader>.Default);

        foreach (var material in scene.Materials)
        {
            if (material.Shader is not { } shader || !seen.Add(shader)) continue;
            var report = _renderer.Prepare(shader);
            (notes ??= []).Add(report.Summary ?? $"{shader.Name ?? "shader"}: compiled");
        }

        return (IReadOnlyList<string>?)notes ?? [];
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
    /// <remarks>
    /// The horizontal delta is negated, so dragging left turns the model to the right. That is a
    /// preference about this viewer rather than a fact about the engine, which is why it is applied
    /// here and not in <see cref="OrbitController"/>: both hosts inherit it from the shared half,
    /// and a consumer who wants the other convention still gets it from the untouched controller.
    /// </remarks>
    public void Drag(float dxPixels, float dyPixels) =>
        _orbit.Orbit(-dxPixels / _width, dyPixels / _height);

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

    /// <summary>
    /// Draw one frame, rearranging the scene first if it moves. Does nothing before
    /// <see cref="Initialise"/> succeeds.
    ///
    /// <para>The rearrangement allocates a scene graph per frame and re-uploads no geometry: the
    /// renderer caches its buffers against each <see cref="Mesh"/> by reference, and the meshes are
    /// the same objects every time. A still model skips even that.</para>
    /// </summary>
    public void Render()
    {
        if (_renderer is null) return;

        if (_model is { Moves: true } model)
        {
            var seconds = (float)_clock.Elapsed.TotalSeconds;
            _scene = model.At(seconds);
            // A scene may light itself — the demo's cubes are lamps — and those lamps move with it.
            // An empty list is the renderer's own key light, so this is harmless for a scene that
            // does not.
            _settings = _settings with { Lights = model.LightsAt(seconds) };
        }

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
