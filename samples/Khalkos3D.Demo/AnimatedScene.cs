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
    private readonly Func<float, IReadOnlyList<Material>>? _dress;
    private readonly Func<float, IReadOnlyList<Light>>? _lights;

    private AnimatedScene(Scene parts, Func<float, IReadOnlyList<Node>> arrange,
                          Func<float, IReadOnlyList<Material>>? dress,
                          Func<float, IReadOnlyList<Light>>? lights, bool moves, BoundingBox? focus = null)
    {
        _parts = parts;
        _arrange = arrange;
        _dress = dress;
        _lights = lights;
        Moves = moves;
        // Framed at the start of the animation rather than continuously: a camera that reframed
        // itself as the orbiters swung about would drift for as long as the demo is left running.
        Focus = focus ?? At(0f).Bounds;
    }

    /// <summary>
    /// A scene that moves. <paramref name="parts"/> supplies the meshes, materials and metadata,
    /// and <paramref name="arrange"/> places them for a time in seconds since the scene appeared.
    /// </summary>
    /// <param name="parts">Meshes, materials and metadata. The MESHES are what must stay fixed — they
    /// are what the renderer caches — so this is the half that never changes.</param>
    /// <param name="arrange">Where everything is at a given moment.</param>
    /// <param name="dress">Optional: the materials at a given moment, for a scene whose surfaces
    /// change rather than only its positions — a shader uniform carrying the time, most obviously.
    /// It must return the same materials in the same order, because the nodes address them by index;
    /// what may change is what each one says. Materials are not cached on the GPU, so rebuilding them
    /// per frame costs an allocation and nothing else.</param>
    /// <param name="lights">Optional: the lights at a given moment, for a scene lit by something
    /// that moves — a lamp on an arm, or an object that is itself glowing. An empty result means the
    /// renderer's own key light, so a scene that returns none is lit exactly as one that offers no
    /// delegate at all.</param>
    public static AnimatedScene Moving(Scene parts, Func<float, IReadOnlyList<Node>> arrange,
                                       Func<float, IReadOnlyList<Material>>? dress = null,
                                       Func<float, IReadOnlyList<Light>>? lights = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(arrange);
        return new AnimatedScene(parts, arrange, dress, lights, moves: true);
    }

    /// <summary>A scene that does not move — a model as its file laid it out.</summary>
    public static AnimatedScene Still(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return new AnimatedScene(scene, _ => scene.Roots, dress: null, lights: null, moves: false, scene.Bounds);
    }

    /// <summary>Whether <see cref="At"/> is worth calling more than once.</summary>
    public bool Moves { get; }

    /// <summary>What the camera should frame: the subject as it stood at time zero.</summary>
    public BoundingBox Focus { get; }

    /// <summary>Which axis is up, from the file or from whoever composed the scene.</summary>
    public UpAxis Up => _parts.Up;

    /// <summary>What the loader could not honour. Empty for a scene built in code.</summary>
    public LoadReport Report => _parts.Report;

    /// <summary>
    /// The lights <paramref name="seconds"/> after the scene appeared, or none — which means the
    /// renderer's key light, and is what every scene that does not light itself returns.
    /// </summary>
    public IReadOnlyList<Light> LightsAt(float seconds) => _lights?.Invoke(seconds) ?? [];

    /// <summary>The scene as it stands <paramref name="seconds"/> after it appeared.</summary>
    public Scene At(float seconds) => new()
    {
        Meshes = _parts.Meshes,
        Materials = _dress is null ? _parts.Materials : _dress(seconds),
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
        var dress = _dress;
        return new AnimatedScene(
            parts,
            seconds => [.. arrange(seconds), gridNode, axesNode],
            // The line material has to be appended per frame as well when the materials are rebuilt
            // per frame, or the index the two ground nodes hold would point past the end of the list.
            dress is null ? null : seconds => [.. dress(seconds), Shapes.LineMaterial],
            _lights,
            Moves,
            Focus);
    }
}
