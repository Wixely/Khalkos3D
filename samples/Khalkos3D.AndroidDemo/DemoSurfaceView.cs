using Android.Content;
using Android.Opengl;
using Android.Util;
using Android.Views;
using Javax.Microedition.Khronos.Opengles;
using EGLConfig = Javax.Microedition.Khronos.Egl.EGLConfig;
using Khalkos3D.Demo;

namespace Khalkos3D.AndroidDemo;

/// <summary>
/// The Android host: a <c>GLSurfaceView</c>, a renderer callback, and touch.
///
/// <para><b>Everything about what is drawn lives in <see cref="DemoViewer"/></b>, shared verbatim
/// with the desktop app. What is here is what Android genuinely forces: a view that owns an EGL
/// context, three callbacks on a render thread, and gestures instead of a mouse.</para>
///
/// <para>Note the thread discipline. <c>GLSurfaceView</c> runs its renderer on its own thread and
/// touch arrives on the UI thread, so every gesture is posted across with <c>QueueEvent</c>. Calling
/// the viewer straight from <c>OnTouchEvent</c> would mutate a camera while the render thread reads
/// it — which usually looks like nothing at all, until it does.</para>
/// </summary>
internal sealed class DemoSurfaceView : GLSurfaceView
{
    private const string LogTag = "khalkos";

    private readonly DemoViewer _viewer = new();
    private float _lastX, _lastY, _lastSpan;

    internal DemoSurfaceView(Context context) : base(context)
    {
        SetEGLContextClientVersion(3);

        // Depth is requested explicitly. The default chooser is entitled to hand back a config with
        // no depth buffer, and a 3D scene without one draws whatever happened to be submitted last
        // on top — which reads as scrambled geometry rather than as a missing buffer.
        SetEGLConfigChooser(8, 8, 8, 8, 16, 0);

        SetRenderer(new Callbacks(_viewer));
        RenderMode = Rendermode.Continuously;
    }

    /// <summary>
    /// One finger orbits; two fingers pinch to zoom and slide to pan.
    ///
    /// <para>Deltas go across in PIXELS and <see cref="DemoViewer"/> divides by the viewport, which
    /// is what makes a drag across half the screen turn the model by the same amount here as on a
    /// desktop monitor several times the size.</para>
    /// </summary>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
            case MotionEventActions.PointerUp:
                // Re-seed on every change in finger count. Without this, lifting one of two fingers
                // makes the next move report the jump from the centroid to the survivor as a drag,
                // and the model leaps.
                (_lastX, _lastY) = Centroid(e);
                _lastSpan = Span(e);
                return true;

            case MotionEventActions.Move:
                var (x, y) = Centroid(e);
                var dx = x - _lastX;
                var dy = y - _lastY;
                (_lastX, _lastY) = (x, y);

                if (e.PointerCount >= 2)
                {
                    var span = Span(e);
                    if (_lastSpan > 0f && span > 0f)
                    {
                        // A pinch is a ratio; the controller counts wheel notches. One notch is
                        // ZoomPerTick, so the log converts between them and a pinch feels like the
                        // same gesture as a scroll rather than an unrelated one.
                        var ticks = MathF.Log(span / _lastSpan, 1.1f);
                        Post(v => v.Zoom(ticks));
                    }
                    _lastSpan = span;
                    Post(v => v.Pan(dx, dy));
                }
                else
                {
                    Post(v => v.Drag(dx, dy));
                }
                return true;
        }

        return base.OnTouchEvent(e);
    }

    private void Post(Action<DemoViewer> what) =>
        QueueEvent(new Java.Lang.Runnable(() => what(_viewer)));

    private static (float X, float Y) Centroid(MotionEvent e)
    {
        float x = 0f, y = 0f;
        for (var i = 0; i < e.PointerCount; i++) { x += e.GetX(i); y += e.GetY(i); }
        return (x / e.PointerCount, y / e.PointerCount);
    }

    private static float Span(MotionEvent e)
    {
        if (e.PointerCount < 2) return 0f;
        var dx = e.GetX(0) - e.GetX(1);
        var dy = e.GetY(0) - e.GetY(1);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The three render-thread callbacks. <c>OnSurfaceCreated</c> runs again after the context is
    /// lost and rebuilt — backgrounding the app is enough — so it must be able to start from
    /// nothing every time rather than only once.
    /// </summary>
    private sealed class Callbacks(DemoViewer viewer) : Java.Lang.Object, IRenderer
    {
        public void OnSurfaceCreated(IGL10? gl, EGLConfig? config)
        {
            var loader = Gles.Loader(out var why);
            if (loader is null)
            {
                Log.Error(LogTag, $"no GLES loader: {why}");
                return;
            }

            if (!viewer.Initialise(loader))
            {
                Log.Error(LogTag, $"Khalkos3D would not start: {viewer.Error}");
                return;
            }

            viewer.Show(DemoScene.Showcase());
            Log.Info(LogTag, $"ready {viewer.Driver}");
            Log.Info(LogTag, $"triangles {viewer.TriangleCount}");
        }

        public void OnSurfaceChanged(IGL10? gl, int width, int height) => viewer.Resize(width, height);

        public void OnDrawFrame(IGL10? gl) => viewer.Render();
    }
}
