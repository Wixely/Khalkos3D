using System.Numerics;
using Khalkos3D.Formats;

namespace Khalkos3D.Demo;

/// <summary>
/// What the demo shows: either a model from disk, or a scene built in code when there is no file to
/// open — which is every launch on a phone, and the default on a desktop.
/// </summary>
public static class DemoScene
{
    /// <summary>
    /// Two rows of spheres sweeping roughness, metal above and dielectric below, and a box.
    ///
    /// <para><b>Chosen because it fails visibly.</b> A single cube looks correct under almost any
    /// broken shader. A roughness sweep does not: if the lighting maths is wrong the row stops
    /// varying, if the environment is missing the metals go black, and if normals are inverted the
    /// spheres light from the wrong side. One screen tells you whether the renderer works.</para>
    /// </summary>
    public static Scene Showcase()
    {
        const int count = 5;
        const float radius = 1f;
        const float gap = 2.6f;

        var meshes = new List<Mesh> { Solids.Sphere(radius, name: "sphere"), Solids.Box(new Vector3(1.8f), name: "box") };
        var materials = new List<Material>();
        var roots = new List<Node>();

        for (var row = 0; row < 2; row++)
        {
            var metallic = row == 0 ? 1f : 0f;
            for (var i = 0; i < count; i++)
            {
                // Never quite 0: a perfect mirror has nothing to reflect but the environment, and
                // reads as a bug rather than as a polished surface.
                var roughness = 0.06f + i * (0.84f / (count - 1));

                materials.Add(new Material
                {
                    Name = $"{(metallic > 0f ? "metal" : "plastic")}-{roughness:0.00}",
                    BaseColor = metallic > 0f
                        ? new Vector4(0.94f, 0.78f, 0.36f, 1f)
                        : new Vector4(0.20f, 0.45f, 0.85f, 1f),
                    Metallic = metallic,
                    Roughness = roughness,
                });

                roots.Add(new Node
                {
                    Name = materials[^1].Name,
                    Mesh = 0,
                    Material = materials.Count - 1,
                    Transform = Matrix4x4.CreateTranslation(
                        (i - (count - 1) / 2f) * gap, radius, row == 0 ? -gap * 0.75f : gap * 0.75f),
                });
            }
        }

        materials.Add(new Material { Name = "box", BaseColor = new(0.82f, 0.30f, 0.24f, 1f), Roughness = 0.35f });
        roots.Add(new Node
        {
            Name = "box",
            Mesh = 1,
            Material = materials.Count - 1,
            // Turned off-axis so its edges are visible as edges rather than as a flat silhouette.
            Transform = Matrix4x4.CreateRotationY(0.55f) * Matrix4x4.CreateTranslation(0f, 0.9f, -gap * 2.3f),
        });

        return new Scene { Meshes = meshes, Materials = materials, Roots = roots, Up = UpAxis.Y };
    }

    /// <summary>
    /// Open a model. Any format the engine reads; the caller decides what to do with the report.
    ///
    /// <para>No image decoder is supplied, so a textured model arrives untextured and says so in its
    /// <see cref="Scene.Report"/>. That is the honest default for a sample: pulling in a codec to
    /// make one demo prettier would put a dependency in front of everyone who only wanted to see
    /// whether the engine runs.</para>
    /// </summary>
    public static Scene Load(string path) => ModelReader.ReadFile(path);

    /// <summary>
    /// Add a ground grid and an origin marker, sized from what is being shown.
    ///
    /// <para>Kept separate from the model so the camera can frame the MODEL. The grid is half again
    /// as wide as the subject on purpose, and framing the combined bounds would push everything into
    /// the distance to fit a floor nobody is looking at.</para>
    /// </summary>
    public static Scene WithGround(Scene model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var (grid, axes) = Shapes.For(model.Bounds, model.Up);

        var meshes = new List<Mesh>(model.Meshes) { grid, axes };
        var materials = new List<Material>(model.Materials) { Shapes.LineMaterial };
        var line = materials.Count - 1;

        var roots = new List<Node>(model.Roots)
        {
            new() { Name = "grid", Mesh = meshes.Count - 2, Material = line },
            new() { Name = "axes", Mesh = meshes.Count - 1, Material = line },
        };

        return new Scene
        {
            Meshes = meshes,
            Materials = materials,
            Images = model.Images,
            Textures = model.Textures,
            Roots = roots,
            Report = model.Report,
            Up = model.Up,
        };
    }
}
