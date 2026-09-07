using System.Numerics;

namespace Khalkos3D;

/// <summary>What a <see cref="ShaderValue"/> holds, and therefore which <c>glUniform</c> call sets
/// it.</summary>
public enum ShaderValueKind
{
    /// <summary>A <c>float</c>.</summary>
    Float,
    /// <summary>An <c>int</c>. A <c>bool</c> uniform takes this too, as 0 or 1.</summary>
    Int,
    /// <summary>A <c>vec2</c>.</summary>
    Vector2,
    /// <summary>A <c>vec3</c>.</summary>
    Vector3,
    /// <summary>A <c>vec4</c>.</summary>
    Vector4,
    /// <summary>A <c>mat4</c>.</summary>
    Matrix,
}

/// <summary>
/// One value for one uniform a caller's <see cref="Shader"/> declares.
///
/// <para><b>Typed, rather than a boxed object or a float array.</b> A uniform set with the wrong call
/// is not an exception anywhere — GL just leaves the old value in place, and the result is a shader
/// that works on the machine where it was written and shows the wrong thing everywhere else. Carrying
/// the kind means the renderer picks the call from the value rather than from a guess.</para>
///
/// <para>The implicit conversions are the point of use: a dictionary initialiser reads as
/// <c>["uTime"] = 1.5f</c> rather than as a constructor call per entry.</para>
///
/// <para>Everything is stored in one <see cref="Matrix4x4"/> — a scalar in M11, a vector along the
/// first row. It wastes bytes on a float and it is deliberate: a discriminated layout would need
/// either an allocation per value or an unsafe overlay, and this is a handful of values per material
/// set once per frame, not a vertex buffer.</para>
/// </summary>
public readonly struct ShaderValue : IEquatable<ShaderValue>
{
    private readonly Matrix4x4 _payload;

    private ShaderValue(ShaderValueKind kind, Matrix4x4 payload)
    {
        Kind = kind;
        _payload = payload;
    }

    /// <summary>Which GLSL type this is for.</summary>
    public ShaderValueKind Kind { get; }

    /// <summary>A <c>float</c> uniform.</summary>
    public static ShaderValue From(float value) =>
        new(ShaderValueKind.Float, new Matrix4x4 { M11 = value });

    /// <summary>An <c>int</c> uniform.</summary>
    public static ShaderValue From(int value) =>
        new(ShaderValueKind.Int, new Matrix4x4 { M11 = value });

    /// <summary>A <c>bool</c> uniform, as GLSL sees it: an int that is 0 or 1.</summary>
    public static ShaderValue From(bool value) => From(value ? 1 : 0);

    /// <summary>A <c>vec2</c> uniform.</summary>
    public static ShaderValue From(Vector2 value) =>
        new(ShaderValueKind.Vector2, new Matrix4x4 { M11 = value.X, M12 = value.Y });

    /// <summary>A <c>vec3</c> uniform.</summary>
    public static ShaderValue From(Vector3 value) =>
        new(ShaderValueKind.Vector3, new Matrix4x4 { M11 = value.X, M12 = value.Y, M13 = value.Z });

    /// <summary>A <c>vec4</c> uniform.</summary>
    public static ShaderValue From(Vector4 value) =>
        new(ShaderValueKind.Vector4,
            new Matrix4x4 { M11 = value.X, M12 = value.Y, M13 = value.Z, M14 = value.W });

    /// <summary>A <c>mat4</c> uniform.</summary>
    public static ShaderValue From(Matrix4x4 value) => new(ShaderValueKind.Matrix, value);

    /// <summary>A <c>float</c> uniform.</summary>
    public static implicit operator ShaderValue(float value) => From(value);

    /// <summary>An <c>int</c> uniform.</summary>
    public static implicit operator ShaderValue(int value) => From(value);

    /// <summary>A <c>bool</c> uniform.</summary>
    public static implicit operator ShaderValue(bool value) => From(value);

    /// <summary>A <c>vec2</c> uniform.</summary>
    public static implicit operator ShaderValue(Vector2 value) => From(value);

    /// <summary>A <c>vec3</c> uniform.</summary>
    public static implicit operator ShaderValue(Vector3 value) => From(value);

    /// <summary>A <c>vec4</c> uniform.</summary>
    public static implicit operator ShaderValue(Vector4 value) => From(value);

    /// <summary>A <c>mat4</c> uniform.</summary>
    public static implicit operator ShaderValue(Matrix4x4 value) => From(value);

    /// <summary>The scalar, whatever the kind — the first component, which is what a
    /// <see cref="ShaderValueKind.Float"/> or <see cref="ShaderValueKind.Int"/> holds.</summary>
    public float Scalar => _payload.M11;

    /// <summary>The first four components, which is what every kind but
    /// <see cref="ShaderValueKind.Matrix"/> fits in.</summary>
    public Vector4 Vector => new(_payload.M11, _payload.M12, _payload.M13, _payload.M14);

    /// <summary>The whole matrix. Meaningful for <see cref="ShaderValueKind.Matrix"/>.</summary>
    public Matrix4x4 Matrix => _payload;

    /// <inheritdoc/>
    public bool Equals(ShaderValue other) => Kind == other.Kind && _payload.Equals(other._payload);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ShaderValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, _payload);

    /// <summary>Equality by kind and value.</summary>
    public static bool operator ==(ShaderValue left, ShaderValue right) => left.Equals(right);

    /// <summary>Inequality by kind and value.</summary>
    public static bool operator !=(ShaderValue left, ShaderValue right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        ShaderValueKind.Float => Scalar.ToString("0.###"),
        ShaderValueKind.Int => ((int)Scalar).ToString(),
        ShaderValueKind.Vector2 => $"vec2({Vector.X:0.###}, {Vector.Y:0.###})",
        ShaderValueKind.Vector3 => $"vec3({Vector.X:0.###}, {Vector.Y:0.###}, {Vector.Z:0.###})",
        ShaderValueKind.Vector4 => $"vec4({Vector.X:0.###}, {Vector.Y:0.###}, {Vector.Z:0.###}, {Vector.W:0.###})",
        _ => "mat4",
    };
}
