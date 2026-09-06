using System.Numerics;

namespace Khalkos3D;

/// <summary>
/// The scenery a viewer draws around a model: a ground grid, a printer's build plate, and the axis
/// marker that says which way is which.
///
/// <para><b>Ordinary meshes, not a special case in the renderer.</b> They come out as
/// <see cref="PrimitiveKind.Lines"/> with an <see cref="Material.Unlit"/> material, so they travel
/// the same upload, the same cache and the same draw call as any model. A renderer with a private
/// "draw the grid" path would need that path on every future backend, and would drift from the one
/// that draws everything else.</para>
///
/// <para>All of them respect the up axis, because a build plate lying in the wrong plane is worse
/// than no build plate.</para>
/// </summary>
public static class Shapes
{
    /// <summary>The unlit material these want. Vertex colours carry the actual colour, so one
    /// material serves a grid, a plate and the axes.</summary>
    public static Material LineMaterial { get; } = new()
    {
        Name = "lines",
        Unlit = true,
        BaseColor = Vector4.One,
        DoubleSided = true,
    };

    /// <summary>
    /// A ground grid centred on the origin, in the plane perpendicular to <paramref name="up"/>.
    /// </summary>
    /// <param name="extent">Half-width. The grid runs from -extent to +extent on both axes.</param>
    /// <param name="spacing">Distance between lines. Clamped so a silly value cannot ask for a
    /// million lines and stall the upload.</param>
    /// <param name="up">Which axis is up; the grid lies in the other two.</param>
    /// <param name="minor">Colour of ordinary lines.</param>
    /// <param name="major">Colour of every fifth line and the two through the origin. Brighter, so
    /// the eye can count squares without measuring them.</param>
    public static Mesh Grid(float extent, float spacing, UpAxis up,
                            Vector4? minor = null, Vector4? major = null)
    {
        if (extent <= 0f || !float.IsFinite(extent)) extent = 100f;
        if (spacing <= 0f || !float.IsFinite(spacing)) spacing = 10f;
        // At most a few thousand lines: past that they are sub-pixel and the grid is a grey haze that
        // costs more to draw than the model.
        spacing = MathF.Max(spacing, extent / 500f);

        var minorColor = minor ?? new Vector4(0.30f, 0.32f, 0.36f, 1f);
        var majorColor = major ?? new Vector4(0.48f, 0.51f, 0.56f, 1f);

        var steps = (int)MathF.Floor(extent / spacing);
        var positions = new List<Vector3>((steps * 2 + 1) * 4);
        var colors = new List<Vector4>(positions.Capacity);

        for (var i = -steps; i <= steps; i++)
        {
            var at = i * spacing;
            var colour = i % 5 == 0 ? majorColor : minorColor;
            AddLine(InPlane(at, -extent, up), InPlane(at, extent, up), colour);
            AddLine(InPlane(-extent, at, up), InPlane(extent, at, up), colour);
        }

        return Lines([.. positions], [.. colors], "grid");

        void AddLine(Vector3 a, Vector3 b, Vector4 colour)
        {
            positions.Add(a); positions.Add(b);
            colors.Add(colour); colors.Add(colour);
        }
    }

    /// <summary>
    /// A printer's build plate: the bed outline plus a grid inside it.
    /// </summary>
    /// <param name="width">Bed size along the first ground axis, in the model's own units.</param>
    /// <param name="depth">Bed size along the second.</param>
    /// <param name="spacing">Grid spacing. 10 mm is the convention every slicer uses.</param>
    /// <param name="up">Which axis is up.</param>
    /// <param name="centred">True puts the origin in the middle of the bed; false puts it at the
    /// front-left corner, which is where most printers put theirs. It matters: a part positioned
    /// against the wrong origin looks off the bed when it is not.</param>
    public static Mesh BuildPlate(float width, float depth, float spacing = 10f,
                                  UpAxis up = UpAxis.Z, bool centred = false)
    {
        if (width <= 0f || !float.IsFinite(width)) width = 220f;
        if (depth <= 0f || !float.IsFinite(depth)) depth = 220f;
        if (spacing <= 0f || !float.IsFinite(spacing)) spacing = 10f;
        spacing = MathF.Max(spacing, MathF.Max(width, depth) / 500f);

        var minX = centred ? -width / 2f : 0f;
        var minY = centred ? -depth / 2f : 0f;
        var maxX = minX + width;
        var maxY = minY + depth;

        var grid = new Vector4(0.28f, 0.30f, 0.34f, 1f);
        var edge = new Vector4(0.62f, 0.66f, 0.72f, 1f);

        var positions = new List<Vector3>(512);
        var colors = new List<Vector4>(512);

        for (var x = minX + spacing; x < maxX - 1e-4f; x += spacing)
            AddLine(InPlane(x, minY, up), InPlane(x, maxY, up), grid);
        for (var y = minY + spacing; y < maxY - 1e-4f; y += spacing)
            AddLine(InPlane(minX, y, up), InPlane(maxX, y, up), grid);

        // The outline last and brighter: it is the constraint a user is actually checking against,
        // so it should not be lost among the grid lines.
        AddLine(InPlane(minX, minY, up), InPlane(maxX, minY, up), edge);
        AddLine(InPlane(maxX, minY, up), InPlane(maxX, maxY, up), edge);
        AddLine(InPlane(maxX, maxY, up), InPlane(minX, maxY, up), edge);
        AddLine(InPlane(minX, maxY, up), InPlane(minX, minY, up), edge);

        return Lines([.. positions], [.. colors], "build plate");

        void AddLine(Vector3 a, Vector3 b, Vector4 colour)
        {
            positions.Add(a); positions.Add(b);
            colors.Add(colour); colors.Add(colour);
        }
    }

    /// <summary>
    /// Three lines from the origin, coloured red, green and blue for X, Y and Z.
    ///
    /// <para>That mapping is not a preference — it is what every CAD package, every game engine and
    /// every DCC tool uses, so it is the one thing on screen a user can read without being told.</para>
    /// </summary>
    public static Mesh Axes(float length = 10f)
    {
        if (length <= 0f || !float.IsFinite(length)) length = 10f;
        Vector3[] positions =
        [
            Vector3.Zero, new(length, 0, 0),
            Vector3.Zero, new(0, length, 0),
            Vector3.Zero, new(0, 0, length),
        ];
        Vector4[] colors =
        [
            new(0.90f, 0.22f, 0.22f, 1f), new(0.90f, 0.22f, 0.22f, 1f),
            new(0.30f, 0.80f, 0.30f, 1f), new(0.30f, 0.80f, 0.30f, 1f),
            new(0.30f, 0.50f, 0.95f, 1f), new(0.30f, 0.50f, 0.95f, 1f),
        ];
        return Lines(positions, colors, "axes");
    }

    /// <summary>
    /// A grid and axes sized to a model, which is what a viewer wants without being asked.
    ///
    /// <para>Sized from the model rather than fixed: a 5 mm printed clip and a 300 m terrain both
    /// need a grid, and one spacing cannot serve both. The step is rounded to a 1-2-5 sequence, which
    /// is what makes the numbers readable — 10 and 20 and 50, never 13.7.</para>
    /// </summary>
    public static (Mesh Grid, Mesh Axes) For(BoundingBox bounds, UpAxis up)
    {
        var size = bounds.IsEmpty ? 10f : MathF.Max(bounds.LongestEdge, 1e-3f);
        var extent = size * 1.5f;

        // Aim for roughly 20 squares across, then snap to the nearest 1, 2 or 5 times a power of ten.
        var rough = extent * 2f / 20f;
        var magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(rough)));
        var normalised = rough / magnitude;
        var spacing = magnitude * (normalised < 1.5f ? 1f : normalised < 3.5f ? 2f : normalised < 7.5f ? 5f : 10f);

        return (Grid(extent, spacing, up), Axes(size * 0.35f));
    }

    /// <summary>Place a point on the ground plane for the given up axis.</summary>
    private static Vector3 InPlane(float a, float b, UpAxis up) =>
        up == UpAxis.Z ? new Vector3(a, b, 0f) : new Vector3(a, 0f, b);

    private static Mesh Lines(Vector3[] positions, Vector4[] colors, string name)
    {
        var indices = new int[positions.Length];
        for (var i = 0; i < indices.Length; i++) indices[i] = i;
        return new Mesh
        {
            Positions = positions,
            Indices = indices,
            Colors = colors,
            Kind = PrimitiveKind.Lines,
            Name = name,
        };
    }
}
