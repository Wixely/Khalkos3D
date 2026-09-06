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

    /// <summary>The camera, after everything done to it so far.</summary>
    public Camera Camera { get; private set; } = Camera.Frame(BoundingBox.Empty);

    /// <summary>Which axis the model treats as up. Set from <see cref="Scene.Up"/>, or a printed part
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
    public void Frame(BoundingBox bounds, UpAxis up, float aspect = 1f,
                      float yaw = 0.6f, float pitch = 0.5f)
    {
        _bounds = bounds;
        Up = up;
        Aspect = aspect > 0f ? aspect : 1f;
        Camera = Camera.Frame(bounds, up, yaw, pitch, aspect: Aspect);
    }

    /// <summary>Frame a scene, taking its up axis from the file.</summary>
    public void Frame(Scene scene, float aspect = 1f)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Frame(scene.Bounds, scene.Up, aspect);
    }

    /// <summary>
    /// The viewport changed shape. Re-fits only when the model would otherwise be cut off, so a
    /// resize does not throw away a view the user has carefully set up.
    /// </summary>
    public void Resize(float aspect)
    {
        if (aspect <= 0f || !float.IsFinite(aspect)) return;
        var narrowed = aspect < Aspect;
        Aspect = aspect;

        // Only a NARROWER viewport can newly clip the model, because a perspective projection is
        // specified vertically: widening adds horizontal room, narrowing takes it away. Re-framing on
        // every resize would undo the user's orbit each time they dragged a window edge.
        if (!narrowed || _bounds.IsEmpty) return;

        var offset = Camera.Position - Camera.Target;
        var distance = offset.Length();
        var needed = Camera.Frame(_bounds, Up, aspect: aspect).Distance;
        if (needed > distance && distance > 1e-6f)
            Camera = Camera.Zoom(needed / distance);
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

    /// <summary>Return to the framing this controller started from, keeping the current aspect. The
    /// escape hatch every viewer needs after a user has lost the model off screen.</summary>
    public void Reset() => Frame(_bounds, Up, Aspect);
}
