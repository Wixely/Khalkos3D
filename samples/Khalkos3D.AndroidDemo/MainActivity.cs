using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace Khalkos3D.AndroidDemo;

/// <summary>
/// One activity holding one GL surface. There is nothing else to the Android app.
/// </summary>
[Activity(
    Label = "Khalkos3D",
    MainLauncher = true,
    // Handled rather than recreated: a rotation would otherwise tear down the EGL context and lose
    // whatever view the user had orbited to. GLSurfaceView reports the new size through
    // OnSurfaceChanged, which is all the demo needs to know.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : Activity
{
    private DemoSurfaceView? _view;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // No layout file: the surface IS the interface, and a demo whose only chrome is an XML file
        // nobody reads is worse than one with none.
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        _view = new DemoSurfaceView(this);
        SetContentView(_view);
    }

    // Both are required by GLSurfaceView, which suspends its render thread and releases the context
    // between them. Skipping either leaves a thread spinning on a surface that no longer exists.
    protected override void OnPause()
    {
        base.OnPause();
        _view?.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _view?.OnResume();
    }
}
