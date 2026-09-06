using System.Runtime.InteropServices;

namespace Khalkos3D.AndroidDemo;

/// <summary>
/// Where GLES entry points come from on Android.
///
/// <para>The engine asks for a <c>Func&lt;string, nint&gt;</c> and never for a windowing library, so
/// this is the whole of the platform's contribution to getting it running: fifteen lines and one
/// hard-won decision.</para>
/// </summary>
internal static class Gles
{
    private static nint _lib;

    /// <summary>
    /// A proc-address function for the context <c>GLSurfaceView</c> has already made current, or
    /// null with a reason.
    /// </summary>
    internal static Func<string, nint>? Loader(out string? why)
    {
        why = null;

        // libGLESv3.so directly, and deliberately NOT eglGetProcAddress. Some drivers' EGL returns
        // a non-null stub for ANY name asked of it, which makes a missing entry point look present
        // and then crashes on the call — a failure that arrives nowhere near its cause. Exported
        // symbols cannot lie that way: a name either resolves or it does not, and the engine's
        // loader reports precisely which ones it could not find.
        if (_lib == 0 && !NativeLibrary.TryLoad("libGLESv3.so", out _lib))
        {
            why = "libGLESv3.so did not load — this device has no GLES 3.0";
            return null;
        }

        var lib = _lib;
        return name => NativeLibrary.TryGetExport(lib, name, out var address) ? address : 0;
    }
}
