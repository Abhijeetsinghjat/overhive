namespace Amc.Daemon.Collectors.Git;

/// <summary>
/// Per-worktree windows of agent shell/write activity (from <see cref="WorktreeActivity"/>).
/// A git delta observed by a refresh is attributed (inferred) to an agent only if that agent
/// is the single one whose activity overlaps the interval since the previous refresh.
/// </summary>
public sealed class ActivityWindows
{
    /// <summary>A start without an end (crashed agent, lost hook) stops counting after this.</summary>
    public static readonly TimeSpan MaxOpen = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(20);

    private sealed record Window(string AgentId, string ToolUseId, DateTimeOffset Start, DateTimeOffset? End);

    private readonly Dictionary<string, List<Window>> _byWorktree = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastActivity = new(StringComparer.Ordinal);

    public void Record(WorktreeActivity a)
    {
        lock (_byWorktree)
        {
            if (!_lastActivity.TryGetValue(a.WorktreeId, out var last) || a.At > last) _lastActivity[a.WorktreeId] = a.At;
            if (a.Kind is not (ActivityKinds.Shell or ActivityKinds.Write)) return;
            if (!_byWorktree.TryGetValue(a.WorktreeId, out var list)) _byWorktree[a.WorktreeId] = list = [];
            var i = list.FindIndex(w => w.ToolUseId == a.ToolUseId && w.AgentId == a.AgentId);
            if (a.Phase == ActivityKinds.End)
            {
                if (i >= 0) list[i] = list[i] with { End = a.At };
                else list.Add(new Window(a.AgentId, a.ToolUseId, a.At, a.At)); // end without start: a point in time
            }
            else if (i < 0) list.Add(new Window(a.AgentId, a.ToolUseId, a.At, null));
        }
    }

    /// <summary>The single agent active in the worktree during (from, to], or null if none or several.</summary>
    public string? SoleAgent(string worktreeId, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_byWorktree)
        {
            if (!_byWorktree.TryGetValue(worktreeId, out var list)) return null;
            var agents = list
                .Where(w => w.Start <= to && (w.End ?? Min(w.Start + MaxOpen, to)) >= from)
                .Select(w => w.AgentId).Distinct().Take(2).ToList();
            return agents.Count == 1 ? agents[0] : null;
        }
    }

    public DateTimeOffset? LastActivity(string worktreeId)
    {
        lock (_byWorktree) return _lastActivity.TryGetValue(worktreeId, out var t) ? t : null;
    }

    public void Prune(DateTimeOffset now)
    {
        lock (_byWorktree)
        {
            foreach (var (id, list) in _byWorktree.ToList())
            {
                list.RemoveAll(w => (w.End ?? w.Start + MaxOpen) < now - Retention);
                if (list.Count == 0) _byWorktree.Remove(id);
            }
            foreach (var (id, t) in _lastActivity.ToList()) if (t < now - Retention) _lastActivity.Remove(id);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
