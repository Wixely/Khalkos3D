namespace Khalkos3D.Gl;

/// <summary>
/// The shader, as one body with two headers — and, since callers may supply their own GLSL, as a
/// body with a hole in it.
///
/// <para><b>This is where the "one codebase" claim is either true or not.</b> Everything else can be
/// abstracted; the version directive cannot, because the same text is illegal in the other dialect.
/// So the body is written once and <see cref="Header"/> puts the right line on top — chosen from what
/// the driver said it is, not from what platform the code was compiled for.</para>
///
/// <para>The shading model is Cook-Torrance metallic-roughness: GGX distribution, Smith geometry,
/// Schlick Fresnel. It is what glTF's <c>pbrMetallicRoughness</c> actually specifies, so a material
/// read from any of the four formats lands somewhere meaningful rather than being reinterpreted by a
/// bespoke model.</para>
///
/// <para><b>The built-in program and a surface shader are the same text.</b> The engine's own
/// materials compile with a <c>surface</c> function that does nothing, and a caller's shader compiles
/// with theirs in its place — so there is one program shape to keep working on six targets rather
/// than two, and a custom surface cannot lose the debug views, the section plane or the alpha
/// handling by forgetting to implement them.</para>
/// </summary>
internal static class ShaderSource
{
    /// <summary>Attribute locations, bound before linking so the renderer never has to query them and
    /// a mesh missing a channel simply leaves that slot disabled.</summary>
    internal const uint AttrPosition = 0, AttrNormal = 1, AttrUv = 2, AttrColor = 3, AttrTangent = 4;

    /// <summary>The names those locations are bound to, which a caller writing a whole program has to
    /// spell the same way. Ordered to match the constants above.</summary>
    internal static readonly (uint Location, string Name)[] Attributes =
    [
        (AttrPosition, "aPos"),
        (AttrNormal, "aNormal"),
        (AttrUv, "aUv"),
        (AttrColor, "aColor"),
        (AttrTangent, "aTangent"),
    ];

    /// <summary>The function a <see cref="ShaderKind.Surface"/> fragment snippet must define.</summary>
    internal const string SurfaceSignature = "void surface(inout Surface s)";

    /// <summary>The function a <see cref="ShaderKind.Surface"/> vertex snippet must define.</summary>
    internal const string VertexSignature = "void vertex(inout Vertex v)";

    /// <summary>The version line, plus the precision qualifier ES requires and desktop rejects.</summary>
    internal static string Header(GlslDialect dialect) => dialect == GlslDialect.GlEs300
        ? "#version 300 es\nprecision highp float;\n"
        : "#version 330 core\n";

    /// <summary>The engine's vertex stage, with no caller hook.</summary>
    internal static string Vertex(GlslDialect dialect) => VertexWith(dialect, null);

    /// <summary>The engine's fragment stage, with no caller hook.</summary>
    internal static string Fragment(GlslDialect dialect) => FragmentWith(dialect, null);

    /// <summary>A caller's whole stage, given nothing but the line it cannot write itself.</summary>
    internal static string Whole(GlslDialect dialect, string source) => Header(dialect) + source + "\n";

    /// <summary>
    /// The engine's vertex stage with <paramref name="hook"/> spliced in, or a hook that does nothing
    /// when there is none.
    /// </summary>
    internal static string VertexWith(GlslDialect dialect, string? hook) =>
        Header(dialect) + VertexPrelude
        + (string.IsNullOrWhiteSpace(hook) ? VertexSignature + " {}\n" : hook + "\n")
        + VertexMain;

    /// <summary>
    /// The engine's fragment stage with <paramref name="hook"/> spliced in.
    ///
    /// <para>The hook goes AFTER the engine's uniforms and helpers and BEFORE <c>main</c>, which is
    /// what lets a caller's function read <c>uCamPos</c> or call <c>environment()</c> without
    /// declaring anything, and declare uniforms of its own in the same breath.</para>
    /// </summary>
    internal static string FragmentWith(GlslDialect dialect, string? hook) =>
        Header(dialect) + Prelude
        + (string.IsNullOrWhiteSpace(hook) ? SurfaceSignature + " {}\n" : hook + "\n")
        + FragmentMain;

    /// <summary>
    /// The fragment prelude with the light array size filled in from <see cref="Light.Max"/>.
    ///
    /// <para>Substituted rather than written into the GLSL by hand, because a shader declaring eight
    /// while the renderer pushed nine would be a defect nothing catches: GL ignores a write past the
    /// end of a uniform array, so the ninth light would simply never arrive and the scene would be
    /// dim for no visible reason. One constant, in Core, next to the type that counts them.</para>
    /// </summary>
    private static readonly string Prelude = FragmentPrelude.Replace(
        "MAX_LIGHTS", Light.Max.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private const string VertexPrelude = """
        in vec3 aPos;
        in vec3 aNormal;
        in vec2 aUv;
        in vec4 aColor;
        in vec4 aTangent;

        uniform mat4 uMvp;
        uniform mat4 uModel;
        uniform mat4 uNormalMatrix;

        out vec3 vNormal;
        out vec3 vWorld;
        out vec3 vObject;
        out vec2 vUv;
        out vec4 vColor;
        out vec4 vTangent;

        // What a vertex hook may change, in OBJECT space — before the model matrix, because that is
        // the space a displacement is authored in: a wave along a flag's length is a fact about the
        // flag, not about where the flag has been put.
        struct Vertex {
            vec3 position;
            vec3 normal;
            vec2 uv;
            vec4 color;
        };

        """;

    private const string VertexMain = """

        void main() {
            Vertex v;
            v.position = aPos;
            v.normal = aNormal;
            v.uv = aUv;
            v.color = aColor;
            vertex(v);

            vec4 world = uModel * vec4(v.position, 1.0);
            vWorld = world.xyz;
            // Object space as well as world space, because a procedural pattern belongs to the thing
            // it is on: grain, brushing, wear and printed markings all stay put when the object
            // moves, and anything keyed to world space would swim across the surface as it turned.
            vObject = v.position;
            // The INVERSE TRANSPOSE, not the model matrix. They agree only while the transform is a
            // rigid motion; the moment a node carries a non-uniform scale — which an instanced,
            // stretched copy routinely does — using the model matrix tilts every normal and
            // the lighting slides across the surface as it rotates.
            vNormal = mat3(uNormalMatrix) * v.normal;
            // The handedness in w survives the transform untouched: it is a sign, not a direction.
            vTangent = vec4(mat3(uNormalMatrix) * aTangent.xyz, aTangent.w);
            vUv = v.uv;
            vColor = v.color;
            gl_Position = uMvp * vec4(v.position, 1.0);
        }
        """;

    private const string FragmentPrelude = """
        in vec3 vNormal;
        in vec3 vWorld;
        in vec3 vObject;
        in vec2 vUv;
        in vec4 vColor;
        in vec4 vTangent;

        uniform vec4  uBaseColor;
        uniform float uMetallic;
        uniform float uRoughness;
        uniform vec3  uEmissive;
        uniform vec3  uCamPos;

        // THE LIGHTS, as three parallel arrays of MAX_LIGHTS. The loop below runs to uLightCount and
        // not to the array size, so a scene with two lights does two lights' work and the rest of the
        // array costs nothing but the uniform slots it reserves.
        //
        //   uLightVector — the direction a directional light TRAVELS, or where a point light IS
        //   uLightColor  — colour and intensity together
        //   uLightRange  — how far a point light reaches; 0 marks the light as directional
        uniform vec3  uLightVector[MAX_LIGHTS];
        uniform vec3  uLightColor[MAX_LIGHTS];
        uniform float uLightRange[MAX_LIGHTS];
        uniform int   uLightCount;
        uniform vec3  uSkyColor;
        uniform vec3  uHorizonColor;
        uniform vec3  uGroundColor;
        uniform vec3  uUpAxis;
        uniform sampler2D uTex;
        uniform sampler2D uNormalTex;
        uniform int   uHasTex;
        uniform int   uHasNormalMap;
        uniform int   uHasVertexColor;
        uniform int   uAlphaMode;      // 0 opaque, 1 mask, 2 blend
        uniform float uAlphaCutoff;
        uniform int   uShowNormals;
        uniform int   uUnlit;
        uniform int   uHighlightBackfaces;
        uniform vec3  uBackfaceColor;
        uniform int   uSectionActive;
        uniform vec4  uSectionPlane;
        uniform vec3  uSectionColor;

        out vec4 fragColor;

        const float PI = 3.14159265359;

        // The environment, as a three-colour gradient by direction. Squared towards the poles rather
        // than linear, because a linear ramp leaves the horizon colour in a narrow band and reads as
        // two flat halves with a seam.
        vec3 environment(vec3 dir) {
            float h = clamp(dot(dir, uUpAxis), -1.0, 1.0);
            return h >= 0.0 ? mix(uHorizonColor, uSkyColor, h * h)
                            : mix(uHorizonColor, uGroundColor, h * h);
        }

        // THE MATERIAL AS THE ENGINE RESOLVED IT, handed to a surface hook to change. Everything here
        // is already the finished article — the base colour has its texture and vertex colour in it,
        // the normal has its normal map in it — so a hook that only wants to tint is one line and does
        // not have to reimplement the sampling to get there.
        struct Surface {
            vec3  world;        // world-space position
            vec3  object;       // position in the model's own space, before its transform
            vec3  normal;       // world space, mapped, NOT yet flipped towards the viewer
            vec3  view;         // unit vector towards the camera
            vec2  uv;
            vec4  vertexColor;
            vec4  baseColor;    // what gets lit
            float metallic;
            float roughness;
            vec3  emissive;
        };

        """;

    private const string FragmentMain = """

        void main() {
            // The cut first, before anything is computed for a fragment about to vanish.
            if (uSectionActive == 1 && dot(vWorld, uSectionPlane.xyz) + uSectionPlane.w > 0.0) discard;

            Surface s;
            s.world = vWorld;
            s.object = vObject;
            s.uv = vUv;
            s.vertexColor = vColor;
            s.view = normalize(uCamPos - vWorld);
            s.metallic = uMetallic;
            s.roughness = uRoughness;
            s.emissive = uEmissive;

            s.baseColor = uBaseColor;
            if (uHasTex == 1) s.baseColor *= texture(uTex, vUv);
            if (uHasVertexColor == 1) s.baseColor *= vColor;

            s.normal = normalize(vNormal);

            // Normal mapping BEFORE the two-sided flip, because the flip is a display correction and
            // the map is surface detail: applying the map to an already-flipped normal would put the
            // detail on backwards wherever a facet happened to be inverted.
            if (uHasNormalMap == 1) {
                vec3 T = normalize(vTangent.xyz);
                T = normalize(T - s.normal * dot(s.normal, T));
                // The handedness matters: a symmetric model usually maps both halves to the same
                // texture region, so one half is mirrored. Without the sign its surface detail comes
                // out punched in rather than raised.
                vec3 B = cross(s.normal, T) * vTangent.w;
                vec3 sampled = texture(uNormalTex, vUv).xyz * 2.0 - 1.0;
                s.normal = normalize(mat3(T, B, s.normal) * sampled);
            }

            // The hook, and everything above it exists to make this line worth writing: whatever the
            // caller leaves in `s` is what the rest of this shader lights. The engine's own materials
            // compile with a version of this function that does nothing at all.
            surface(s);

            if (uAlphaMode == 1 && s.baseColor.a < uAlphaCutoff) discard;

            // Scenery — a grid, a work area, an axis marker — has no meaningful normal, so lighting
            // it makes lines dim on one side of the model and bright on the other. Straight out, no
            // tone mapping: an unlit colour is a colour, not a luminance to be compressed.
            if (uUnlit == 1) {
                fragColor = s.baseColor;
                return;
            }

            // Read BEFORE the normal is flipped, because the flip is exactly what hides this: a
            // facet wound the wrong way would otherwise shade like its neighbours. gl_FrontFacing is
            // the winding as rasterised, which is the fact a user needs in order to fix their mesh.
            if (uHighlightBackfaces == 1 && !gl_FrontFacing) {
                fragColor = vec4(uBackfaceColor, 1.0);
                return;
            }

            // Looking through a cut means looking at the INSIDE of the far wall. Painting it flat
            // makes the section read as a surface; leaving it shaded lights the interior as though
            // it were outdoors, which makes a hollow model look like a rendering fault.
            if (uSectionActive == 1 && !gl_FrontFacing) {
                fragColor = vec4(uSectionColor, 1.0);
                return;
            }

            vec3 N = normalize(s.normal);
            vec3 V = s.view;

            // Two-sided shading, and it is not optional for this engine's first use case: real STL
            // files routinely contain inverted facets that no other tool objects to. Flipping the
            // normal towards the viewer means such a facet shades like its neighbours instead of
            // appearing as a black hole in an otherwise valid part.
            if (dot(N, V) < 0.0) N = -N;

            if (uShowNormals == 1) {
                fragColor = vec4(N * 0.5 + 0.5, 1.0);
                return;
            }

            vec3 albedo = s.baseColor.rgb;
            float NdotV = max(dot(N, V), 0.0001);

            float rough = clamp(s.roughness, 0.05, 1.0);
            float a  = rough * rough;
            float a2 = a * a;
            float k  = (rough + 1.0) * (rough + 1.0) / 8.0;
            vec3  F0 = mix(vec3(0.04), albedo, s.metallic);

            // Every light, summed. The terms that depend only on the surface and the viewer are
            // computed once above; everything inside depends on where this particular light is.
            vec3 direct = vec3(0.0);
            for (int i = 0; i < uLightCount; i++) {
                vec3 L;
                float attenuation = 1.0;

                if (uLightRange[i] <= 0.0) {
                    L = normalize(-uLightVector[i]);
                } else {
                    vec3 offset = uLightVector[i] - s.world;
                    float distance = max(length(offset), 0.0001);
                    L = offset / distance;

                    // Inverse square, WINDOWED to reach exactly zero at the range. Pure inverse
                    // square never gets there, so every lamp would touch every fragment in the scene
                    // forever — the same cost as a light you can see, and a faint wash nobody asked
                    // for. The squared window is the usual one: it fades smoothly rather than
                    // cutting off at a visible edge.
                    float ratio = clamp(distance / uLightRange[i], 0.0, 1.0);
                    float window = 1.0 - ratio * ratio * ratio * ratio;
                    attenuation = window * window / (distance * distance);
                }

                float NdotL = max(dot(N, L), 0.0);
                if (NdotL <= 0.0 || attenuation <= 0.0) continue;

                vec3 H = normalize(V + L);
                float NdotH = max(dot(N, H), 0.0);
                float VdotH = max(dot(V, H), 0.0);

                float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
                float D = a2 / max(PI * d * d, 0.0001);
                float G = (NdotV / (NdotV * (1.0 - k) + k)) * (NdotL / (NdotL * (1.0 - k) + k));
                vec3  F = F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);

                vec3 spec = (D * G * F) / max(4.0 * NdotV * NdotL, 0.0001);
                vec3 kD   = (vec3(1.0) - F) * (1.0 - s.metallic);

                direct += (kD * albedo / PI + spec) * NdotL * uLightColor[i] * attenuation;
            }

            // IMAGE-BASED LIGHTING, APPROXIMATED. A metal has no diffuse colour at all — everything
            // visible on chrome is a reflection — so with nothing to reflect it renders black and
            // looks broken. Two lookups stand in for a prefiltered environment map: one along the
            // normal for the diffuse half, one along the reflection for the specular half, blended
            // towards the diffuse one as roughness rises because a rough surface reflects a blurrier
            // and therefore flatter picture of its surroundings.
            vec3 irradiance = environment(N);
            vec3 reflection = mix(environment(reflect(-V, N)), irradiance, rough);

            // Fresnel with roughness folded in: the grazing-angle brightening has to fall off on a
            // rough surface, or every matte object gets a hard bright rim.
            vec3 Fr = F0 + (max(vec3(1.0 - rough), F0) - F0) * pow(1.0 - NdotV, 5.0);

            vec3 ambient = irradiance * albedo * (1.0 - s.metallic) * (vec3(1.0) - Fr)
                         + reflection * Fr;

            vec3 colour = direct + ambient + s.emissive;
            colour = colour / (colour + vec3(1.0));      // Reinhard
            colour = pow(colour, vec3(1.0 / 2.2));       // to sRGB

            fragColor = vec4(colour, uAlphaMode == 2 ? s.baseColor.a : 1.0);
        }
        """;
}
