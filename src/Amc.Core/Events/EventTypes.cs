using Amc.Core.Domain;

namespace Amc.Core.Events;

/// <summary>Event type catalogue. See docs/EVENT_MODEL.md.</summary>
public static class EventTypes
{
    // Agent lifecycle
    public const string AgentCreated = "agent.created";            // AgentCreatedData
    public const string AgentSpawned = "agent.spawned";            // AgentCreatedData (sub-agent; ParentId set)
    public const string AgentStarted = "agent.started";            // AgentStatusData
    public const string AgentStatusChanged = "agent.status_changed"; // AgentStatusData
    public const string AgentCompleted = "agent.completed";        // AgentStatusData
    public const string AgentFailed = "agent.failed";              // AgentStatusData
    public const string AgentUpdated = "agent.updated";            // AgentUpdatedData (title, model, cwd …)
    public const string AgentPrompt = "agent.prompt";              // AgentTextData — user prompt to the agent (reported)
    public const string AgentOutput = "agent.output";              // AgentTextData — assistant text (reported)
    public const string AgentNotification = "agent.notification";  // AgentTextData — e.g. permission prompt
    public const string AgentUsage = "agent.usage";                // UsageData — token usage of one model response (reported)

    // Messaging (Overhive → agent and replies)
    public const string MessageSent = "agent.message_sent";        // MessageData (status queued)
    public const string MessageDelivered = "agent.message_delivered"; // MessageStatusData
    public const string MessageFailed = "agent.message_failed";    // MessageStatusData
    public const string MessageReceived = "agent.message_received"; // MessageReplyData (inferred reply)

    // Processes
    public const string ProcessStarted = "process.started";        // ProcessData
    public const string ProcessExited = "process.exited";          // ProcessExitData

    // Tool calls (non-shell) and shell commands
    public const string ToolStarted = "tool.started";              // ToolCallData
    public const string ToolCompleted = "tool.completed";          // ToolCallData
    public const string ToolFailed = "tool.failed";                // ToolCallData
    public const string CommandStarted = "command.started";        // CommandData
    public const string CommandCompleted = "command.completed";    // CommandData
    public const string CommandFailed = "command.failed";          // CommandData

    // Files changed by an agent (actor attributed)
    public const string FileCreated = "file.created";              // FileChangeData
    public const string FileModified = "file.modified";            // FileChangeData
    public const string FileDeleted = "file.deleted";              // FileChangeData
    public const string FileRenamed = "file.renamed";              // FileChangeData
    public const string FileRead = "file.read";                    // FileChangeData (reads, shown on demand)

    // Git (observed; no actor unless inferred)
    public const string GitFileStatus = "git.file_status";         // GitFileStatusData
    public const string GitBranchChanged = "git.branch_changed";   // GitRefData
    public const string GitCheckout = "git.checkout";              // GitRefData
    public const string GitCommitCreated = "git.commit_created";   // GitCommitData
    public const string GitMerge = "git.merge";                    // GitRefData
    public const string GitRebase = "git.rebase";                  // GitRefData
    public const string GitConflict = "git.conflict";              // GitConflictData

    // Repositories / worktrees
    public const string RepositoryDiscovered = "repository.discovered"; // RepositoryData
    public const string WorktreeCreated = "worktree.created";      // WorktreeData
    public const string WorktreeChanged = "worktree.changed";      // WorktreeData
    public const string WorktreeDeleted = "worktree.deleted";      // WorktreeData

    // Tests
    public const string TestStarted = "test.started";              // TestRunData
    public const string TestPassed = "test.passed";                // TestRunData
    public const string TestFailed = "test.failed";                // TestRunData

    // Tasks
    public const string TasksUpdated = "task.updated";             // TaskListData (full list snapshot from the agent)

    // Terminals (PTY sessions launched by agentctl run)
    public const string TerminalStarted = "terminal.started";      // TerminalData
    public const string TerminalExited = "terminal.exited";        // TerminalData

    // Control / audit
    public const string AuditControl = "audit.control";            // AuditData

    // Runaway guard (payloads in Amc.Core.Guard)
    public const string GuardAlert = "guard.alert";                // GuardAlertData — a tree crossed a threshold (or changed level)
    public const string GuardCleared = "guard.cleared";            // GuardClearedData
    public const string GuardDismissed = "guard.dismissed";        // GuardDismissedData — hidden by the user

    public static bool IsFileChange(string type) =>
        type is FileCreated or FileModified or FileDeleted or FileRenamed;
}

// ---- Payloads ---------------------------------------------------------------

public sealed record AgentCreatedData(
    AgentKind Kind,
    string Provider,
    string? ParentId = null,
    string? AgentType = null,
    string? Title = null,
    string? Description = null,
    string? Cwd = null,
    int? Pid = null,
    string? ProcStart = null,
    string? TranscriptPath = null,
    string? SpawnToolUseId = null,
    string? Model = null,
    string? Version = null);

public sealed record AgentStatusData(AgentStatus Status, string? Reason = null);

public sealed record AgentUpdatedData(
    string? Title = null,
    string? Cwd = null,
    string? Model = null,
    string? GitBranch = null,
    int? Pid = null,
    string? ProcStart = null,
    string? TerminalId = null,
    string? TranscriptPath = null,
    IReadOnlyList<string>? Transports = null);

public sealed record AgentTextData(string Text, string? Kind = null);

/// <summary>Token usage of one model response (cumulative values as recorded for that message id).</summary>
public sealed record UsageData(string MessageId, string? Model, long Input, long Output, long CacheWrite, long CacheRead);

public sealed record MessageData(string MessageId, string Text, string Transport, string? Author = null);
public sealed record MessageStatusData(string MessageId, string Transport, string? Detail = null);
public sealed record MessageReplyData(string MessageId, string Text);

public sealed record ProcessData(
    int Pid, int Ppid, string ProcStart, string Command, string? Cwd = null,
    string? AgentId = null, string? Tty = null);

public sealed record ProcessExitData(int Pid, string ProcStart, int? ExitCode = null, string? Reason = null);

public sealed record ToolCallData(
    string ToolUseId,
    string Tool,
    string? Summary = null,
    string? FilePath = null,
    string? Error = null,
    long? DurationMs = null,
    string? Output = null);

public sealed record CommandData(
    string ToolUseId,
    string Command,
    string? Description = null,
    string? Cwd = null,
    int? ExitCode = null,
    string? Stdout = null,
    string? Stderr = null,
    long? DurationMs = null,
    bool Interrupted = false,
    bool Background = false);

public sealed record FileChangeData(
    string? ToolUseId = null,
    string? Tool = null,
    string? OldPath = null,
    int? Added = null,
    int? Removed = null,
    /// <summary>Unified diff hunk(s) of this one edit, when the tool reported it.</summary>
    string? Patch = null,
    string? AbsolutePath = null);

public sealed record GitFileStatusData(
    /// <summary>One of A, M, D, R, U (unmerged), ? (untracked), or "clean".</summary>
    string Status,
    int? Added = null,
    int? Removed = null,
    string? OldPath = null,
    /// <summary>Agent that this change is attributed to, if any.</summary>
    string? AttributedAgentId = null);

public sealed record GitRefData(string? Branch, string? Head, string? PreviousBranch = null, string? PreviousHead = null, string? Detail = null);
public sealed record GitCommitData(string Sha, string Subject, string? Author, DateTimeOffset? CommittedAt, string? Branch, int FilesChanged = 0);
public sealed record GitConflictData(IReadOnlyList<string> Paths, string? Operation = null);

public sealed record RepositoryData(string Name, string CommonDir, string? MainWorktree, string? WebUrl = null);

public sealed record WorktreeData(
    string Root,
    string RepositoryId,
    string? Branch = null,
    string? Head = null,
    bool Detached = false,
    bool IsMain = false,
    bool Locked = false,
    bool Prunable = false,
    int Modified = 0,
    int Added = 0,
    int Deleted = 0,
    int Untracked = 0,
    int Conflicted = 0,
    int Ahead = 0,
    int Behind = 0,
    string? Upstream = null,
    string? Operation = null);

public sealed record TestRunData(
    string ToolUseId, string Runner, string Command,
    int? Passed = null, int? Failed = null, int? Skipped = null,
    long? DurationMs = null, IReadOnlyList<string>? FailedTests = null);

public sealed record TaskItemData(string Key, string Title, TaskState State, string? ActiveForm = null);
public sealed record TaskListData(IReadOnlyList<TaskItemData> Tasks, string? Tool = null);

public sealed record TerminalData(string TerminalId, int? Pid = null, string? Command = null, string? Cwd = null, int? ExitCode = null);

public sealed record AuditData(string Action, string Target, string Outcome, string? Detail = null);
