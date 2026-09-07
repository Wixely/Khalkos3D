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
    /// Brushed copper, for the logo: parallel streaks of grain, each catching the light at a
    /// slightly different angle.
    ///
    /// <para><b>Keyed to OBJECT space, which is the whole reason <c>Surface.object</c> exists.</b>
    /// The logo turns, and a pattern keyed to where it happens to be would swim across the metal as
    /// it went round — grain belongs to the thing, not to the room.</para>
    ///
    /// <para><b>The tilt is what makes it read as brushed rather than as a texture.</b> A flat face
    /// reflects one patch of the environment, so varying the colour alone gives stripes on a flat
    /// sheet; nudging the normal across the brush is what a groove physically does, and it is what
    /// makes the streaks light up as the logo turns past the lamp.</para>
    /// </summary>
    private static readonly Shader Brushed = Shader.Surface("""
        uniform float uLanes;    // streaks per unit of the model's own height
        uniform float uAlong;    // how quickly a streak varies along its length
        uniform vec3  uCopper;

        float grainAt(float n) { return fract(sin(n * 12.9898) * 43758.5453); }

        // One pass of the brush, FILTERED AGAINST THE PIXEL GRID.
        //
        // A streak is a lane: constant along the brush and changing across it, which is what makes
        // the grain directional rather than noise. The filtering is the part that matters at this
        // density — fwidth says how many lanes fall inside one pixel, and once that passes about one
        // there is nothing left to resolve, so drawing the lanes anyway produces sparkle rather than
        // detail. Fading each pass towards its own mean as it reaches that limit is what lets the
        // grain be genuinely fine: sharp when the camera is close, smooth when it is not, and never
        // the shimmering mess that a high frequency drawn unfiltered gives.
        float brushPass(vec3 at, float lanes, float along) {
            float across = at.y * lanes;
            float lane = floor(across);
            float grain = mix(grainAt(lane), grainAt(lane * 2.7 + 5.1), 0.5);

            // A slow variation ALONG the streak as well, so it is not a perfectly even line — real
            // brushing wanders.
            float wander = 0.5 + 0.5 * sin(at.x * along + lane * 11.0);
            float streak = mix(grain, grain * wander, 0.4);

            float clarity = clamp(1.0 - fwidth(across) * 0.75, 0.0, 1.0);
            return mix(0.5, streak, clarity);
        }

        void surface(inout Surface s) {
            // Three passes an octave or so apart, which is what a brushed surface actually carries:
            // fine scratches from the abrasive, a coarser rhythm from the pass of the tool, and a
            // broad unevenness across the sheet. One frequency alone reads as corduroy.
            float fine   = brushPass(s.object, uLanes,         uAlong);
            float medium = brushPass(s.object, uLanes * 0.28,  uAlong * 0.7);
            float coarse = brushPass(s.object, uLanes * 0.085, uAlong * 0.4);
            float streak = fine * 0.46 + medium * 0.34 + coarse * 0.20;

            s.metallic = 1.0;
            s.baseColor.rgb = uCopper * (0.75 + 0.50 * streak);
            s.roughness = clamp(0.14 + 0.30 * streak, 0.05, 1.0);
            s.normal = normalize(s.normal + uUpAxis * (streak - 0.5) * 0.22);

            // THE ANISOTROPIC SHEEN, and it is here rather than in the engine on purpose: a brushed
            // highlight is stretched ALONG the grain, and the engine's BRDF is isotropic — it has one
            // roughness and no notion of which way a surface was brushed. Adding a second, directional
            // model to the engine for one material would be the wrong trade; putting it in the shader
            // that wants it is exactly what this feature is for.
            //
            // It goes into emissive because that is the one channel a surface hook can put light
            // into. On a face this flat it is also the only thing that reads as metal at all: the
            // environment barely changes across a plane, so without it the grain is a pattern on a
            // dull sheet.
            vec3 L = normalize(-uLightDir);
            vec3 H = normalize(L + s.view);

            // The grooves FAN, and that detail is the whole effect. Tilting the normal alone leaves
            // the brush direction untouched — cross(up, n + up*k) is cross(up, n) — so the sheen
            // comes out identical across a flat face and reads as a brightness lift rather than as
            // metal. Turning each lane's groove by a fraction of a degree is what makes some of them
            // catch the light while their neighbours do not.
            vec3 tangent = normalize(cross(uUpAxis, s.normal));  // along the streaks
            vec3 across = cross(s.normal, tangent);
            vec3 brush = normalize(tangent + across * (streak - 0.5) * 0.30);

            float axis = dot(brush, H);
            float sheen = pow(max(0.0, 1.0 - axis * axis), 90.0);

            // Desaturated a little towards the light: a metal's highlight carries its own tint, but
            // the brightest part of a real one is close to white and that is what stops this reading
            // as a brown surface with orange stripes on it.
            // Two lobes: a narrow one for the glints that pick out individual grooves, and a broad
            // one that lifts the whole face the way a metal reads brighter than its surroundings.
            // With only the narrow lobe the metal is dark between glints and looks like grained wood.
            float broad = pow(max(0.0, 1.0 - axis * axis), 12.0);
            vec3 gleam = mix(uCopper, vec3(1.0), 0.06);

            // Kept under the tone mapper's shoulder on purpose. Reinhard compresses a channel that
            // is already near 1 far harder than the others, so pushing the sheen for brightness
            // desaturates the copper into salmon — the metal gets paler the more it gleams.
            s.emissive += gleam * uLightColor * (sheen * 0.42 + broad * 0.10) * (0.25 + 0.75 * streak);
        }
        """, name: "brushed copper");

    /// <summary>
    /// The shader on the orbiting cubes: iron heated until it glows, breathing between dull red and
    /// nearly white.
    ///
    /// <para><b>Its heat is a uniform rather than a clock.</b> The same number drives the light each
    /// cube casts into the scene, and a shader that computed its own pulse from the time would be the
    /// same curve written twice in two languages — where the glow and the light it throws would drift
    /// apart the first time either was edited. One value, set from C#, used by both.</para>
    ///
    /// <para>This is a <see cref="ShaderKind.Surface"/> shader, so it contains no version directive,
    /// no varyings and no lighting: the same lines compile on desktop GL 3.3, on the phone's GLES 3.0
    /// and in a browser's WebGL2, the cubes still answer W, N and B, and — having been written before
    /// the engine had more than one light — it is lit by all five of them without a word about it.</para>
    /// </summary>
    private static readonly Shader Hot = Shader.Surface("""
        uniform float uHeat;    // 0 dull iron, 1 at its hottest
        uniform vec3  uEmber;   // the colour at that peak

        void surface(inout Surface s) {
            // Hotter around the silhouette: a glowing solid is brighter where you look through more
            // of it, and this is the cheap stand-in for that — the same reason a hot bar looks like
            // it has a bright edge.
            float rim = pow(1.0 - max(dot(normalize(s.normal), s.view), 0.0), 2.0);

            // Crust, in the model's own space so it stays on the iron while the cube tumbles. A flat
            // face of a solid emissive colour reads as a card rather than as metal: real hot metal is
            // uneven, cooler where scale has formed and brighter in the cracks between.
            float crust = 0.5 + 0.5 * sin(s.object.x * 26.0) * sin(s.object.y * 21.0) * sin(s.object.z * 31.0);
            crust = mix(crust, 0.5 + 0.5 * sin(s.object.x * 61.0 + 2.1) * sin(s.object.z * 47.0 - 1.3), 0.45);

            float glow = uHeat * (0.55 + 0.45 * rim) * (0.80 + 0.34 * crust);

            // Up the blackbody ramp: dull red first, then the ember colour, then towards white at the
            // top of the pulse. Metal does not brighten while keeping its hue, and a glow that does
            // reads as a light bulb painted orange.
            vec3 hot = mix(vec3(0.85, 0.09, 0.02), uEmber, smoothstep(0.15, 0.85, uHeat));
            // Only a hint of white at the very top: iron this bright is rare, and a cube that
            // reaches white every cycle reads as a lamp rather than as metal.
            hot = mix(hot, vec3(1.0, 0.93, 0.82), smoothstep(0.88, 1.0, uHeat) * 0.30);

            s.baseColor.rgb = mix(s.baseColor.rgb, hot * 0.30, glow);
            s.metallic = mix(s.metallic, 0.15, glow);
            s.roughness = mix(s.roughness, 0.45, glow);
            s.emissive = hot * glow * 1.7;
        }
        """, name: "hot iron");

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
        //
        // The brush is measured in the MODEL'S OWN UNITS, taken from its bounds: this file is in
        // millimetres and hundreds of them tall, and a streak count means something in a scene where
        // a size in units would mean nothing.
        var materials = new List<Material>
        {
            new()
            {
                Name = "brushed copper",
                BaseColor = new Vector4(0.78f, 0.36f, 0.18f, 1f),
                Metallic = 1f,
                Roughness = 0.28f,
                Shader = Brushed,
                ShaderValues = new Dictionary<string, ShaderValue>
                {
                    ["uLanes"] = 1600f / bounds.Size.Y,   // the finest pass; it resolves when you zoom in
                    ["uAlong"] = MathF.Tau * 14f / bounds.Size.X,
                    ["uCopper"] = new Vector3(0.86f, 0.36f, 0.15f),
                },
            },
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
        // by something with corners. These are the ones carrying the sample's own shader.
        const int cubes = 4;
        var firstCube = materials.Count;
        for (var i = 0; i < cubes; i++)
        {
            orbiters.Add(new Orbiter(
                Mesh: 2,
                Material: firstCube + i,
                Radius: 3.3f,
                Height: 2.15f,
                Phase: MathF.Tau * i / cubes,
                Orbit: -0.19f,
                Spin: -1.3f,
                Bob: 0.30f));
        }

        // The cubes' materials are rebuilt every frame, because their heat changes. Everything ahead
        // of them in the list is fixed and the order never changes — the orbiters above hold indices
        // into it.
        IReadOnlyList<Material> Dress(float seconds)
        {
            var all = new List<Material>(materials);
            for (var i = 0; i < cubes; i++)
            {
                var heat = Heat(seconds, i);
                all.Add(new Material
                {
                    Name = $"iron-{i}",
                    // Cold iron underneath, which is what the glow is mixed into rather than added
                    // on top of: a hot bar is not a cold bar with light in front of it.
                    BaseColor = new Vector4(0.16f, 0.15f, 0.15f, 1f),
                    Metallic = 1f,
                    Roughness = 0.55f,
                    Shader = Hot,
                    ShaderValues = new Dictionary<string, ShaderValue>
                    {
                        ["uHeat"] = heat,
                        ["uEmber"] = Ember,
                    },
                });
            }
            return all;
        }

        // WHAT THE CUBES THROW INTO THE SCENE, which is the point of them being hot rather than
        // merely painted hot. Each is a point light at the cube's own position, coloured by the same
        // heat its shader is given and reaching about as far as the ring is wide — so the logo and
        // the spheres are lit by four moving lamps that brighten and fade, and the light on them
        // agrees with the glow you can see because both come from one number.
        //
        // The key light is spelled out here rather than left to the renderer: a filled Lights list
        // REPLACES the default rather than adding to it, so a scene that lights itself has to say
        // that it still wants the lamp everything else is lit by.
        IReadOnlyList<Light> Lights(float seconds)
        {
            var lights = new List<Light>(cubes + 1)
            {
                // Dimmer than the engine's default key light, so the four moving lamps are visibly
                // doing the work rather than adding a tint to something already fully lit.
                Light.Directional(RenderSettings.Default.LightDirection, new Vector3(0.9f)),
            };

            for (var i = 0; i < cubes; i++)
            {
                var heat = Heat(seconds, i);
                var where = orbiters[spheres + i].At(seconds).Transform.Translation;
                // Cubed, so the light falls away faster than the glow does. A lamp that dims linearly
                // with a visible ember reads as a light with a painted object near it; light from
                // something actually hot goes almost out while the metal is still visibly red.
                lights.Add(Light.Point(where, Ember * (20f * heat * heat * heat), range: 6.5f));
            }

            return lights;
        }

        var parts = new Scene
        {
            Meshes = meshes,
            Materials = Dress(0f),
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
        }, Dress, Lights);
    }

    /// <summary>The colour these cubes glow at the top of their pulse, linear RGB.</summary>
    private static readonly Vector3 Ember = new(1.0f, 0.42f, 0.10f);

    /// <summary>
    /// How hot one cube is at a moment: 0 is dull iron, 1 is the top of its pulse.
    ///
    /// <para><b>One curve, used twice.</b> It is handed to the shader as a uniform and used again to
    /// colour the light that cube casts, so the glow and the light always agree. Computing the pulse
    /// inside the shader would be the same maths in two languages, and the first edit to either would
    /// leave a cube that glows without lighting anything, or lights the scene while looking cold.</para>
    ///
    /// <para>Squared rather than a plain sine, and never quite zero: heat lingers at the bottom of a
    /// cycle and peaks sharply, which is what makes it read as something cooling and reheating rather
    /// than as a brightness slider being waved.</para>
    /// </summary>
    private static float Heat(float seconds, int cube)
    {
        var pulse = 0.5f + 0.5f * MathF.Sin(seconds * 1.15f + cube * 1.7f);
        return 0.12f + 0.88f * pulse * pulse;
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
