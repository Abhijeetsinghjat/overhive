using Amc.Core.Events;

namespace Amc.Core.Verification;

/// <summary>What an agent said it achieved, in its own text output (always evidence "reported").</summary>
public enum ClaimKind { TestsPass, BuildSucceeds, Committed, FileCreated, FileUpdated, FileDeleted }

/// <summary>How a claim compares with what Overhive saw.</summary>
public enum CheckStatus { Confirmed, Contradicted, Unverified }

/// <summary>A claim as found in one piece of agent text (output of <see cref="ClaimExtractor"/>).</summary>
public sealed record ExtractedClaim(ClaimKind Kind, string Excerpt, string? Path = null);

/// <summary>A claim stored in state. <see cref="Settled"/> is set by a <c>verification.settled</c> event once Git had time to see its effect.</summary>
public sealed class Claim
{
    /// <summary>"&lt;output event id&gt;#&lt;n&gt;": stable across replays.</summary>
    public required string Id { get; init; }
    public ClaimKind Kind { get; init; }
    public required string Excerpt { get; init; }
    public string? Path { get; init; }
    public DateTimeOffset At { get; init; }
    public bool Settled { get; set; }
    /// <summary>File claims: whether the path existed when the claim settled (observed); null = not checked.</summary>
    public bool? Exists { get; set; }
}

/// <summary>One finished test- or build-like shell command, as reported by the tool runtime.</summary>
public sealed class CheckRun
{
    public required string ToolUseId { get; init; }
    public required string AgentId { get; init; }
    public required string Command { get; init; }
    public bool IsTest { get; init; }
    public bool IsBuild { get; init; }
    public int? ExitCode { get; init; }
    /// <summary>passed / failed / interrupted.</summary>
    public required string Outcome { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
}

/// <summary>A file write attributed to an agent (reported by the tool, or inferred from a git delta).</summary>
public sealed record EditMark(string WorktreeId, string Path, DateTimeOffset At, bool IsTest, Evidence Evidence);

/// <summary>A test/build command that started and has not ended yet (protocol tools may end it without repeating the command).</summary>
public sealed record PendingRun(string Command, bool IsTest, bool IsBuild, DateTimeOffset StartedAt);

/// <summary>Per-agent verification ingredients folded from events. Bounded.</summary>
public sealed class AgentVerification
{
    public const int MaxClaims = 50, MaxRuns = 100, MaxEdits = 300, MaxPending = 50;

    public List<Claim> Claims { get; set; } = [];
    public List<CheckRun> Runs { get; set; } = [];
    public List<EditMark> Edits { get; set; } = [];
    public Dictionary<string, PendingRun> Pending { get; set; } = [];
}

/// <summary>Compact counts shown on the agent card / tree row (stored on the agent).</summary>
public sealed record VerificationSummary(int Confirmed, int Contradicted, int Unverified, int Flags);

/// <summary>Result of checking one claim against evidence.</summary>
public sealed record VerificationCheck(
    string Id,
    ClaimKind Kind,
    string AgentId,
    string Claim,
    string? Path,
    DateTimeOffset ClaimedAt,
    CheckStatus Status,
    /// <summary>Evidence level of the observation (null when nothing relevant was observed).</summary>
    Evidence? Evidence,
    string Observation,
    DateTimeOffset? ObservedAt = null,
    /// <summary>Tool use id of the command, or the commit sha, behind the observation.</summary>
    string? Ref = null);

/// <summary>A review hint that is not tied to a single claim (always inferred).</summary>
public sealed record VerificationFlag(
    string Kind,
    string Message,
    Evidence Evidence,
    IReadOnlyList<string> Files,
    DateTimeOffset? FailedAt,
    DateTimeOffset At);

public sealed record VerificationReport(
    string AgentId,
    VerificationSummary Summary,
    IReadOnlyList<VerificationCheck> Checks,
    IReadOnlyList<VerificationFlag> Flags);

/// <summary>Payload of <c>verification.settled</c>: the settle window of a claim passed (observed by the daemon).</summary>
public sealed record VerificationSettledData(string ClaimId, bool? Exists = null);

