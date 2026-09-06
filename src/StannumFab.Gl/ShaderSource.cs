namespace StannumFab.Gl;

/// <summary>
/// The shader, as one body with two headers.
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
/// </summary>
internal static class ShaderSource
{
    /// <summary>Attribute locations, bound before linking so the renderer never has to query them and
    /// a mesh missing a channel simply leaves that slot disabled.</summary>
    internal const uint AttrPosition = 0, AttrNormal = 1, AttrUv = 2, AttrColor = 3, AttrTangent = 4;

    /// <summary>The version line, plus the precision qualifier ES requires and desktop rejects.</summary>
    internal static string Header(GlslDialect dialect) => dialect == GlslDialect.GlEs300
        ? "#version 300 es\nprecision highp float;\n"
        : "#version 330 core\n";

    internal static string Vertex(GlslDialect dialect) => Header(dialect) + """
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
        out vec2 vUv;
        out vec4 vColor;
        out vec4 vTangent;

        void main() {
            vec4 world = uModel * vec4(aPos, 1.0);
            vWorld = world.xyz;
            // The INVERSE TRANSPOSE, not the model matrix. They agree only while the transform is a
            // rigid motion; the moment a node carries a non-uniform scale — which a printer plate
            // holding a stretched part routinely does — using the model matrix tilts every normal and
            // the lighting slides across the surface as it rotates.
            vNormal = mat3(uNormalMatrix) * aNormal;
            // The handedness in w survives the transform untouched: it is a sign, not a direction.
            vTangent = vec4(mat3(uNormalMatrix) * aTangent.xyz, aTangent.w);
            vUv = aUv;
            vColor = aColor;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    internal static string Fragment(GlslDialect dialect) => Header(dialect) + """
        in vec3 vNormal;
        in vec3 vWorld;
        in vec2 vUv;
        in vec4 vColor;
        in vec4 vTangent;

        uniform vec4  uBaseColor;
        uniform float uMetallic;
        uniform float uRoughness;
        uniform vec3  uEmissive;
        uniform vec3  uCamPos;
        uniform vec3  uLightDir;
        uniform vec3  uLightColor;
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

        void main() {
            vec4 base = uBaseColor;
            if (uHasTex == 1) base *= texture(uTex, vUv);
            if (uHasVertexColor == 1) base *= vColor;

            if (uAlphaMode == 1 && base.a < uAlphaCutoff) discard;

            // Scenery — a grid, a build plate, an axis marker — has no meaningful normal, so lighting
            // it makes lines dim on one side of the model and bright on the other. Straight out, no
            // tone mapping: an unlit colour is a colour, not a luminance to be compressed.
            if (uUnlit == 1) {
                fragColor = base;
                return;
            }

            // Read BEFORE the normal is flipped, because the flip is exactly what hides this: a
            // facet wound the wrong way would otherwise shade like its neighbours. gl_FrontFacing is
            // the winding as rasterised, which is the fact a user needs in order to fix their mesh.
            if (uHighlightBackfaces == 1 && !gl_FrontFacing) {
                fragColor = vec4(uBackfaceColor, 1.0);
                return;
            }

            vec3 N = normalize(vNormal);
            vec3 V = normalize(uCamPos - vWorld);

            // Normal mapping BEFORE the two-sided flip, because the flip is a display correction and
            // the map is surface detail: applying the map to an already-flipped normal would put the
            // detail on backwards wherever a facet happened to be inverted.
            if (uHasNormalMap == 1) {
                vec3 T = normalize(vTangent.xyz);
                T = normalize(T - N * dot(N, T));
                // The handedness matters: a symmetric model usually maps both halves to the same
                // texture region, so one half is mirrored. Without the sign its surface detail comes
                // out punched in rather than raised.
                vec3 B = cross(N, T) * vTangent.w;
                vec3 sampled = texture(uNormalTex, vUv).xyz * 2.0 - 1.0;
                N = normalize(mat3(T, B, N) * sampled);
            }

            // Two-sided shading, and it is not optional for this engine's first use case: real STL
            // files routinely contain inverted facets, and a slicer prints them fine. Flipping the
            // normal towards the viewer means such a facet shades like its neighbours instead of
            // appearing as a black hole in an otherwise valid part.
            if (dot(N, V) < 0.0) N = -N;

            if (uShowNormals == 1) {
                fragColor = vec4(N * 0.5 + 0.5, 1.0);
                return;
            }

            vec3 albedo = base.rgb;
            vec3 L = normalize(-uLightDir);
            vec3 H = normalize(V + L);

            float NdotL = max(dot(N, L), 0.0);
            float NdotV = max(dot(N, V), 0.0001);
            float NdotH = max(dot(N, H), 0.0);
            float VdotH = max(dot(V, H), 0.0);

            float rough = clamp(uRoughness, 0.05, 1.0);
            float a  = rough * rough;
            float a2 = a * a;
            float d  = NdotH * NdotH * (a2 - 1.0) + 1.0;
            float D  = a2 / max(PI * d * d, 0.0001);

            float k = (rough + 1.0) * (rough + 1.0) / 8.0;
            float G = (NdotV / (NdotV * (1.0 - k) + k)) * (NdotL / (NdotL * (1.0 - k) + k));

            vec3 F0 = mix(vec3(0.04), albedo, uMetallic);
            vec3 F  = F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);

            vec3 spec   = (D * G * F) / max(4.0 * NdotV * NdotL, 0.0001);
            vec3 kD     = (vec3(1.0) - F) * (1.0 - uMetallic);
            vec3 direct = (kD * albedo / PI + spec) * NdotL * uLightColor;

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

            vec3 ambient = irradiance * albedo * (1.0 - uMetallic) * (vec3(1.0) - Fr)
                         + reflection * Fr;

            vec3 colour = direct + ambient + uEmissive;
            colour = colour / (colour + vec3(1.0));      // Reinhard
            colour = pow(colour, vec3(1.0 / 2.2));       // to sRGB

            fragColor = vec4(colour, uAlphaMode == 2 ? base.a : 1.0);
        }
        """;
}
