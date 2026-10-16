using System.Text.Json.Serialization;
using Amc.Core.Events;

namespace Amc.Core.Domain;

/// <summary>Result of comparing the changed line ranges of one file across worktrees.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OverlapVerdict>))]
public enum OverlapVerdict
{
    /// <summary>Not analysed, or the ranges could not be read (binary handled separately, size cap, git error).</summary>
    Unknown,
    /// <summary>Same file, different regions: a merge will most likely be clean.</summary>
    Separate,
    /// <summary>Changed regions intersect or are within the context margin: a merge will most likely conflict.</summary>
    Overlapping,
}

/// <summary>Inclusive, 1-based line span in one version of a file.</summary>
public sealed record LineSpan(int Start, int End);

/// <summary>
/// Two worktrees' changes to one file, both relative to their merge base (committed and
/// uncommitted changes included). "A"/"B" spans are in each worktree's own file (what the agent
/// sees); <see cref="OverlapBase"/> is in merge-base coordinates (stable for dedupe).
/// </summary>
public sealed record OverlapPair(
    string WorktreeA,
    string WorktreeB,
    string? MergeBase,
    List<LineSpan> ChangedA,
    List<LineSpan> ChangedB,
    List<LineSpan> OverlapA,
    List<LineSpan> OverlapB,
    List<LineSpan> OverlapBase,
    bool Overlapping,
    /// <summary>`git merge-tree` on the committed heads: true = conflict on this path, false = clean, null = not checked.</summary>
    bool? CommittedConflict = null,
    string? Note = null);

/// <summary>Hunk-level overlap analysis of a cross-worktree conflict (observed: computed from git diffs).</summary>
public sealed class ConflictOverlap
{
    public OverlapVerdict Verdict { get; set; }
    public Evidence Evidence { get; set; } = Evidence.Observed;
    public DateTimeOffset AnalyzedAt { get; set; }
    /// <summary>Fingerprint of the inputs (heads, file status/seq); unchanged inputs are not re-analysed.</summary>
    public string InputKey { get; set; } = "";
    /// <summary>Changes separated by fewer than this many unchanged lines count as overlapping.</summary>
    public int Margin { get; set; }
    public List<OverlapPair> Pairs { get; set; } = [];
    public string? Note { get; set; }

    /// <summary>high = overlapping hunks, low = same file in different regions, unknown = not determined.</summary>
    public string Severity => Verdict switch { OverlapVerdict.Overlapping => "high", OverlapVerdict.Separate => "low", _ => "unknown" };
}

/// <summary>One agent's share of a conflict warning.</summary>
public sealed record ConflictWarningRecipient(
    string AgentId,
    /// <summary>delivered, queued, unsupported or failed.</summary>
    string Outcome,
    string? MessageId = null,
    string? Transport = null,
    string? Detail = null,
    /// <summary>Overlapping regions (merge-base coordinates) the agent was told about; used to dedupe automatic warnings.</summary>
    List<LineSpan>? BaseSpans = null);

public sealed record ConflictWarning(DateTimeOffset At, bool Auto, List<ConflictWarningRecipient> Recipients);

/// <summary>Analysis and warning history attached to a conflict (kept by conflict key in <see cref="State.WorldState.ConflictInsights"/>).</summary>
public sealed class ConflictInsight
{
    public const int MaxWarnings = 10;

    public ConflictOverlap? Overlap { get; set; }
    public List<ConflictWarning> Warnings { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}
