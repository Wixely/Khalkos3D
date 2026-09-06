using System.Numerics;
using System.Runtime.InteropServices;

namespace Khalkos3D.Demo;

/// <summary>
/// A sphere and a box, generated in code.
///
/// <para><b>Here rather than in the engine, deliberately.</b> <c>Shapes</c> in Core makes grids and
/// axes because a viewer needs a floor to judge scale against, which is a rendering concern. Solids
/// are content, and an engine that starts shipping content acquires opinions about what a scene
/// should contain. This is a demo, so the content lives with the demo.</para>
///
/// <para>Both are built as unindexed triangle corners and handed to <see cref="MeshWelder"/>, which
/// is the same path an STL takes — so the demo exercises the welder rather than side-stepping it,
/// and the smoothing angle is what decides whether an edge reads as hard or round.</para>
///
/// <para><b>Both come out as closed manifolds</b>, which <see cref="MeshAnalysis"/> will confirm. A
/// sample for an engine that ships a watertightness check should not hand it geometry that fails —
/// and getting the sphere there took more care than it looks.</para>
/// </summary>
internal static class Solids
{
    /// <summary>
    /// A UV sphere, welded at 180 degrees so every edge is smooth: on a sphere a crease is an
    /// artefact of how it was tessellated rather than a feature of the shape.
    /// </summary>
    internal static Mesh Sphere(float radius, int segments = 48, int rings = 24, string? name = null)
    {
        // THE POINT OF THIS TABLE IS BIT-IDENTICAL WRAPAROUND. The vertex at longitude index
        // `segments` is the vertex at index 0, so it is looked up rather than recomputed:
        // MathF.Sin(0) is exactly 0 while MathF.Sin(MathF.Tau) is -8.7e-8, and the welder compares
        // positions exactly by default. Recomputing leaves the seam unwelded — geometrically
        // invisible, and a ring of boundary edges that stops the mesh being watertight.
        var sin = new float[segments];
        var cos = new float[segments];
        for (var i = 0; i < segments; i++)
        {
            var theta = MathF.Tau * i / segments;
            sin[i] = MathF.Sin(theta);
            cos[i] = MathF.Cos(theta);
        }

        var north = new Vector3(0f, radius, 0f);
        var south = new Vector3(0f, -radius, 0f);

        var corners = new List<Vector3>(segments * rings * 6);
        for (var ring = 0; ring < rings; ring++)
        {
            for (var segment = 0; segment < segments; segment++)
            {
                var a = At(segment, ring);
                var b = At(segment, ring + 1);
                var c = At(segment + 1, ring + 1);
                var d = At(segment + 1, ring);

                // A POLE ROW IS A FAN, NOT A STRIP. At the top a and d are both the north pole and
                // at the bottom b and c are both the south, so one triangle of each end quad has two
                // identical corners and no area. Emitting them anyway is how a UV sphere is usually
                // written and it looks perfectly fine — nothing with no area draws — but it leaves a
                // boundary edge per segment and a mesh that is not closed.
                if (ring < rings - 1) { corners.Add(a); corners.Add(b); corners.Add(c); }
                if (ring > 0) { corners.Add(a); corners.Add(c); corners.Add(d); }
            }
        }

        return MeshWelder.FromTriangleSoup(CollectionsMarshal.AsSpan(corners), out _,
                                           smoothingAngle: 180f, name: name);

        // The poles are constants rather than computed values. MathF.Sin(MathF.PI) is 8.7e-8, not 0,
        // so a computed south pole is a ring of distinct near-coincident points rather than one
        // vertex — the same unwelded-seam problem as the longitude wrap, at the other end.
        Vector3 At(int segment, int ring)
        {
            if (ring == 0) return north;
            if (ring == rings) return south;

            var phi = MathF.PI * ring / rings;
            var (sinPhi, cosPhi) = (MathF.Sin(phi), MathF.Cos(phi));
            var s = segment % segments;
            return new Vector3(radius * sinPhi * sin[s], radius * cosPhi, radius * sinPhi * cos[s]);
        }
    }

    /// <summary>
    /// An axis-aligned box. Welded at the default angle, so the ninety-degree edges stay hard while
    /// coincident corners still collapse — the behaviour that makes a box look like a box and not
    /// like a badly inflated ball.
    /// </summary>
    internal static Mesh Box(Vector3 size, string? name = null)
    {
        var h = size * 0.5f;

        // Each face as two tangents chosen so that tangent cross bitangent equals the outward
        // normal. That identity is what makes one winding below correct for all six faces, without
        // six separately reasoned-about vertex orders.
        ReadOnlySpan<(Vector3 T, Vector3 U)> faces =
        [
            (new(0, 0, -1), new(0, 1, 0)),   // +X
            (new(0, 0, 1), new(0, 1, 0)),    // -X
            (new(1, 0, 0), new(0, 0, -1)),   // +Y
            (new(1, 0, 0), new(0, 0, 1)),    // -Y
            (new(1, 0, 0), new(0, 1, 0)),    // +Z
            (new(-1, 0, 0), new(0, 1, 0)),   // -Z
        ];

        var corners = new List<Vector3>(6 * 6);
        foreach (var (t, u) in faces)
        {
            var normal = Vector3.Cross(t, u);
            var centre = normal * Extent(normal, h);
            var tangent = t * Extent(t, h);
            var bitangent = u * Extent(u, h);

            var a = centre - tangent - bitangent;
            var b = centre + tangent - bitangent;
            var c = centre + tangent + bitangent;
            var d = centre - tangent + bitangent;

            corners.Add(a); corners.Add(b); corners.Add(c);
            corners.Add(a); corners.Add(c); corners.Add(d);
        }

        return MeshWelder.FromTriangleSoup(CollectionsMarshal.AsSpan(corners), out _, name: name);

        // The half-extent along a unit axis vector, whichever axis and whichever sign it is.
        static float Extent(Vector3 axis, Vector3 half) =>
            MathF.Abs(axis.X) * half.X + MathF.Abs(axis.Y) * half.Y + MathF.Abs(axis.Z) * half.Z;
    }
}
