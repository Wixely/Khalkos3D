using System.Numerics;

namespace Khalkos3D;

/// <summary>
/// One thing to draw: a transform, optionally a mesh, and children.
///
/// <para>Flat files use one of these and stop. glTF uses a tree, and the tree matters — a printer
/// plate holding six copies of a part is six nodes sharing one mesh, which is the difference between
/// uploading the geometry once and uploading it six times.</para>
/// </summary>
public sealed class Node
{
    /// <summary>Name from the file.</summary>
    public string? Name { get; init; }

    /// <summary>Transform relative to the parent. Identity by default.</summary>
    public Matrix4x4 Transform { get; init; } = Matrix4x4.Identity;

    /// <summary>Index into <see cref="Scene.Meshes"/>, or null for a node that only groups.</summary>
    public int? Mesh { get; init; }

    /// <summary>Index into <see cref="Scene.Materials"/>, or null for <see cref="Material.Default"/>.</summary>
    public int? Material { get; init; }

    /// <summary>Children, in file order.</summary>
    public IReadOnlyList<Node> Children { get; init; } = [];
}

/// <summary>An image as bytes, decoded by whoever loaded it. Straight RGBA8, top row first — the
/// layout both Skia and GL want, so neither has to flip.</summary>
/// <param name="Pixels">Width * Height * 4 bytes, RGBA.</param>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Name">Optional name from the file.</param>
public sealed record ImageData(byte[] Pixels, int Width, int Height, string? Name = null);

/// <summary>
/// A loaded asset: geometry, surfaces, images, and the hierarchy that arranges them.
///
/// <para>Every loader in <c>Khalkos3D.Formats</c> produces one of these, whatever it read. That is
/// the whole "common code" claim made concrete — a viewer, a renderer, a bounds calculation and an
/// exporter are each written once and work on STL, 3MF, OBJ and glTF alike, because the differences
/// were resolved at the parse and not pushed downstream.</para>
/// </summary>
public sealed class Scene
{
    /// <summary>Geometry, referenced by <see cref="Node.Mesh"/>.</summary>
    public IReadOnlyList<Mesh> Meshes { get; init; } = [];

    /// <summary>Surfaces, referenced by <see cref="Node.Material"/>.</summary>
    public IReadOnlyList<Material> Materials { get; init; } = [];

    /// <summary>Decoded images. Referenced by <see cref="Textures"/>, not by materials directly —
    /// the same image may be sampled two different ways in one file.</summary>
    public IReadOnlyList<ImageData> Images { get; init; } = [];

    /// <summary>Image-and-sampler pairings, referenced by <see cref="Material.BaseColorTexture"/>
    /// and friends. See <see cref="TextureRef"/> for why the sampler is not a property of the
    /// image.</summary>
    public IReadOnlyList<TextureRef> Textures { get; init; } = [];

    /// <summary>Top-level nodes.</summary>
    public IReadOnlyList<Node> Roots { get; init; } = [];

    /// <summary>What the loader could not honour. Never null, usually empty, and worth showing —
    /// see <see cref="LoadReport"/> for why it is a first-class part of the result.</summary>
    public LoadReport Report { get; init; } = LoadReport.Empty;

    /// <summary>
    /// Which axis this file treats as up, as its FORMAT defines it rather than as anything in the
    /// geometry says.
    ///
    /// <para>Carried because the formats disagree and a viewer that ignores it lays every printed
    /// part on its side: glTF is Y-up, while STL and 3MF are Z-up by the universal convention of CAD
    /// and slicing. Nothing is rotated to match — the mesh stays exactly as written, which is what
    /// keeps a round trip lossless — but <see cref="Camera.Frame"/> reads this so the default view is
    /// the one the author intended.</para>
    /// </summary>
    public UpAxis Up { get; init; } = UpAxis.Y;

    /// <summary>Total triangles across every mesh, counted once per mesh rather than per
    /// instance.</summary>
    public int TriangleCount
    {
        get { var n = 0; foreach (var m in Meshes) n += m.TriangleCount; return n; }
    }

    /// <summary>
    /// World-space bounds of everything, with each node's transform chain applied — the number a
    /// viewer frames its camera on.
    /// </summary>
    public BoundingBox Bounds
    {
        get
        {
            var box = BoundingBox.Empty;
            foreach (var root in Roots) Accumulate(root, Matrix4x4.Identity, ref box);
            return box;

            void Accumulate(Node node, Matrix4x4 parent, ref BoundingBox into)
            {
                var world = node.Transform * parent;
                if (node.Mesh is { } index && index >= 0 && index < Meshes.Count)
                    into = into.Union(Meshes[index].Bounds.Transform(world));
                foreach (var child in node.Children) Accumulate(child, world, ref into);
            }
        }
    }

    /// <summary>
    /// Every mesh in the scene paired with its world transform and material — the flat list a
    /// renderer actually draws, walked once here instead of in every backend.
    /// </summary>
    public IEnumerable<(Mesh Mesh, Matrix4x4 World, Material Material)> Draws()
    {
        foreach (var root in Roots)
            foreach (var draw in Walk(root, Matrix4x4.Identity))
                yield return draw;

        IEnumerable<(Mesh, Matrix4x4, Material)> Walk(Node node, Matrix4x4 parent)
        {
            var world = node.Transform * parent;
            if (node.Mesh is { } index && index >= 0 && index < Meshes.Count)
            {
                var material = node.Material is { } m && m >= 0 && m < Materials.Count
                    ? Materials[m] : Material.Default;
                yield return (Meshes[index], world, material);
            }
            foreach (var child in node.Children)
                foreach (var draw in Walk(child, world))
                    yield return draw;
        }
    }

    /// <summary>A scene holding one mesh — what every flat format produces, spelled once.</summary>
    public static Scene FromMesh(Mesh mesh, Material? material = null, LoadReport? report = null,
                                 UpAxis up = UpAxis.Y) =>
        new()
        {
            Meshes = [mesh],
            Materials = material is null ? [] : [material],
            Roots = [new Node { Name = mesh.Name, Mesh = 0, Material = material is null ? null : 0 }],
            Report = report ?? LoadReport.Empty,
            Up = up,
        };
}
