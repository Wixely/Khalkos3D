using Silk.NET.Windowing;
using Xunit;

namespace Khalkos3D.RenderTests;

/// <summary>
/// One hidden GL context for the whole suite.
///
/// <para>Shared rather than per-test, and for a reason beyond speed: GL contexts are thread-affine,
/// and xunit will happily run two test classes in parallel on different threads. A context created
/// per class would be current on the wrong thread half the time, and the failures would be
/// intermittent and completely misleading. One context, one collection, no parallelism.</para>
///
/// <para>The window is 1x1 and never shown — GLFW has no portable headless context, and everything
/// is rendered to a framebuffer anyway, so its own buffer is never used.</para>
/// </summary>
public sealed class GlFixture : IDisposable
{
    private readonly IWindow? _window;

    /// <summary>Resolves GL entry points on this context, or null when no context could be made.</summary>
    public Func<string, nint>? GetProcAddress { get; }

    /// <summary>Why there is no context, when there is none. Not a failure: a machine with no GL
    /// should skip these tests rather than fail them, because the thing being reported would be the
    /// machine and not the code.</summary>
    public string? Unavailable { get; }

    public GlFixture()
    {
        try
        {
            var options = WindowOptions.Default with
            {
                Size = new(1, 1),
                IsVisible = false,
                Title = "stannumfab-tests",
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core,
                                      ContextFlags.Default, new APIVersion(3, 3)),
            };
            _window = Window.Create(options);
            _window.Initialize();
            _window.MakeCurrent();

            var context = _window.GLContext
                ?? throw new InvalidOperationException("the window has no GL context");
            GetProcAddress = name => context.TryGetProcAddress(name, out var p) ? p : 0;
        }
        catch (Exception ex)
        {
            Unavailable = $"{ex.GetType().Name}: {ex.Message}";
            _window?.Dispose();
            _window = null;
        }
    }

    public void Dispose() => _window?.Dispose();
}

/// <summary>Binds every render test to the one context. Also disables parallelism between them,
/// which xunit does per collection — see <see cref="GlFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class GlCollection : ICollectionFixture<GlFixture>
{
    public const string Name = "gl";
}
