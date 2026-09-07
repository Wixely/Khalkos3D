using System.Numerics;
using Khalkos3D.Gl;
using Xunit;

namespace Khalkos3D.Tests;

/// <summary>
/// The half of caller-supplied shaders that needs no GPU: what a <see cref="Shader"/> is, what a
/// <see cref="ShaderValue"/> carries, and which GLSL is refused before a driver ever sees it.
///
/// <para><b>This suite is the portability guarantee, and it runs on the machines that cannot render
/// anything.</b> The rendering tests prove a shader works on the driver in front of them, which is
/// exactly the wrong evidence for "it runs everywhere" — a desktop compiler accepts <c>texture2D</c>
/// and <c>varying</c> under its compatibility rules and OpenGL ES 3.0 does not. These rules are text,
/// so they hold on a CI runner with no GL at all.</para>
/// </summary>
public class ShaderTests
{
    private const string Surface = "void surface(inout Surface s) { s.baseColor.rgb = vec3(1.0, 0.0, 0.0); }";

    // ---- what a shader is -------------------------------------------------------------------

    [Fact]
    public void A_surface_shader_carries_its_fragment_source_and_no_vertex_stage()
    {
        var shader = Shader.Surface(Surface, name: "red");

        Assert.Equal(ShaderKind.Surface, shader.Kind);
        Assert.Equal(Surface, shader.Fragment);
        Assert.Null(shader.Vertex);
        Assert.Equal("red", shader.Name);
    }

    [Fact]
    public void A_whole_program_carries_both_stages()
    {
        var shader = Shader.Program("void main() { gl_Position = vec4(aPos, 1.0); }",
                                    "out vec4 c; void main() { c = vec4(1.0); }");

        Assert.Equal(ShaderKind.Program, shader.Kind);
        Assert.NotNull(shader.Vertex);
        Assert.NotNull(shader.Fragment);
    }

    [Fact]
    public void Empty_source_is_refused_at_the_call_that_made_it()
    {
        Assert.Throws<ArgumentException>(() => Shader.Surface("   "));
        Assert.Throws<ArgumentException>(() => Shader.Program("", "out vec4 c; void main() { c = vec4(1.0); }"));
    }

    [Fact]
    public void Two_equivalent_shaders_are_different_objects()
    {
        // Reference identity is the cache key, and this is what the documentation promises: a caller
        // who rebuilds an identical shader every frame gets a compile every frame, so the type must
        // not quietly behave like a record.
        Assert.NotEqual(Shader.Surface(Surface), Shader.Surface(Surface));
    }

    // ---- values -----------------------------------------------------------------------------

    [Theory]
    [InlineData(ShaderValueKind.Float)]
    [InlineData(ShaderValueKind.Int)]
    [InlineData(ShaderValueKind.Vector2)]
    [InlineData(ShaderValueKind.Vector3)]
    [InlineData(ShaderValueKind.Vector4)]
    [InlineData(ShaderValueKind.Matrix)]
    public void Every_kind_survives_the_round_trip(ShaderValueKind kind)
    {
        ShaderValue value = kind switch
        {
            ShaderValueKind.Float => 2.5f,
            ShaderValueKind.Int => 7,
            ShaderValueKind.Vector2 => new Vector2(1f, 2f),
            ShaderValueKind.Vector3 => new Vector3(1f, 2f, 3f),
            ShaderValueKind.Vector4 => new Vector4(1f, 2f, 3f, 4f),
            _ => Matrix4x4.CreateScale(3f),
        };

        Assert.Equal(kind, value.Kind);
        switch (kind)
        {
            case ShaderValueKind.Float: Assert.Equal(2.5f, value.Scalar); break;
            case ShaderValueKind.Int: Assert.Equal(7, (int)value.Scalar); break;
            case ShaderValueKind.Vector2: Assert.Equal(new Vector4(1f, 2f, 0f, 0f), value.Vector); break;
            case ShaderValueKind.Vector3: Assert.Equal(new Vector4(1f, 2f, 3f, 0f), value.Vector); break;
            case ShaderValueKind.Vector4: Assert.Equal(new Vector4(1f, 2f, 3f, 4f), value.Vector); break;
            default: Assert.Equal(Matrix4x4.CreateScale(3f), value.Matrix); break;
        }
    }

    [Fact]
    public void A_bool_is_the_int_glsl_actually_wants()
    {
        ShaderValue on = true;
        ShaderValue off = false;

        Assert.Equal(ShaderValueKind.Int, on.Kind);
        Assert.Equal(1, (int)on.Scalar);
        Assert.Equal(0, (int)off.Scalar);
    }

    [Fact]
    public void The_kind_is_part_of_the_value()
    {
        // 1.0f and 1 set the same bits and need different glUniform calls, so a comparison that
        // ignored the kind would call them equal and hide exactly the mix-up that matters.
        Assert.NotEqual(ShaderValue.From(1f), ShaderValue.From(1));
        Assert.Equal(ShaderValue.From(1f), ShaderValue.From(1f));
    }

    [Fact]
    public void A_material_with_no_shader_has_an_empty_value_set_rather_than_null()
    {
        Assert.Null(Material.Default.Shader);
        Assert.Empty(Material.Default.ShaderValues);
    }

    // ---- what will not run everywhere -------------------------------------------------------

    [Theory]
    [InlineData("#version 330 core\nvoid surface(inout Surface s) {}")]
    [InlineData("void surface(inout Surface s) { gl_FragColor = vec4(1.0); }")]
    [InlineData("void surface(inout Surface s) { s.baseColor = texture2D(uTex, s.uv); }")]
    [InlineData("varying vec3 vThing;\nvoid surface(inout Surface s) {}")]
    [InlineData("attribute vec3 aThing;\nvoid surface(inout Surface s) {}")]
    [InlineData("#include \"common.glsl\"\nvoid surface(inout Surface s) {}")]
    public void Source_that_cannot_compile_on_every_target_is_refused(string source)
    {
        var report = ShaderCheck.Inspect(Shader.Surface(source));

        Assert.False(report.Ok);
        Assert.NotNull(report.Error);
        Assert.NotNull(report.Summary);
    }

    [Fact]
    public void A_surface_shader_that_defines_no_surface_function_is_refused()
    {
        // It would otherwise link against the engine's do-nothing hook and draw the material
        // unchanged, which looks exactly like the shader being ignored.
        var report = ShaderCheck.Inspect(Shader.Surface("vec3 tint() { return vec3(1.0); }"));

        Assert.False(report.Ok);
        Assert.Contains("void surface(inout Surface s)", report.Error);
    }

    [Fact]
    public void A_vertex_hook_that_defines_no_vertex_function_is_refused()
    {
        var report = ShaderCheck.Inspect(
            Shader.Surface(Surface, vertex: "float wobble(float t) { return sin(t); }"));

        Assert.False(report.Ok);
        Assert.Contains("void vertex(inout Vertex v)", report.Error);
    }

    [Fact]
    public void Desktop_only_constructs_are_reported_and_still_compiled()
    {
        var report = ShaderCheck.Inspect(Shader.Surface(
            "void surface(inout Surface s) { double d = 1.0LF; s.roughness = float(d); }"));

        Assert.True(report.Ok);
        Assert.Contains(report.Notes, note => note.Contains("double", StringComparison.Ordinal));
        Assert.NotNull(report.Summary);
    }

    [Fact]
    public void A_rule_does_not_fire_on_the_word_inside_a_comment()
    {
        // A lint that cannot read a comment gets switched off, and then it protects nobody.
        var report = ShaderCheck.Inspect(Shader.Surface("""
            // Do not call texture2D here, and never write gl_FragColor.
            /* #version lines belong to the engine. */
            void surface(inout Surface s) { s.metallic = 1.0; }
            """));

        Assert.True(report.Ok);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void A_rule_does_not_fire_on_a_longer_word_that_contains_it()
    {
        var report = ShaderCheck.Inspect(Shader.Surface(
            "void surface(inout Surface s) { float doubled = s.roughness * 2.0; s.roughness = doubled; }"));

        Assert.True(report.Ok);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void Ordinary_source_passes_with_nothing_to_say()
    {
        var report = ShaderCheck.Inspect(Shader.Surface("""
            uniform float uTime;
            void surface(inout Surface s) {
                s.baseColor.rgb *= 0.5 + 0.5 * sin(uTime + s.world.y);
            }
            """, name: "pulse"));

        Assert.True(report.Ok);
        Assert.Null(report.Error);
        Assert.Null(report.Summary);
    }

    [Fact]
    public void A_whole_program_is_checked_in_both_stages()
    {
        var report = ShaderCheck.Inspect(Shader.Program(
            vertex: "varying vec3 vThing; void main() { gl_Position = vec4(aPos, 1.0); }",
            fragment: "out vec4 c; void main() { c = vec4(1.0); }"));

        Assert.False(report.Ok);
        Assert.StartsWith("vertex:", report.Error);
    }

    // ---- the splice, in both dialects --------------------------------------------------------

    [Theory]
    [InlineData(GlslDialect.Gl330Core, "#version 330 core")]
    [InlineData(GlslDialect.GlEs300, "#version 300 es")]
    public void Each_dialect_gets_its_own_first_line(GlslDialect dialect, string expected)
    {
        // The ES source cannot be compiled by the desktop driver these tests run on, so what it says
        // is checked as text. That is not a weaker test than compiling it: the line is the only thing
        // that differs between the two, which is the entire portability argument.
        Assert.StartsWith(expected, ShaderSource.Vertex(dialect), StringComparison.Ordinal);
        Assert.StartsWith(expected, ShaderSource.Fragment(dialect), StringComparison.Ordinal);
        Assert.Contains("precision highp float;", ShaderSource.Fragment(GlslDialect.GlEs300), StringComparison.Ordinal);
        Assert.DoesNotContain("precision", ShaderSource.Fragment(GlslDialect.Gl330Core), StringComparison.Ordinal);
    }

    [Fact]
    public void The_built_in_program_is_the_same_text_with_a_hook_that_does_nothing()
    {
        var built = ShaderSource.Fragment(GlslDialect.Gl330Core);

        Assert.Contains("void surface(inout Surface s) {}", built, StringComparison.Ordinal);
        Assert.Contains("void vertex(inout Vertex v) {}", ShaderSource.Vertex(GlslDialect.Gl330Core), StringComparison.Ordinal);
    }

    [Fact]
    public void A_hook_lands_after_the_uniforms_and_before_main()
    {
        // Ordering is the whole contract of the splice: a hook that landed after main could not be
        // called, and one that landed before the uniforms could not read them.
        const string hook = """
            uniform float uThing;
            void surface(inout Surface s) { s.metallic = uThing; }
            """;

        var source = ShaderSource.FragmentWith(GlslDialect.GlEs300, hook);

        var uniforms = source.IndexOf("uniform vec4  uBaseColor;", StringComparison.Ordinal);
        var spliced = source.IndexOf("uniform float uThing;", StringComparison.Ordinal);
        var main = source.IndexOf("void main()", StringComparison.Ordinal);

        Assert.True(uniforms < spliced, "the engine's uniforms come first");
        Assert.True(spliced < main, "and the hook before main, which calls it");
        Assert.Contains("surface(s);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_program_is_given_the_header_and_nothing_else()
    {
        const string source = """
            out vec4 c;
            void main() { c = vec4(1.0); }
            """;

        var built = ShaderSource.Whole(GlslDialect.Gl330Core, source);

        // The header, the source, and nothing of the engine's: no Surface struct to collide with
        // a name of theirs, no uniforms they did not ask for.
        Assert.StartsWith(ShaderSource.Header(GlslDialect.Gl330Core), built, StringComparison.Ordinal);
        Assert.Equal((ShaderSource.Header(GlslDialect.Gl330Core) + source).TrimEnd(), built.TrimEnd());
        Assert.DoesNotContain("struct Surface", built, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_attribute_has_a_bound_location_and_they_are_the_documented_ones()
    {
        // aTangent was the one missing from this list, and it worked only because the linker put it
        // where the VAO already expected it. A caller writing a whole program, declaring attributes
        // in their own order, is exactly who that would have broken.
        Assert.Equal(
            [(0u, "aPos"), (1u, "aNormal"), (2u, "aUv"), (3u, "aColor"), (4u, "aTangent")],
            ShaderSource.Attributes);
    }
}
