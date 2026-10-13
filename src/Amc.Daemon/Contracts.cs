using Amc.Core.Domain;

namespace Amc.Daemon;

/// <summary>
/// Path → worktree resolution with on-demand discovery. Implemented by the Git collector.
/// Agent providers use it to attribute tool activity to worktrees.
/// </summary>
public interface IWorktreeIndex
{
    /// <summary>
    /// Resolves an absolute path to (worktree, relative path). If the path lies in a Git
    /// repository not yet known, discovers that repository and all its worktrees first.
    /// Returns null for paths outside any Git worktree. Cheap when already known.
    /// </summary>
    ValueTask<WorktreeHit?> ResolveAsync(string absolutePath, CancellationToken ct = default);

    /// <summary>
    /// Tells the Git collector an agent is acting in a worktree, so it refreshes status
    /// (debounced) and can attribute git deltas observed while a shell command ran.
    /// </summary>
    void NotifyActivity(WorktreeActivity activity);
}

public sealed record WorktreeHit(string WorktreeId, string RepositoryId, string Root, string RelativePath);

/// <summary>
/// <paramref name="Phase"/>: "start" when a tool begins, "end" when it completes.
/// <paramref name="Kind"/>: "write" for file-writing tools, "shell" for shell commands, "read" otherwise.
/// </summary>
public sealed record WorktreeActivity(string WorktreeId, string AgentId, string ToolUseId, string Kind, string Phase, DateTimeOffset At);

public static class ActivityKinds
{
    public const string Write = "write";
    public const string Shell = "shell";
    public const string Read = "read";
    public const string Start = "start";
    public const string End = "end";
}

/// <summary>Live (volatile, not persisted) process information. Implemented by the process collector.</summary>
public interface ILiveProcesses
{
    IReadOnlyList<LiveProcess> Snapshot();
    LiveProcess? Get(int pid);
    /// <summary>The pid plus all its live descendants (for process-group control).</summary>
    IReadOnlyList<int> Tree(int pid);
}

public sealed record LiveProcess(
    int Pid, int Ppid, string ProcStart, string Command, string? Cwd, string? Tty,
    char State, double CpuPercent, long RssBytes, DateTimeOffset StartedAt,
    string? AgentId, IReadOnlyList<string> EnvSummary);

/// <summary>PTY sessions launched through `agentctl run`. Implemented by the terminal hub.</summary>
public interface ITerminalHub
{
    bool IsAttached(string terminalId);
    /// <summary>Writes input to the PTY. Returns false if the terminal is gone.</summary>
    Task<bool> WriteAsync(string terminalId, ReadOnlyMemory<byte> input, CancellationToken ct);
    /// <summary>Recent output (bounded ring buffer), raw bytes including ANSI sequences.</summary>
    byte[] Buffer(string terminalId);
}

/// <summary>Null implementations so the daemon runs while a component is disabled.</summary>
internal sealed class NullLiveProcesses : ILiveProcesses
{
    public IReadOnlyList<LiveProcess> Snapshot() => [];
    public LiveProcess? Get(int pid) => null;
    public IReadOnlyList<int> Tree(int pid) => [pid];
}

internal sealed class NullTerminalHub : ITerminalHub
{
    public bool IsAttached(string terminalId) => false;
    public Task<bool> WriteAsync(string terminalId, ReadOnlyMemory<byte> input, CancellationToken ct) => Task.FromResult(false);
    public byte[] Buffer(string terminalId) => [];
}

internal sealed class NullWorktreeIndex : IWorktreeIndex
{
    public ValueTask<WorktreeHit?> ResolveAsync(string absolutePath, CancellationToken ct = default) => ValueTask.FromResult<WorktreeHit?>(null);
    public void NotifyActivity(WorktreeActivity activity) { }
}
