using Amc.Core.Events;

namespace Amc.Core.Risk;

/// <summary>Severity of a risk finding. Strings (not an enum) so JSON, UI and CLI share one spelling.</summary>
public static class RiskSeverity
{
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Critical = "critical";

    public static int Rank(string? severity) => severity switch { Critical => 2, Warn => 1, _ => 0 };
    public static string Normalize(string? severity) => severity?.ToLowerInvariant() switch { Critical => Critical, Warn or "warning" => Warn, _ => Info };
    public static string Max(string a, string b) => Rank(a) >= Rank(b) ? a : b;
}

/// <summary>Stable rule identifiers. Each rule is documented in docs/RISKS.md.</summary>
public static class RiskRules
{
    public const string RecursiveDelete = "recursive-delete";
    public const string DeleteOutsideWorktree = "delete-outside-worktree";
    public const string WriteOutsideWorktree = "write-outside-worktree";
    public const string GitForcePush = "git-force-push";
    public const string GitRemoteDelete = "git-remote-delete";
    public const string GitResetHard = "git-reset-hard";
    public const string GitClean = "git-clean";
    public const string GitDiscardChanges = "git-discard-changes";
    public const string GitBranchForceDelete = "git-branch-force-delete";
    public const string ChmodOpen = "chmod-recursive";
    public const string ChownRecursive = "chown-recursive";
    public const string PipeToShell = "pipe-to-shell";
    public const string CredentialAccess = "credential-access";
    public const string PublishDeploy = "publish-deploy";
    public const string DatabaseDrop = "database-drop";
}

/// <summary>
/// One classifier result. <see cref="Span"/> is the matched simple command (words joined by single spaces,
/// quotes removed) or the file event; <see cref="Target"/> the resolved path the rule is about, if any.
/// </summary>
public sealed record RiskFinding(string RuleId, string Severity, string Explanation, string Span, string? Target = null);

public enum RiskOs { Linux, MacOs, Windows }

/// <summary>What the classifier knows about where a command runs. Every field is optional; less context means fewer findings, never guesses.</summary>
public sealed record RiskContext
{
    /// <summary>Working directory the command runs in (absolute).</summary>
    public string? Cwd { get; init; }
    public string? HomeDir { get; init; }
    public RiskOs Os { get; init; } = CurrentOs;
    /// <summary>Roots of the worktrees this agent works in. When empty, a non-system <see cref="Cwd"/> stands in.</summary>
    public IReadOnlyList<string> WorktreeRoots { get; init; } = [];
    /// <summary>Roots of every known worktree (other agents' included): writes there are not "outside", deletes there are flagged.</summary>
    public IReadOnlyList<string> KnownWorktreeRoots { get; init; } = [];
    /// <summary>Extra temporary directories (e.g. $TMPDIR); /tmp and the OS defaults are always included.</summary>
    public IReadOnlyList<string> TempDirs { get; init; } = [];
    /// <summary>Current branch of the worktree (to tell a force-push to main without a refspec).</summary>
    public string? Branch { get; init; }
    /// <summary>Whether the worktree has uncommitted changes (null = unknown).</summary>
    public bool? HasUncommittedChanges { get; init; }

    public bool Windows => Os == RiskOs.Windows;

    public static RiskOs CurrentOs =>
        OperatingSystem.IsWindows() ? RiskOs.Windows : OperatingSystem.IsMacOS() ? RiskOs.MacOs : RiskOs.Linux;
}

/// <summary>Payload of <c>risk.detected</c>. The envelope carries agent, worktree and evidence (observed / reported).</summary>
public sealed record RiskData(
    string RuleId,
    string Severity,
    string Explanation,
    string Span,
    string? Target = null,
    /// <summary>Full command line (redacted) or "&lt;kind&gt; &lt;path&gt;" for file events.</summary>
    string? Command = null,
    /// <summary>Collector the evidence came from: hook, transcript, process, protocol…</summary>
    string? Origin = null,
    /// <summary>Event type the finding was derived from (command.started, process.started, file.read …).</summary>
    string? SourceType = null,
    long? SourceSeq = null,
    string? ToolUseId = null,
    int? Pid = null);

/// <summary>A recorded finding (projection of one <c>risk.detected</c> event).</summary>
public sealed class Risk
{
    public required string Id { get; init; }
    public long Seq { get; set; }
    public string? AgentId { get; set; }
    public string? SessionId { get; set; }
    public string? WorktreeId { get; set; }
    public required string RuleId { get; set; }
    public required string Severity { get; set; }
    public required string Explanation { get; set; }
    public required string Span { get; set; }
    public string? Target { get; set; }
    public string? Command { get; set; }
    public Evidence Evidence { get; set; }
    public string? Origin { get; set; }
    public string? SourceType { get; set; }
    public long? SourceSeq { get; set; }
    public int? Pid { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>Per-agent finding counters (cumulative; the finding list itself is capped).</summary>
public sealed class RiskCounts
{
    public int Info { get; set; }
    public int Warn { get; set; }
    public int Critical { get; set; }
    public DateTimeOffset? LastAt { get; set; }

    public void Add(string severity, DateTimeOffset at)
    {
        switch (RiskSeverity.Normalize(severity))
        {
            case RiskSeverity.Critical: Critical++; break;
            case RiskSeverity.Warn: Warn++; break;
            default: Info++; break;
        }
        if (LastAt is null || at > LastAt) LastAt = at;
    }
}
