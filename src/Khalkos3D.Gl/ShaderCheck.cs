using System.Text;

namespace Khalkos3D.Gl;

/// <summary>
/// Reads a caller's GLSL before the driver does, and objects to the things one driver accepts and
/// another does not.
///
/// <para><b>Public, and usable with no GL context at all.</b> These are text rules, so an application
/// can run them in its own build or test suite — on a machine with no GPU, in a headless CI job — and
/// find out that a shader will not run on a phone without owning a phone. <see cref="GlRenderer"/>
/// runs them itself before it compiles anything, so nobody is obliged to.</para>
///
/// <para><b>The driver in front of you is the wrong judge of portability.</b> A desktop NVIDIA
/// compiler takes <c>texture2D</c>, <c>varying</c> and a stray <c>#version</c> in its stride under
/// its compatibility rules; OpenGL ES 3.0 and WebGL2 reject all three. Source that compiles here and
/// fails on a phone is the exact failure this engine exists to prevent, and it is cheap to catch:
/// these are text, not semantics.</para>
///
/// <para><b>Two severities, and the split is the point.</b> An ERROR is source that cannot be right
/// on every target — it is refused here, so the failure happens on the developer's machine at the
/// moment they wrote it rather than in somebody's browser. A NOTE is source that is legal on some
/// targets and absent on others; it is compiled anyway, because the caller may well know their
/// deployment better than this list does, and the note is there when the report from the phone
/// arrives.</para>
///
/// <para>Comments are stripped before anything is matched. A rule that fires on the word
/// <c>gl_FragColor</c> inside <c>// do not use gl_FragColor</c> teaches people to distrust the
/// tool.</para>
/// </summary>
public static class ShaderCheck
{
    /// <summary>Constructs that are not legal on every target. Refused rather than reported.</summary>
    private static readonly (string Token, string Why)[] Fatal =
    [
        ("#version", "the engine writes the version directive, chosen from what the driver reports; remove it"),
        ("#include", "GLSL has no #include on any of these targets; compose the source in C# instead"),
        ("gl_FragColor", "removed in both GLSL 330 core and ES 300; declare an out and write to that"),
        ("gl_FragData", "removed in both GLSL 330 core and ES 300; declare an out and write to that"),
        ("texture2D", "GLSL 1.x spelling, removed in both dialects; call texture() instead"),
        ("texture2DLod", "GLSL 1.x spelling, removed in both dialects; call textureLod() instead"),
        ("textureCube", "GLSL 1.x spelling, removed in both dialects; call texture() instead"),
        ("varying", "removed in both dialects; use in/out"),
        ("attribute", "removed in both dialects; use in"),
    ];

    /// <summary>Constructs that exist on desktop GL and not on ES 3.0 or WebGL2. Compiled, and
    /// reported.</summary>
    private static readonly (string Token, string Why)[] Suspect =
    [
        ("double", "double precision is desktop-only; it does not exist on ES 3.0 or WebGL2"),
        ("dvec2", "double precision is desktop-only"),
        ("dvec3", "double precision is desktop-only"),
        ("dvec4", "double precision is desktop-only"),
        ("dmat4", "double precision is desktop-only"),
        ("gl_ClipDistance", "desktop-only; ES 3.0 and WebGL2 have no clip distances"),
        ("gl_SampleID", "needs GL 4.0 or ES 3.1; absent on WebGL2"),
        ("imageLoad", "image load/store needs GL 4.2 or ES 3.1; absent on WebGL2"),
        ("imageStore", "image load/store needs GL 4.2 or ES 3.1; absent on WebGL2"),
        ("textureQueryLod", "needs GL 4.0; absent on ES 3.0 and WebGL2"),
        ("#extension", "extensions are per-driver; what this enables here may not exist elsewhere"),
    ];

    /// <summary>
    /// Inspect a shader's sources without compiling them.
    ///
    /// <para>A report that is not <see cref="ShaderReport.Ok"/> names source that cannot be right on
    /// every target, and <see cref="GlRenderer"/> will refuse to build it. One that is Ok may still
    /// carry notes, which are constructs that exist on some of these targets and not others.</para>
    /// </summary>
    public static ShaderReport Inspect(Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        List<string>? notes = null;

        foreach (var (stage, source) in Stages(shader))
        {
            if (source is null) continue;
            var code = WithoutComments(source);

            foreach (var (token, why) in Fatal)
                if (ContainsWord(code, token))
                    return new ShaderReport(false, $"{stage}: {token} — {why}", notes ?? []);

            foreach (var (token, why) in Suspect)
                if (ContainsWord(code, token))
                    (notes ??= []).Add($"{stage}: {token} — {why}");
        }

        // A surface shader that defines no surface function links with the engine's do-nothing one
        // and draws the material unchanged — which looks like the shader was ignored, and is exactly
        // the kind of silence that costs an afternoon. Say it instead.
        if (shader.Kind == ShaderKind.Surface)
        {
            if (!WithoutComments(shader.Fragment).Contains("void surface(", StringComparison.Ordinal))
                return new ShaderReport(false,
                    $"fragment: a surface shader must define {ShaderSource.SurfaceSignature}", notes ?? []);

            if (shader.Vertex is { } vertex &&
                !WithoutComments(vertex).Contains("void vertex(", StringComparison.Ordinal))
                return new ShaderReport(false,
                    $"vertex: a vertex hook must define {ShaderSource.VertexSignature}", notes ?? []);
        }

        return notes is null ? ShaderReport.Success : new ShaderReport(true, null, notes);
    }

    private static IEnumerable<(string Stage, string? Source)> Stages(Shader shader)
    {
        yield return ("vertex", shader.Vertex);
        yield return ("fragment", shader.Fragment);
    }

    /// <summary>
    /// The source with its comments blanked out. GLSL has no string literals, so this needs to know
    /// about nothing but the two comment forms.
    /// </summary>
    private static string WithoutComments(string source)
    {
        if (!source.Contains('/')) return source;

        var kept = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                if (i < source.Length) kept.Append('\n');
                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                // Newlines are kept so a line count taken from this text still matches the original.
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n') kept.Append('\n');
                    i++;
                }
                i++;
                continue;
            }

            kept.Append(source[i]);
        }

        return kept.ToString();
    }

    /// <summary>
    /// Whether the token appears as a whole word.
    ///
    /// <para>Boundaries matter more than they look: <c>double</c> is a fatal word and <c>doubled</c>
    /// is a perfectly good variable name, and a lint that cannot tell them apart gets switched off.
    /// A preprocessor token such as <c>#version</c> starts on a boundary of its own, which the same
    /// check handles because <c>#</c> is not a word character.</para>
    /// </summary>
    private static bool ContainsWord(string text, string token)
    {
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(token, from, StringComparison.Ordinal);
            if (at < 0) return false;

            var beforeOk = at == 0 || !IsWordChar(text[at - 1]);
            var after = at + token.Length;
            var afterOk = after >= text.Length || !IsWordChar(text[after]);
            if (beforeOk && afterOk) return true;

            from = at + 1;
        }

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '#';
    }
}
