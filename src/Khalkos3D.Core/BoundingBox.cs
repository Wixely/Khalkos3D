using System.Numerics;

namespace Khalkos3D;

/// <summary>
/// An axis-aligned box. Small, but it is what every viewer reaches for first: framing a camera on a
/// model it has never seen, checking whether something fits a region, or simply reporting how big it
/// is.
/// </summary>
public readonly record struct BoundingBox(Vector3 Min, Vector3 Max)
{
    /// <summary>The empty box: inverted, so that <see cref="Union(Vector3)"/> from here yields
    /// exactly the point given. A zero box at the origin would silently drag every model's bounds
    /// towards (0,0,0), which frames the camera wrongly and is hard to see.</summary>
    public static BoundingBox Empty { get; } =
        new(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    /// <summary>False once anything has been added.</summary>
    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    /// <summary>Width, height and depth. Zero on any axis for a flat or empty box.</summary>
    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    /// <summary>The middle.</summary>
    public Vector3 Center => IsEmpty ? Vector3.Zero : (Min + Max) * 0.5f;

    /// <summary>Half the corner-to-corner distance — the radius of the sphere that contains this
    /// box, which is what a camera framing the model actually needs.</summary>
    public float Radius => IsEmpty ? 0f : Size.Length() * 0.5f;

    /// <summary>The longest edge. Used for choosing a sensible default scale and grid spacing.</summary>
    public float LongestEdge
    {
        get { var s = Size; return MathF.Max(s.X, MathF.Max(s.Y, s.Z)); }
    }

    /// <summary>This box grown to include a point.</summary>
    public BoundingBox Union(Vector3 point) => new(Vector3.Min(Min, point), Vector3.Max(Max, point));

    /// <summary>This box grown to include another. An empty operand leaves it unchanged, which is
    /// what <see cref="Empty"/>'s inverted form buys.</summary>
    public BoundingBox Union(BoundingBox other) =>
        new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    /// <summary>The bounds of a point cloud.</summary>
    public static BoundingBox FromPoints(ReadOnlySpan<Vector3> points)
    {
        if (points.Length == 0) return Empty;
        var min = points[0];
        var max = points[0];
        for (var i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }
        return new BoundingBox(min, max);
    }

    /// <summary>
    /// This box after a transform — the bounds of the transformed CORNERS, not the transformed
    /// bounds.
    ///
    /// <para>Transforming Min and Max alone is the classic mistake: it is correct for a pure
    /// translation and wrong for every rotation, because the extreme points of a rotated box are
    /// generally different corners. The result is a box that is too small, so parts of the model
    /// fall outside the camera that was framed on it.</para>
    /// </summary>
    public BoundingBox Transform(Matrix4x4 matrix)
    {
        if (IsEmpty) return this;
        var result = Empty;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? Min.X : Max.X,
                (i & 2) == 0 ? Min.Y : Max.Y,
                (i & 4) == 0 ? Min.Z : Max.Z);
            result = result.Union(Vector3.Transform(corner, matrix));
        }
        return result;
    }

    /// <summary>
    /// A cutting plane through this box, at a fraction of the way along a direction.
    ///
    /// <para>For looking inside something: slide the plane through and see wall thickness and
    /// internal structure. Expressed against the bounds rather than in absolute coordinates, so a
    /// caller can offer a 0..1 slider without first knowing how big the model is.</para>
    /// </summary>
    /// <param name="normal">Which way to cut. Normalised on use.</param>
    /// <param name="fraction">0 puts the plane at the near extreme of the box along the normal, 1 at
    /// the far extreme. Outside that range nothing is cut off.</param>
    public Plane SectionAt(Vector3 normal, float fraction)
    {
        var length = normal.Length();
        var unit = length > 1e-6f ? normal / length : Vector3.UnitZ;
        if (IsEmpty) return new Plane(unit, 0f);

        // The extent along the cut direction, taken from the CORNERS rather than from Size: a
        // diagonal cut spans further than any single axis, and using Size would clip the model short.
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? Min.X : Max.X,
                (i & 2) == 0 ? Min.Y : Max.Y,
                (i & 4) == 0 ? Min.Z : Max.Z);
            var d = Vector3.Dot(corner, unit);
            min = MathF.Min(min, d);
            max = MathF.Max(max, d);
        }

        // Plane convention is dot(normal, p) + D = 0, so D is the negated offset.
        return new Plane(unit, -(min + (max - min) * fraction));
    }

    /// <inheritdoc/>
    public override string ToString() =>
        IsEmpty ? "empty" : $"{Size.X:0.###} x {Size.Y:0.###} x {Size.Z:0.###} at {Center}";
}
