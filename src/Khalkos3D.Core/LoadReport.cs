namespace Khalkos3D;

/// <summary>How much a note matters.</summary>
public enum LoadSeverity
{
    /// <summary>Worth knowing, nothing lost. Welding statistics, units applied, a default
    /// substituted.</summary>
    Info,

    /// <summary>The file broke a rule and the loader recovered. A degenerate triangle, a normal that
    /// did not normalise, an index repaired. The model is shown; something in it was wrong.</summary>
    Repaired,

    /// <summary>The file contained a feature this loader does not implement, so what is on screen is
    /// INCOMPLETE. Animation tracks, a material extension, a second UV set.</summary>
    Unsupported,
}

/// <summary>One thing the loader wants the caller to know.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Feature">The thing, named the way the file format names it — <c>animations</c>,
/// <c>KHR_draco_mesh_compression</c>, <c>facet normal</c> — so it can be searched for.</param>
/// <param name="Detail">A sentence a user could act on.</param>
public readonly record struct LoadNote(LoadSeverity Severity, string Feature, string Detail)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Severity}: {Feature} — {Detail}";
}

/// <summary>
/// What a loader could not honour, carried out with the result rather than logged and lost.
///
/// <para><b>This is the engine's central honesty rule, given a type.</b> A viewer that silently
/// drops an animation track and shows a T-posed character has told its user their file is broken
/// when it is not. One that skips an unreadable material and renders the mesh grey has told them
/// their colours are gone. Both are worse than saying so, and neither leaves any trace to debug —
/// the file opens, something is wrong, and nothing anywhere explains it.</para>
///
/// <para>So every loader records what it stepped over, and <see cref="Scene.Report"/> carries it to
/// the caller. Showing it is the caller's choice; being able to is not optional. The rule that
/// follows from it: <b>refuse loudly rather than render something wrong</b> — where a feature
/// changes how the BYTES must be read (mesh compression, a required extension), loaders throw
/// instead of noting, because a half-decoded mesh is not a degraded result but a wrong one.</para>
/// </summary>
public sealed class LoadReport
{
    /// <summary>Nothing to report.</summary>
    public static LoadReport Empty { get; } = new([]);

    private LoadReport(IReadOnlyList<LoadNote> notes) => Notes = notes;

    /// <summary>Everything the loader recorded, in the order it found it.</summary>
    public IReadOnlyList<LoadNote> Notes { get; }

    /// <summary>True when something in the file is NOT on screen. The one flag a viewer should
    /// surface without being asked.</summary>
    public bool HasUnsupported
    {
        get { foreach (var n in Notes) if (n.Severity == LoadSeverity.Unsupported) return true; return false; }
    }

    /// <summary>True when the loader had to repair something.</summary>
    public bool HasRepairs
    {
        get { foreach (var n in Notes) if (n.Severity == LoadSeverity.Repaired) return true; return false; }
    }

    /// <summary>A sentence for a status bar, or null when there is nothing to say.</summary>
    public string? Summary
    {
        get
        {
            int unsupported = 0, repaired = 0;
            foreach (var n in Notes)
            {
                if (n.Severity == LoadSeverity.Unsupported) unsupported++;
                else if (n.Severity == LoadSeverity.Repaired) repaired++;
            }
            if (unsupported == 0 && repaired == 0) return null;

            var parts = new List<string>(2);
            if (unsupported > 0) parts.Add($"{unsupported} feature{(unsupported == 1 ? "" : "s")} not shown");
            if (repaired > 0) parts.Add($"{repaired} problem{(repaired == 1 ? "" : "s")} repaired");
            return string.Join(", ", parts);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Summary ?? "nothing to report";

    /// <summary>Accumulates notes while a loader runs. Loaders build one of these and call
    /// <see cref="Build"/> once; the result is immutable, because a report that could change after
    /// the caller read it would be worse than no report.</summary>
    public sealed class Builder
    {
        private readonly List<LoadNote> _notes = [];

        /// <summary>Note something worth knowing that lost nothing.</summary>
        public void Info(string feature, string detail) =>
            _notes.Add(new LoadNote(LoadSeverity.Info, feature, detail));

        /// <summary>Note something the loader fixed.</summary>
        public void Repaired(string feature, string detail) =>
            _notes.Add(new LoadNote(LoadSeverity.Repaired, feature, detail));

        /// <summary>Note a feature that is in the file and not on screen. Recorded ONCE per feature
        /// however many times it occurs: a file with 400 unsupported material extensions should
        /// produce one line a person reads, not 400 they scroll past.</summary>
        public void Unsupported(string feature, string detail)
        {
            foreach (var n in _notes)
                if (n.Severity == LoadSeverity.Unsupported && n.Feature == feature) return;
            _notes.Add(new LoadNote(LoadSeverity.Unsupported, feature, detail));
        }

        /// <summary>Freeze it.</summary>
        public LoadReport Build() => _notes.Count == 0 ? Empty : new LoadReport([.. _notes]);
    }
}

/// <summary>Thrown when a file cannot be read correctly — as opposed to read incompletely, which is
/// what <see cref="LoadReport"/> is for. The distinction is the whole point: an unsupported material
/// extension is a note, and a required mesh-compression extension is this, because guessing at the
/// bytes would produce geometry that is wrong rather than geometry that is plain.</summary>
public sealed class ModelFormatException : Exception
{
    /// <summary>Create one.</summary>
    public ModelFormatException(string message) : base(message) { }

    /// <summary>Create one that wraps the underlying failure.</summary>
    public ModelFormatException(string message, Exception inner) : base(message, inner) { }
}
