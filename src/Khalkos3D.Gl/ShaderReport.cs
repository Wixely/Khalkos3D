namespace Khalkos3D.Gl;

/// <summary>
/// What happened when a caller's <see cref="Shader"/> was built.
///
/// <para><b>Returned rather than thrown, for the same reason <see cref="GlRenderer.Create"/> returns
/// null rather than throwing.</b> A shader that will not compile is a normal event in an application
/// that lets people write shaders, and the application around it has to keep running and show the
/// message. A viewer that dies because a material failed has turned an editing mistake into a crash
/// report.</para>
///
/// <para><see cref="Notes"/> is the other half, and it is not decoration. The engine compiles on
/// whatever driver is in front of it, so source that only works on THIS one compiles clean and fails
/// on a phone months later. The notes name the constructs that are known to differ before that
/// happens.</para>
/// </summary>
/// <param name="Ok">Whether the program was built. False means this material draws with the built-in
/// program instead.</param>
/// <param name="Error">Why it was not built: the driver's compile or link log, or the engine's own
/// objection to source that could not have compiled everywhere. Null when <paramref name="Ok"/>.</param>
/// <param name="Notes">Constructs that compiled here and may not compile on another target. Never
/// null, usually empty, and worth showing.</param>
public sealed record ShaderReport(bool Ok, string? Error, IReadOnlyList<string> Notes)
{
    /// <summary>A shader that built with nothing to say about it.</summary>
    public static ShaderReport Success { get; } = new(true, null, []);

    /// <summary>One line fit for a log or a status bar, or null when there is nothing to report.</summary>
    public string? Summary => (Ok, Notes.Count) switch
    {
        (false, _) => $"shader failed: {Error}",
        (true, 0) => null,
        (true, 1) => $"shader: {Notes[0]}",
        (true, var n) => $"shader: {n} portability notes, first is {Notes[0]}",
    };
}
