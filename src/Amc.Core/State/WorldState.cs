using Amc.Core.Domain;

namespace Amc.Core.State;

/// <summary>Entity kinds used in change sets and UI patches.</summary>
public static class EntityKinds
{
    public const string Agent = "agent";
    public const string Repository = "repository";
    public const string Worktree = "worktree";
    public const string File = "file";
    public const string Message = "message";
    public const string Commit = "commit";
    public const string Test = "test";
    public const string Terminal = "terminal";
    public const string Conflict = "conflict";
    public const string Risk = "risk";
    public const string Guard = "guard";
}

public readonly record struct EntityRef(string Kind, string Id);

/// <summary>
/// The complete projected state: a deterministic fold of the event log.
/// Serializable as a snapshot. Mutated only by <see cref="Reducer"/>.
/// </summary>
public sealed class WorldState
{
    public const int MaxCommits = 2000;
    public const int MaxTestRuns = 1000;
    public const int MaxMessages = 2000;

    /// <summary>Reducer version that produced this snapshot (0 = unknown/legacy). Stamped on save.</summary>
    public int ReducerVersion { get; set; }
    public long Seq { get; set; }
    /// <summary>Timestamp of the latest applied event. Used instead of wall-clock so folds are deterministic.</summary>
    public DateTimeOffset Clock { get; set; }

    private StateIndex? _index;
    /// <summary>Derived indexes (not serialized; rebuilt lazily after restore).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public StateIndex Index => _index ??= StateIndex.Build(this);
    /// <summary>The index only if already built, so the reducer maintains it without forcing a build.</summary>
    [System.Text.Json.Serialization.JsonIgnore] internal StateIndex? BuiltIndex => _index;
    /// <summary>Event-time of the last full conflict scan (not serialized: the first batch after restore does a full scan).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset? LastFullConflictScan { get; set; }
    /// <summary>Agents whose active/terminal state flipped since the last conflict pass (their files need re-evaluation).</summary>
    [System.Text.Json.Serialization.JsonIgnore] internal HashSet<string> ConflictDirtyAgents { get; } = [];
    /// <summary>Worktrees removed/restored since the last conflict pass (their files need re-evaluation).</summary>
    [System.Text.Json.Serialization.JsonIgnore] internal HashSet<string> ConflictDirtyWorktrees { get; } = [];

    public Dictionary<string, Agent> Agents { get; set; } = [];
    public Dictionary<string, Repository> Repositories { get; set; } = [];
    public Dictionary<string, Worktree> Worktrees { get; set; } = [];
    public Dictionary<string, FileChange> Files { get; set; } = [];
    public Dictionary<string, Message> Messages { get; set; } = [];
    public Dictionary<string, Commit> Commits { get; set; } = [];
    public Dictionary<string, TestRun> TestRuns { get; set; } = [];
    public Dictionary<string, Terminal> Terminals { get; set; } = [];
    /// <summary>Highest usage seen per model response (message id), so repeated/streamed records add only their increase.</summary>
    public Dictionary<string, UsageMark> UsageMarks { get; set; } = [];
    public const int MaxUsageMarks = 20_000;
    public Dictionary<string, Conflict> Conflicts { get; set; } = [];
    /// <summary>Risky-action findings (newest kept): at most <see cref="MaxRisksPerAgent"/> per agent and <see cref="MaxRisks"/> overall.</summary>
    public Dictionary<string, Amc.Core.Risk.Risk> Risks { get; set; } = [];
    public const int MaxRisks = 2000;
    public const int MaxRisksPerAgent = 100;
    /// <summary>Active runaway-guard alerts, keyed root|rule (see Amc.Core.Guard).</summary>
    public Dictionary<string, Amc.Core.Guard.GuardAlert> GuardAlerts { get; set; } = [];

    public object? Get(EntityRef r) => r.Kind switch
    {
        EntityKinds.Agent => Agents.GetValueOrDefault(r.Id),
        EntityKinds.Repository => Repositories.GetValueOrDefault(r.Id),
        EntityKinds.Worktree => Worktrees.GetValueOrDefault(r.Id),
        EntityKinds.File => Files.GetValueOrDefault(r.Id),
        EntityKinds.Message => Messages.GetValueOrDefault(r.Id),
        EntityKinds.Commit => Commits.GetValueOrDefault(r.Id),
        EntityKinds.Test => TestRuns.GetValueOrDefault(r.Id),
        EntityKinds.Terminal => Terminals.GetValueOrDefault(r.Id),
        EntityKinds.Conflict => Conflicts.GetValueOrDefault(r.Id),
        EntityKinds.Risk => Risks.GetValueOrDefault(r.Id),
        EntityKinds.Guard => GuardAlerts.GetValueOrDefault(r.Id),
        _ => null,
    };

    /// <summary>Find the main or sub-agent owning a pid (main agents only carry pids).</summary>
    public Agent? AgentByPid(int pid, string? procStart) =>
        Agents.Values.FirstOrDefault(a => a.Pid == pid && (procStart is null || a.ProcStart == procStart));
}

/// <summary>Usage already counted for one model response (AgentId lets the runaway guard compute token rates per tree).</summary>
public sealed record UsageMark(long Input, long Output, long CacheWrite, long CacheRead, DateTimeOffset At, string? AgentId = null);
