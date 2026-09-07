namespace Khalkos3D.Demo;

/// <summary>
/// A scene whose arrangement depends on the time: fixed meshes and materials, and a node tree
/// rebuilt for whatever moment is being drawn.
///
/// <para><b>Geometry is uploaded once regardless.</b> <see cref="Khalkos3D.Gl.GlRenderer"/> caches
/// its buffers against the <see cref="Mesh"/> object by reference, so handing it a fresh
/// <see cref="Scene"/> every frame costs a few small allocations and re-uploads nothing. That is
/// what makes animating a scene a matter of rearranging nodes rather than of touching the
/// renderer — and it is why an engine with no animation system can still show moving things.</para>
///
/// <para>A model opened from a file is the same type with an arrangement that ignores the clock;
/// see <see cref="Still"/>. One currency means a host says <c>Show</c> once and neither knows nor
/// cares which it was handed.</para>
/// </summary>
public sealed class AnimatedScene
{
    private readonly Scene _parts;
    private readonly Func<float, IReadOnlyList<Node>> _arrange;

    private AnimatedScene(Scene parts, Func<float, IReadOnlyList<Node>> arrange, bool moves, BoundingBox? focus = null)
    {
        _parts = parts;
        _arrange = arrange;
        Moves = moves;
        // Framed at the start of the animation rather than continuously: a camera that reframed
        // itself as the orbiters swung about would drift for as long as the demo is left running.
        Focus = focus ?? At(0f).Bounds;
    }

    /// <summary>
    /// A scene that moves. <paramref name="parts"/> supplies the meshes, materials and metadata,
    /// and <paramref name="arrange"/> places them for a time in seconds since the scene appeared.
    /// </summary>
    public static AnimatedScene Moving(Scene parts, Func<float, IReadOnlyList<Node>> arrange)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(arrange);
        return new AnimatedScene(parts, arrange, moves: true);
    }

    /// <summary>A scene that does not move — a model as its file laid it out.</summary>
    public static AnimatedScene Still(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return new AnimatedScene(scene, _ => scene.Roots, moves: false, scene.Bounds);
    }

    /// <summary>Whether <see cref="At"/> is worth calling more than once.</summary>
    public bool Moves { get; }

    /// <summary>What the camera should frame: the subject as it stood at time zero.</summary>
    public BoundingBox Focus { get; }

    /// <summary>Which axis is up, from the file or from whoever composed the scene.</summary>
    public UpAxis Up => _parts.Up;

    /// <summary>What the loader could not honour. Empty for a scene built in code.</summary>
    public LoadReport Report => _parts.Report;

    /// <summary>The scene as it stands <paramref name="seconds"/> after it appeared.</summary>
    public Scene At(float seconds) => new()
    {
        Meshes = _parts.Meshes,
        Materials = _parts.Materials,
        Images = _parts.Images,
        Textures = _parts.Textures,
        Roots = _arrange(seconds),
        Report = _parts.Report,
        Up = _parts.Up,
    };

    /// <summary>
    /// The same scene with a ground grid and an origin marker sized to it.
    ///
    /// <para>The grid is built once here rather than inside the per-frame arrangement, because a
    /// new grid <see cref="Mesh"/> every frame would be a new cache entry every frame — the one way
    /// to make an animated scene genuinely expensive.</para>
    ///
    /// <para><see cref="Focus"/> deliberately survives unchanged. The grid is half again as wide as
    /// its subject, and framing the two together would push the thing you came to look at into the
    /// distance to fit a floor nobody is looking at.</para>
    /// </summary>
    public AnimatedScene WithGround()
    {
        var (grid, axes) = Shapes.For(Focus, Up);

        var meshes = new List<Mesh>(_parts.Meshes) { grid, axes };
        var materials = new List<Material>(_parts.Materials) { Shapes.LineMaterial };

        // Appended, so every index the arrangement already holds still means what it meant.
        var gridNode = new Node { Name = "grid", Mesh = meshes.Count - 2, Material = materials.Count - 1 };
        var axesNode = new Node { Name = "axes", Mesh = meshes.Count - 1, Material = materials.Count - 1 };

        var parts = new Scene
        {
            Meshes = meshes,
            Materials = materials,
            Images = _parts.Images,
            Textures = _parts.Textures,
            Report = _parts.Report,
            Up = _parts.Up,
        };

        var arrange = _arrange;
        return new AnimatedScene(
            parts,
            seconds => [.. arrange(seconds), gridNode, axesNode],
            Moves,
            Focus);
    }
}
