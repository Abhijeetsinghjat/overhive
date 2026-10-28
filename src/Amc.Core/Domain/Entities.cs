using System.Text.Json.Serialization;
using Amc.Core.Events;

namespace Amc.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<AgentStatus>))]
public enum AgentStatus
{
    Created, Starting, Running, Waiting, Blocked, Paused,
    Completed, Failed, Cancelled, Disconnected, Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentKind>))]
public enum AgentKind { Main, Sub }

[JsonConverter(typeof(JsonStringEnumConverter<TaskState>))]
public enum TaskState { Pending, InProgress, Completed, Blocked, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter<MessageStatus>))]
public enum MessageStatus { Queued, Delivered, Answered, Failed }

[JsonConverter(typeof(JsonStringEnumConverter<ConflictKind>))]
public enum ConflictKind { SameFile, SameWorktree, CrossWorktree, GitUnmerged }

/// <summary>Transport names an agent can be messaged through.</summary>
public static class Transports
{
    public const string Pty = "pty";
    public const string HookMailbox = "hook-mailbox";
}

/// <summary>Stable identifiers. No magic string concatenation elsewhere.</summary>
public static class Ids
{
    public static string MainAgent(string provider, string sessionId) => $"{provider}:{sessionId}";
    /// <summary>Colon-separated (never "/") so ids are safe as single URL path segments.</summary>
    public static string SubAgent(string provider, string sessionId, string agentId) => $"{provider}:{sessionId}:{agentId}";
    /// <summary>On Windows the directory is hashed in <see cref="PathText"/> canonical form, so "C:\r\.git" and "C:/r/.git" agree.</summary>
    public static string Repository(string commonDir) => "repo:" + Hash(PathKey(commonDir));
    public static string Worktree(string root) => "wt:" + Hash(PathKey(root));
    public static string Process(int pid, string procStart) => $"pid:{pid}:{procStart}";
    public static string FileKey(string worktreeId, string relPath) => worktreeId + "|" + relPath;

    private static string PathKey(string path) => PathText.Windows ? PathText.Normalize(path) : path;

    private static string Hash(string s) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12];
}

public sealed class Agent
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public AgentKind Kind { get; set; }
    public string? SessionId { get; set; }
    public string? ParentId { get; set; }
    public List<string> ChildIds { get; set; } = [];
    public string? AgentType { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Model { get; set; }
    public string? Version { get; set; }

    public AgentStatus Status { get; set; } = AgentStatus.Created;
    public string? StatusReason { get; set; }
    public Evidence StatusEvidence { get; set; } = Evidence.Reported;
    /// <summary>Timestamp of the evidence behind the current status. Older evidence never overrides it.</summary>
    public DateTimeOffset StatusAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Process / terminal
    public int? Pid { get; set; }
    public string? ProcStart { get; set; }
    public string? TerminalId { get; set; }
    public string? TranscriptPath { get; set; }
    public string? SpawnToolUseId { get; set; }
    public List<string> Transports { get; set; } = [];

    // "Current" — each derived from the latest evidence, never guessed.
    public string? Cwd { get; set; }
    public string? GitBranch { get; set; }
    public string? CurrentTool { get; set; }
    public string? CurrentToolUseId { get; set; }
    public string? CurrentAction { get; set; }
    public string? CurrentCommand { get; set; }
    public string? CurrentFile { get; set; }
    public string? CurrentWorktreeId { get; set; }
    public string? CurrentRepositoryId { get; set; }
    public string? LastPrompt { get; set; }
    public string? LastOutput { get; set; }

    /// <summary>Worktrees this agent touched (inferred from tool paths), with last touch time.</summary>
    public Dictionary<string, DateTimeOffset> Worktrees { get; set; } = [];
    public Dictionary<string, DateTimeOffset> Repositories { get; set; } = [];

    /// <summary>This agent's own token usage (sub-agents are separate agents; roll up via ChildIds).</summary>
    public TokenUsage Usage { get; set; } = new();
    public Dictionary<string, TokenUsage> UsageByModel { get; set; } = [];

    public int ToolCount { get; set; }
    public int ToolFailures { get; set; }
    public int FilesChanged { get; set; }
    public List<AgentTask> Tasks { get; set; } = [];
    /// <summary>Risky-action findings by severity (cumulative; the findings are in WorldState.Risks).</summary>
    public Amc.Core.Risk.RiskCounts Risks { get; set; } = new();
    /// <summary>Claimed vs observed counts (null when the agent made no checkable claim).</summary>
    public Amc.Core.Verification.VerificationSummary? Verification { get; set; }

    [JsonIgnore] public bool IsTerminal => AgentStateMachine.IsTerminal(Status);
}

/// <summary>Token totals (reported by the provider per model response).</summary>
public sealed class TokenUsage
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheWrite { get; set; }
    public long CacheRead { get; set; }
    /// <summary>Number of model responses counted.</summary>
    public long Responses { get; set; }

    [JsonIgnore] public long Total => Input + Output + CacheWrite + CacheRead;

    public void Add(long input, long output, long cacheWrite, long cacheRead)
    {
        Input += input; Output += output; CacheWrite += cacheWrite; CacheRead += cacheRead;
    }
}

public sealed class AgentTask
{
    public required string Key { get; init; }
    public required string Title { get; set; }
    public TaskState State { get; set; }
    public string? ActiveForm { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class Repository
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string CommonDir { get; set; }
    public string? MainWorktree { get; set; }
    /// <summary>Web page of the repository, derived from the origin remote (no credentials); null if none.</summary>
    public string? WebUrl { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; }
}

public sealed class Worktree
{
    public required string Id { get; init; }
    public required string Root { get; set; }
    public required string RepositoryId { get; set; }
    public string? Branch { get; set; }
    public string? Head { get; set; }
    public bool Detached { get; set; }
    public bool IsMain { get; set; }
    public bool Locked { get; set; }
    public bool Prunable { get; set; }
    public bool Removed { get; set; }
    public int Modified { get; set; }
    public int Added { get; set; }
    public int DeletedFiles { get; set; }
    public int Untracked { get; set; }
    public int Conflicted { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public string? Upstream { get; set; }
    public string? Operation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Agents that touched this worktree, with last touch time.</summary>
    public Dictionary<string, DateTimeOffset> Agents { get; set; } = [];

    [JsonIgnore] public bool Dirty => Modified + Added + DeletedFiles + Untracked + Conflicted > 0;

    public string Lifecycle =>
        Removed ? "deleted" :
        Operation is not null ? Operation :
        Detached ? "detached" :
        Dirty ? "dirty" :
        Agents.Count > 0 ? "assigned" : "clean";
}

public sealed class FileChange
{
    public required string Key { get; init; }
    public required string WorktreeId { get; init; }
    public required string Path { get; set; }
    public string? RepositoryId { get; set; }
    /// <summary>Current git status (observed): A, M, D, R, U, ?, or clean. Null until git has been checked.</summary>
    public string? GitStatus { get; set; }
    /// <summary>Last change kind reported by an agent: created / modified / deleted / renamed.</summary>
    public string? LastChangeKind { get; set; }
    public string? OldPath { get; set; }
    public int? Added { get; set; }
    public int? Removed { get; set; }
    public DateTimeOffset FirstChangedAt { get; set; }
    public DateTimeOffset LastChangedAt { get; set; }
    /// <summary>Agent id → attribution (evidence + last time).</summary>
    public Dictionary<string, Attribution> Agents { get; set; } = [];
    public int EditCount { get; set; }
    public long LastEventSeq { get; set; }
}

public sealed record Attribution(Evidence Evidence, DateTimeOffset LastAt, int Edits);

public sealed class Message
{
    public required string Id { get; init; }
    public required string AgentId { get; init; }
    public required string Text { get; init; }
    public required string Transport { get; set; }
    public MessageStatus Status { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? Reply { get; set; }
    public DateTimeOffset? RepliedAt { get; set; }
}

public sealed class Conflict
{
    public required string Key { get; init; }
    public ConflictKind Kind { get; init; }
    public required string Description { get; set; }
    public string? WorktreeId { get; set; }
    public string? RepositoryId { get; set; }
    public string? Path { get; set; }
    public List<string> AgentIds { get; set; } = [];
    public List<string> WorktreeIds { get; set; } = [];
    public DateTimeOffset DetectedAt { get; set; }
    /// <summary>Hunk-level overlap and warnings sent (cross-worktree conflicts); null until analysed.</summary>
    public ConflictInsight? Insight { get; set; }
}

public sealed class Commit
{
    public required string Sha { get; init; }
    public required string WorktreeId { get; init; }
    public required string Subject { get; set; }
    public string? Branch { get; set; }
    public string? Author { get; set; }
    public string? AgentId { get; set; }
    public DateTimeOffset At { get; set; }
    public int FilesChanged { get; set; }
}

public sealed class TestRun
{
    public required string Id { get; init; }
    public string? AgentId { get; set; }
    public string? WorktreeId { get; set; }
    public required string Runner { get; set; }
    public required string Command { get; set; }
    /// <summary>running / passed / failed.</summary>
    public string State { get; set; } = "running";
    public int? Passed { get; set; }
    public int? Failed { get; set; }
    public int? Skipped { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public List<string> FailedTests { get; set; } = [];
}

public sealed class Terminal
{
    public required string Id { get; init; }
    public string? AgentId { get; set; }
    public int? Pid { get; set; }
    public string? Command { get; set; }
    public string? Cwd { get; set; }
    public bool Alive { get; set; }
    public int? ExitCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}
