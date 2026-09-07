using System.Numerics;
using Khalkos3D.Formats;

namespace Khalkos3D.Demo;

/// <summary>
/// What the demo shows: either a model from disk, or a scene built in code when there is no file to
/// open — which is every launch on a phone, and the default on a desktop.
/// </summary>
public static class DemoScene
{
    /// <summary>Resource name of the logo, set by <c>LogicalName</c> in the project file.</summary>
    private const string LogoResource = "Khalkos3D.Demo.Khalkos3D.stl";

    /// <summary>How tall the logo is made, in the units everything else here is expressed in.</summary>
    private const float LogoHeight = 3f;

    /// <summary>Radians per second. Slow on purpose: fast enough to read as motion within a second
    /// of launch, slow enough that a still photograph of it is not blurred.</summary>
    private const float LogoTurn = 0.35f;

    /// <summary>
    /// Where in its turn the logo starts, so the lettering faces the camera the demo opens with
    /// rather than arriving edge-on and turning into view some seconds later.
    ///
    /// <para>The face of the model points along +Z and <see cref="OrbitController.Frame"/> places
    /// the camera 0.6 radians off +X, hence the quarter turn less that. It decides nothing but which
    /// moment of the turn you arrive at, so a change to the default framing costs at worst a
    /// slightly oblique first frame.</para>
    /// </summary>
    private const float LogoFacing = MathF.PI / 2f - 0.6f;

    /// <summary>
    /// The Khalkos3D logo, turning, with metal spheres and dielectric cubes in orbit around it.
    ///
    /// <para><b>Chosen because it fails visibly.</b> A single static object looks correct under
    /// almost any broken shader. This does not: the orbiting bodies sweep roughness from near-mirror
    /// to nearly matte, so if the lighting maths is wrong the sweep stops varying, if the
    /// environment is missing the metals go black — a metal has no diffuse colour to fall back
    /// on — and if normals are inverted everything lights from the wrong side. The motion adds a
    /// second check the old still scene could not make: a scene graph that is rebuilt every frame
    /// but re-uploads nothing, which is visible as a demo that turns smoothly rather than one that
    /// stutters while the geometry goes back up the bus.</para>
    ///
    /// <para>The logo itself is a real STL — the same file the README's image was made from —
    /// loaded through <see cref="StlReader"/> like any other model rather than built in code. A
    /// sample for an asset layer should open an asset.</para>
    /// </summary>
    public static AnimatedScene Showcase()
    {
        var logo = LoadLogo();
        var mesh = logo.Meshes[0];

        // Centred on the origin, scaled to LogoHeight, then lifted to stand ON the grid rather than
        // through it. The file is modelled in millimetres somewhere out in the first quadrant, which
        // is normal for an exported glyph and no use to a scene that wants to spin it about itself.
        var bounds = mesh.Bounds;
        var place = Matrix4x4.CreateTranslation(-bounds.Center)
            * Matrix4x4.CreateScale(LogoHeight / bounds.Size.Y);
        var lift = Matrix4x4.CreateTranslation(0f, LogoHeight / 2f, 0f);

        var meshes = new List<Mesh>
        {
            mesh,
            Solids.Sphere(0.42f, name: "sphere"),
            Solids.Box(new Vector3(0.62f), name: "cube"),
        };

        // Copper, because that is what khalkós means — but the colour is taken from the artwork
        // rather than from a physics table, and that is a deliberate departure worth explaining.
        // Real copper's reflectance is a pale (0.955, 0.638, 0.538); on a FLAT face, which reflects
        // one patch of a grey environment and no more, that renders as dusty pink and reads as
        // plastic. A sphere would show the whole environment across its surface and look right. So
        // the logo gets the brand's colour and the orbiting bodies below get the physical treatment.
        var materials = new List<Material>
        {
            new() { Name = "copper", BaseColor = new(0.78f, 0.36f, 0.18f, 1f), Metallic = 1f, Roughness = 0.28f },
        };

        var orbiters = new List<Orbiter>();

        // The inner ring: gold, metallic, sweeping roughness. Every one of these is the same mesh
        // and a different material, which is the arrangement a real scene has.
        const int spheres = 5;
        for (var i = 0; i < spheres; i++)
        {
            // Never quite 0: a perfect mirror has nothing to reflect but the environment, and reads
            // as a bug rather than as a polished surface.
            var roughness = 0.06f + i * (0.84f / (spheres - 1));
            materials.Add(new Material
            {
                Name = $"metal-{roughness:0.00}",
                BaseColor = new Vector4(0.94f, 0.78f, 0.36f, 1f),
                Metallic = 1f,
                Roughness = roughness,
            });

            orbiters.Add(new Orbiter(
                Mesh: 1,
                Material: materials.Count - 1,
                Radius: 2.6f,
                Height: 0.95f,
                Phase: MathF.Tau * i / spheres,
                Orbit: 0.30f,
                Spin: 0.9f,
                Bob: 0.22f));
        }

        // The outer ring: plastic, counter-turning, higher up, and cubes — a sphere spinning on its
        // own axis is invisible however correct it is, so the visible half of "rotating" is carried
        // by something with corners.
        const int cubes = 4;
        for (var i = 0; i < cubes; i++)
        {
            var roughness = 0.12f + i * (0.66f / (cubes - 1));
            materials.Add(new Material
            {
                Name = $"plastic-{roughness:0.00}",
                BaseColor = new Vector4(0.20f, 0.45f, 0.85f, 1f),
                Roughness = roughness,
            });

            orbiters.Add(new Orbiter(
                Mesh: 2,
                Material: materials.Count - 1,
                Radius: 3.3f,
                Height: 2.15f,
                Phase: MathF.Tau * i / cubes,
                Orbit: -0.19f,
                Spin: -1.3f,
                Bob: 0.30f));
        }

        var parts = new Scene
        {
            Meshes = meshes,
            Materials = materials,
            // The loader's notes are carried through, so a host that prints them prints the truth
            // about this file rather than an empty list for a scene that did in fact load one.
            Report = logo.Report,
            Up = UpAxis.Y,
        };

        return AnimatedScene.Moving(parts, seconds =>
        {
            var nodes = new List<Node>(orbiters.Count + 1)
            {
                new()
                {
                    Name = "khalkos3d",
                    Mesh = 0,
                    Material = 0,
                    Transform = place * Matrix4x4.CreateRotationY(LogoFacing + seconds * LogoTurn) * lift,
                },
            };

            foreach (var orbiter in orbiters) nodes.Add(orbiter.At(seconds));
            return nodes;
        });
    }

    /// <summary>
    /// Open a model. Any format the engine reads; the caller decides what to do with the report.
    ///
    /// <para>No image decoder is supplied, so a textured model arrives untextured and says so in its
    /// <see cref="AnimatedScene.Report"/>. That is the honest default for a sample: pulling in a
    /// codec to make one demo prettier would put a dependency in front of everyone who only wanted
    /// to see whether the engine runs.</para>
    ///
    /// <para>Returned as an <see cref="AnimatedScene"/> that ignores the clock, so a host shows a
    /// file and shows the built-in scene the same way.</para>
    /// </summary>
    public static AnimatedScene Load(string path) => AnimatedScene.Still(ModelReader.ReadFile(path));

    /// <summary>
    /// The logo, read from an STL compiled into this assembly.
    ///
    /// <para><b>Embedded rather than copied next to the executable</b>, because "next to the
    /// executable" is a desktop idea. On Android the same code runs out of an APK and on the browser
    /// out of a download, and neither has a working directory to put a file in. One resource works
    /// on all three, which is the arrangement the rest of this project keeps arguing for.</para>
    /// </summary>
    private static Scene LoadLogo()
    {
        using var stream = typeof(DemoScene).Assembly.GetManifestResourceStream(LogoResource)
            ?? throw new InvalidOperationException(
                $"{LogoResource} is not embedded in this assembly; see Khalkos3D.Demo.csproj");

        return StlReader.Read(stream, name: "khalkos3d");
    }

    /// <summary>
    /// One body in orbit: where it goes round, how fast, and how fast it turns on its own axis.
    ///
    /// <para>Held as data and evaluated per frame rather than as accumulated state. Position is a
    /// function of the time, so the scene cannot drift when frames are late, and pausing is nothing
    /// more than not advancing the clock.</para>
    /// </summary>
    /// <param name="Mesh">Index into the scene's meshes.</param>
    /// <param name="Material">Index into the scene's materials.</param>
    /// <param name="Radius">Distance from the axis the logo turns on.</param>
    /// <param name="Height">Height it circles at, before bobbing.</param>
    /// <param name="Phase">Where on the ring it starts, in radians.</param>
    /// <param name="Orbit">Radians per second around the logo; negative goes the other way.</param>
    /// <param name="Spin">Radians per second about its own axes.</param>
    /// <param name="Bob">How far it rises and falls, in scene units.</param>
    private readonly record struct Orbiter(
        int Mesh, int Material, float Radius, float Height, float Phase, float Orbit, float Spin, float Bob)
    {
        internal Node At(float seconds)
        {
            var angle = Phase + seconds * Orbit;
            // The bob is deliberately off the orbital period, so the ring never settles into a
            // pattern that reads as one rigid object rotating.
            var height = Height + MathF.Sin(seconds * 0.7f + Phase) * Bob;

            return new Node
            {
                Mesh = Mesh,
                Material = Material,
                Transform =
                    Matrix4x4.CreateFromYawPitchRoll(seconds * Spin, seconds * Spin * 0.6f, 0f)
                    * Matrix4x4.CreateTranslation(
                        MathF.Cos(angle) * Radius, height, MathF.Sin(angle) * Radius),
            };
        }
    }
}
