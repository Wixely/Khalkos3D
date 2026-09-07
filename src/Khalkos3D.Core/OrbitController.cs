using System.Numerics;

namespace Khalkos3D;

/// <summary>
/// Turns drags, pans and wheel ticks into camera moves — the interaction every model viewer has, so
/// that every application does not write it again slightly differently.
///
/// <para><b>It takes NORMALISED deltas, as a fraction of the viewport, not pixels.</b> A drag across
/// half the window rotates by the same amount on a phone and on a 4K monitor, which is what makes the
/// feel identical across the six targets — pixel deltas would make the same gesture spin a phone
/// twice as far. Converting from pixels is one division the caller already knows how to do.</para>
///
/// <para>Nothing here touches a window, a device or an event loop. It is state and arithmetic, so it
/// lives in <c>Core</c> alongside the camera and is the same code whichever host is feeding it.</para>
/// </summary>
public sealed class OrbitController
{
    private BoundingBox _bounds = BoundingBox.Empty;

    // The framing this controller was given, kept so Reset can actually return to it rather than to
    // the defaults it happens to share with most callers.
    private float _yaw = 0.6f, _pitch = 0.5f, _zoom = 1f;

    /// <summary>The camera, after everything done to it so far.</summary>
    public Camera Camera { get; private set; } = Camera.Frame(BoundingBox.Empty);

    /// <summary>Which axis the model treats as up. Set from <see cref="Scene.Up"/>, or the model
    /// ends up on its side.</summary>
    public UpAxis Up { get; set; } = UpAxis.Y;

    /// <summary>Viewport width divided by height. Kept so that re-framing after a resize fits the new
    /// shape — a portrait window needs a different distance from a landscape one.</summary>
    public float Aspect { get; private set; } = 1f;

    /// <summary>How far a full-viewport drag rotates, in radians. The default turns the model about
    /// half a revolution across the window, which is brisk enough to feel direct without overshooting
    /// on a small screen.</summary>
    public float OrbitSensitivity { get; init; } = MathF.PI;

    /// <summary>How much one wheel tick zooms. 1.1 is a tenth closer per notch.</summary>
    public float ZoomPerTick { get; init; } = 1.1f;

    /// <summary>Frame a model, replacing any previous view.</summary>
    /// <param name="bounds">What must be visible.</param>
    /// <param name="up">Which axis the model treats as up.</param>
    /// <param name="aspect">Viewport width over height.</param>
    /// <param name="yaw">Rotation about the up axis, radians.</param>
    /// <param name="pitch">Elevation above the horizon, radians.</param>
    /// <param name="zoom">Multiplies the fitted distance: below 1 opens closer in. An application
    /// that knows its own scene — a viewer whose subject sits in the middle of a wider arrangement,
    /// say — usually knows better than a bounding sphere how much of the frame it wants filled.</param>
    public void Frame(BoundingBox bounds, UpAxis up, float aspect = 1f,
                      float yaw = 0.6f, float pitch = 0.5f, float zoom = 1f)
    {
        _bounds = bounds;
        Up = up;
        Aspect = aspect > 0f ? aspect : 1f;
        _yaw = yaw;
        _pitch = pitch;
        _zoom = zoom > 0f && float.IsFinite(zoom) ? zoom : 1f;
        Camera = Camera.Frame(bounds, up, _yaw, _pitch, _zoom, aspect: Aspect);
    }

    /// <summary>Frame a scene, taking its up axis from the file.</summary>
    public void Frame(Scene scene, float aspect = 1f)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Frame(scene.Bounds, scene.Up, aspect);
    }

    /// <summary>
    /// The viewport changed shape. The view is kept: the same orbit, the same pan, and the same zoom
    /// RELATIVE to what now fits.
    ///
    /// <para><b>A resize is not a request to look at something else.</b> Only the shape of the window
    /// changed, so the camera moves only as far as that shape forces it to — which, because a
    /// perspective projection is specified vertically, is not at all while the viewport is at least
    /// as wide as it is tall. A portrait window has a narrower horizontal field than a vertical one
    /// and does force the camera back; scaling the distance by the ratio the fit changed by pushes it
    /// exactly that far and no further, so a user who had zoomed in is still zoomed in by the same
    /// amount afterwards.</para>
    ///
    /// <para>Scaling both ways rather than only outwards is what makes it reversible: narrow a window
    /// and widen it again and the view is where it started, instead of having crept outwards a little
    /// with every drag of the edge.</para>
    /// </summary>
    public void Resize(float aspect)
    {
        if (aspect <= 0f || !float.IsFinite(aspect)) return;

        var previous = Aspect;
        Aspect = aspect;
        if (_bounds.IsEmpty || previous <= 0f) return;

        // What the model would need in each shape. These are equal whenever both are landscape, so
        // the ordinary case of dragging a window edge moves the camera not at all.
        var before = Camera.Frame(_bounds, Up, aspect: previous).Distance;
        var after = Camera.Frame(_bounds, Up, aspect: aspect).Distance;
        if (before <= 1e-6f || MathF.Abs(after - before) <= 1e-6f) return;

        Camera = Camera.Zoom(after / before);
    }

    /// <summary>Rotate around the target. Deltas are fractions of the viewport — a drag of the full
    /// width is 1.0.</summary>
    public void Orbit(float dx, float dy) =>
        Camera = Camera.Orbit(-dx * OrbitSensitivity, dy * OrbitSensitivity, Up);

    /// <summary>Slide the view sideways and up, moving what is being looked at. Deltas are fractions
    /// of the viewport.</summary>
    public void Pan(float dx, float dy) => Camera = Camera.Pan(dx, dy);

    /// <summary>Zoom by whole wheel notches; positive moves closer.</summary>
    public void Zoom(float ticks)
    {
        if (ticks == 0f || !float.IsFinite(ticks)) return;
        Camera = Camera.Zoom(MathF.Pow(ZoomPerTick, -ticks));
    }

    /// <summary>
    /// Return to the framing this controller started from, keeping the current aspect. The escape
    /// hatch every viewer needs after a user has lost the model off screen.
    ///
    /// <para>The framing it started from, not the defaults: an application that opened on a
    /// particular angle or closer in gets that view back, which is the one its user recognises.</para>
    /// </summary>
    public void Reset() => Frame(_bounds, Up, Aspect, _yaw, _pitch, _zoom);
}
