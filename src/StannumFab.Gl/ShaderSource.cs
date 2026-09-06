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
    internal const uint AttrPosition = 0, AttrNormal = 1, AttrUv = 2, AttrColor = 3;

    /// <summary>The version line, plus the precision qualifier ES requires and desktop rejects.</summary>
    internal static string Header(GlslDialect dialect) => dialect == GlslDialect.GlEs300
        ? "#version 300 es\nprecision highp float;\n"
        : "#version 330 core\n";

    internal static string Vertex(GlslDialect dialect) => Header(dialect) + """
        in vec3 aPos;
        in vec3 aNormal;
        in vec2 aUv;
        in vec4 aColor;

        uniform mat4 uMvp;
        uniform mat4 uModel;
        uniform mat4 uNormalMatrix;

        out vec3 vNormal;
        out vec3 vWorld;
        out vec2 vUv;
        out vec4 vColor;

        void main() {
            vec4 world = uModel * vec4(aPos, 1.0);
            vWorld = world.xyz;
            // The INVERSE TRANSPOSE, not the model matrix. They agree only while the transform is a
            // rigid motion; the moment a node carries a non-uniform scale — which a printer plate
            // holding a stretched part routinely does — using the model matrix tilts every normal and
            // the lighting slides across the surface as it rotates.
            vNormal = mat3(uNormalMatrix) * aNormal;
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

        uniform vec4  uBaseColor;
        uniform float uMetallic;
        uniform float uRoughness;
        uniform vec3  uEmissive;
        uniform vec3  uCamPos;
        uniform vec3  uLightDir;
        uniform vec3  uLightColor;
        uniform vec3  uAmbient;
        uniform sampler2D uTex;
        uniform int   uHasTex;
        uniform int   uHasVertexColor;
        uniform int   uAlphaMode;      // 0 opaque, 1 mask, 2 blend
        uniform float uAlphaCutoff;
        uniform int   uShowNormals;

        out vec4 fragColor;

        const float PI = 3.14159265359;

        void main() {
            vec3 N = normalize(vNormal);

            // Two-sided shading, and it is not optional for this engine's first use case: real STL
            // files routinely contain inverted facets, and a slicer prints them fine. Flipping the
            // normal towards the viewer means such a facet shades like its neighbours instead of
            // appearing as a black hole in an otherwise valid part.
            vec3 V = normalize(uCamPos - vWorld);
            if (dot(N, V) < 0.0) N = -N;

            if (uShowNormals == 1) {
                fragColor = vec4(N * 0.5 + 0.5, 1.0);
                return;
            }

            vec4 base = uBaseColor;
            if (uHasTex == 1) base *= texture(uTex, vUv);
            if (uHasVertexColor == 1) base *= vColor;

            if (uAlphaMode == 1 && base.a < uAlphaCutoff) discard;

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

            // Standing in for image-based lighting. Without it a metal has nothing to reflect and
            // renders black, which reads as a broken shader rather than as an unlit material — so the
            // term is scaled down for metals rather than removed, and is the first thing a real
            // environment map replaces.
            vec3 ambient = albedo * uAmbient * (1.0 - uMetallic * 0.6);

            vec3 colour = direct + ambient + uEmissive;
            colour = colour / (colour + vec3(1.0));      // Reinhard
            colour = pow(colour, vec3(1.0 / 2.2));       // to sRGB

            fragColor = vec4(colour, uAlphaMode == 2 ? base.a : 1.0);
        }
        """;
}
