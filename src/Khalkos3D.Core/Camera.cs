using System.Numerics;

namespace Khalkos3D;

/// <summary>
/// Which way is up in a file's own coordinates.
///
/// <para><b>The formats genuinely disagree, and a viewer that ignores it lays every printed part on
/// its side.</b> glTF specifies Y-up. CAD and 3D printing are Z-up by universal convention — an STL
/// or a 3MF out of any slicer or CAD package has the build plate in XY and height along Z. OBJ is
/// usually Y-up because it comes from content tools.</para>
///
/// <para>Nothing here rotates the geometry. The mesh stays exactly as the file wrote it, which is
/// what makes a round trip lossless; only the CAMERA is told which axis points up, so the default
/// view is the one the author intended.</para>
/// </summary>
public enum UpAxis
{
    /// <summary>Y is up. glTF, and most content-authoring tools.</summary>
    Y,
    /// <summary>Z is up. CAD, 3D printing, and every slicer.</summary>
    Z,
}

/// <summary>
/// Where the view is from, and what it can see. A plain value: no state, no matrices cached, nothing
/// that has to be kept in step.
/// </summary>
/// <param name="Position">Eye position in world space.</param>
/// <param name="Target">The point being looked at.</param>
/// <param name="Up">Which way is up for the view. Must not be parallel to the view direction.</param>
/// <param name="FieldOfView">Vertical field of view, in radians.</param>
/// <param name="Near">Near clip distance. See <see cref="Frame"/> on why this is not a constant.</param>
/// <param name="Far">Far clip distance.</param>
public readonly record struct Camera(
    Vector3 Position,
    Vector3 Target,
    Vector3 Up,
    float FieldOfView,
    float Near,
    float Far)
{
    /// <summary>A sensible default vertical field of view: 40 degrees, which is close enough to a
    /// long lens to keep perspective distortion off a mechanical part without flattening it.</summary>
    public const float DefaultFieldOfView = 40f * MathF.PI / 180f;

    /// <summary>The view matrix.</summary>
    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Up);

    /// <summary>The projection matrix for a viewport of this aspect ratio (width / height).</summary>
    public Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect > 0f ? aspect : 1f, Near, Far);

    /// <summary>View and projection combined — what a shader actually wants.</summary>
    public Matrix4x4 ViewProjection(float aspect) => View * Projection(aspect);

    /// <summary>Distance from the eye to the target.</summary>
    public float Distance => Vector3.Distance(Position, Target);

    /// <summary>
    /// Place a camera so that everything in <paramref name="bounds"/> is on screen, orbiting the
    /// centre at the given angles.
    ///
    /// <para><b>The near and far planes are derived from the model, not fixed.</b> A constant pair
    /// has to span everything from a 5 mm printed clip to a 300 m terrain, and no pair does: too near
    /// a near plane wrecks depth precision and the model self-stripes, too far and the front of it is
    /// clipped away. Deriving them from the radius makes depth precision the same for a bracket and
    /// for a building, which is exactly the kind of thing "unified experience" has to mean.</para>
    /// </summary>
    /// <param name="bounds">What must be visible. An empty box yields a camera looking at the origin.</param>
    /// <param name="up">Which axis the file treats as up.</param>
    /// <param name="yaw">Rotation about the up axis, radians. 0 looks along -X towards the model.</param>
    /// <param name="pitch">Elevation above the horizon, radians. Clamped just short of straight
    /// down, since a view direction parallel to up has no defined orientation.</param>
    /// <param name="zoom">Multiplies the fitted distance. Below 1 moves closer.</param>
    /// <param name="fieldOfView">Vertical field of view in radians.</param>
    /// <param name="aspect">Viewport width divided by height. Fitting vertically alone is only safe
    /// while this is at least 1 — a PORTRAIT viewport has a narrower horizontal field than vertical,
    /// so a wide model would be cut off at the sides. Every phone held upright is that case.</param>
    public static Camera Frame(BoundingBox bounds, UpAxis up = UpAxis.Y,
                               float yaw = 0.6f, float pitch = 0.5f, float zoom = 1f,
                               float fieldOfView = DefaultFieldOfView, float aspect = 1f)
    {
        var centre = bounds.IsEmpty ? Vector3.Zero : bounds.Center;
        var radius = bounds.IsEmpty ? 1f : MathF.Max(bounds.Radius, 1e-4f);

        // Fit the SPHERE around the box rather than the box: it is orientation-independent, so
        // spinning the model cannot push a corner out of frame — which fitting the box's projected
        // extent would allow at exactly the angles a user rotates to.
        //
        // And fit whichever field of view is NARROWER. A perspective projection is specified
        // vertically, so the horizontal field follows the aspect ratio: wider than tall gives extra
        // horizontal room and vertical is the constraint, but taller than wide reverses that and a
        // vertical-only fit clips the sides.
        var half = MathF.Max(fieldOfView * 0.5f, 1e-3f);
        if (aspect > 0f && aspect < 1f) half = MathF.Atan(MathF.Tan(half) * aspect);
        var distance = radius / MathF.Sin(MathF.Max(half, 1e-3f)) * zoom;

        const float limit = MathF.PI * 0.5f - 0.01f;
        pitch = Math.Clamp(pitch, -limit, limit);
        var horizontal = MathF.Cos(pitch) * distance;

        var upVector = up == UpAxis.Z ? Vector3.UnitZ : Vector3.UnitY;
        var offset = up == UpAxis.Z
            ? new Vector3(MathF.Cos(yaw) * horizontal, MathF.Sin(yaw) * horizontal, MathF.Sin(pitch) * distance)
            : new Vector3(MathF.Cos(yaw) * horizontal, MathF.Sin(pitch) * distance, MathF.Sin(yaw) * horizontal);

        return new Camera(
            centre + offset, centre, upVector, fieldOfView,
            // A hundredth of the radius is close enough to never clip the front of the model, and far
            // enough out that a 24-bit depth buffer still separates its back faces. Four radii of
            // range behind the centre covers the model plus room to orbit out.
            Near: MathF.Max(distance - radius * 2f, radius * 0.01f),
            Far: distance + radius * 4f);
    }

    /// <summary>This camera orbited by the given deltas, keeping its target and distance. What a
    /// drag gesture does.</summary>
    public Camera Orbit(float deltaYaw, float deltaPitch, UpAxis up = UpAxis.Y)
    {
        var offset = Position - Target;
        var distance = offset.Length();
        if (distance < 1e-6f) return this;

        var upIsZ = up == UpAxis.Z;
        var height = upIsZ ? offset.Z : offset.Y;
        var pitch = MathF.Asin(Math.Clamp(height / distance, -1f, 1f)) + deltaPitch;
        var yaw = upIsZ ? MathF.Atan2(offset.Y, offset.X) : MathF.Atan2(offset.Z, offset.X);
        yaw += deltaYaw;

        const float limit = MathF.PI * 0.5f - 0.01f;
        pitch = Math.Clamp(pitch, -limit, limit);
        var horizontal = MathF.Cos(pitch) * distance;
        var rotated = upIsZ
            ? new Vector3(MathF.Cos(yaw) * horizontal, MathF.Sin(yaw) * horizontal, MathF.Sin(pitch) * distance)
            : new Vector3(MathF.Cos(yaw) * horizontal, MathF.Sin(pitch) * distance, MathF.Sin(yaw) * horizontal);

        return this with { Position = Target + rotated };
    }

    /// <summary>
    /// Slide the view sideways and up, taking the target with it.
    ///
    /// <para><b>Scaled by DISTANCE, which is the whole trick.</b> A pan of a fixed number of world
    /// units feels wild when zoomed out and useless when zoomed in; scaling by how far away the target
    /// is means a drag moves the model by the same fraction of the screen whatever the zoom. That is
    /// what makes panning feel like dragging the object rather than nudging a camera.</para>
    /// </summary>
    /// <param name="dx">Fraction of the viewport to slide right.</param>
    /// <param name="dy">Fraction of the viewport to slide up.</param>
    public Camera Pan(float dx, float dy)
    {
        var forward = Target - Position;
        var distance = forward.Length();
        if (distance < 1e-6f) return this;
        forward /= distance;

        var right = Vector3.Cross(forward, Up);
        var length = right.Length();
        if (length < 1e-6f) return this;      // looking straight along up; nothing to slide along
        right /= length;
        var up = Vector3.Cross(right, forward);

        // The visible height at the target's distance. Matching it means a drag of the full viewport
        // moves the model exactly one screen.
        var extent = 2f * distance * MathF.Tan(FieldOfView * 0.5f);
        var shift = right * (-dx * extent) + up * (dy * extent);

        return this with { Position = Position + shift, Target = Target + shift };
    }

    /// <summary>This camera moved towards or away from its target. <paramref name="factor"/> below 1
    /// moves closer. The clip planes follow, so precision does not decay as the view closes in.</summary>
    public Camera Zoom(float factor)
    {
        if (factor <= 0f || !float.IsFinite(factor)) return this;
        var offset = (Position - Target) * factor;
        var distance = offset.Length();
        if (distance < 1e-6f) return this;

        var radius = Distance > 0f ? Distance * MathF.Sin(FieldOfView * 0.5f) : 1f;
        return this with
        {
            Position = Target + offset,
            Near = MathF.Max(distance - radius * 2f, distance * 0.001f),
            Far = distance + radius * 4f,
        };
    }
}
