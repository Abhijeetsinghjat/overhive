using Amc.Core.Domain;
using Amc.Core.Verification;

namespace Amc.Core.State;

/// <summary>
/// How long the in-memory state keeps things that no longer change (config section "StateRetention").
/// Windows are in days of EVENT time (<see cref="WorldState.Clock"/>), never wall-clock, so a replay of the
/// same log prunes exactly the same entities. A value of 0 or less keeps that kind forever.
/// The event log keeps its own, longer history (see <c>DetailRetentionDays</c> / <c>HistoryRetentionDays</c>).
/// </summary>
public sealed class StateRetention
{
    /// <summary>Policy given to new <see cref="WorldState"/> instances. The daemon sets it from configuration at startup.</summary>
    public static StateRetention Default { get; set; } = new();

    /// <summary>A removed worktree, its files and its file writes are dropped this long after the removal.</summary>
    public double RemovedWorktreeDays { get; set; } = 1;
    /// <summary>A file that is clean (or never seen by Git) and untouched this long is dropped.</summary>
    public double IdleFileDays { get; set; } = 7;
    /// <summary>Commits, test runs and risk findings older than this are dropped (the event log keeps them).</summary>
    public double HistoryDays { get; set; } = 14;
    /// <summary>An agent tree with no active member and no activity for this long is dropped with its usage marks, risks and messages.</summary>
    public double FinishedAgentDays { get; set; } = 14;
    /// <summary>Per-response usage marks (used to de-duplicate streamed usage and for the guard's token rates) older than this are dropped.</summary>
    public double UsageMarkDays { get; set; } = 7;
    /// <summary>Verification ingredients (claims, runs, edits) of an agent tree that ended this long ago are dropped.</summary>
    public double VerificationDays { get; set; } = 7;
    /// <summary>How often (in event time) the sweep runs.</summary>
    public double SweepEveryMinutes { get; set; } = 10;

    internal TimeSpan SweepEvery => TimeSpan.FromMinutes(Math.Max(0.1, SweepEveryMinutes));
}

/// <summary>
/// Retention sweep of the reducer: runs from <see cref="Reducer.Apply"/> whenever <see cref="StateRetention.SweepEvery"/>
/// of event time has passed since the last sweep (the time of the last sweep is part of the snapshot), so it is a
/// deterministic function of the event sequence, independent of batching and restarts.
/// </summary>
public static class RetentionFold
{
    public static void MaybeSweep(WorldState s, HashSet<EntityRef> changed)
    {
        var p = s.Retention;
        if (s.LastRetentionSweep is { } last && s.Clock - last < p.SweepEvery) return;
        s.LastRetentionSweep = s.Clock;
        Sweep(s, p, changed);
    }

    private static DateTimeOffset Cut(DateTimeOffset now, double days)
    {
        if (days <= 0) return DateTimeOffset.MinValue; // keep forever
        var span = TimeSpan.FromDays(days);
        return now - DateTimeOffset.MinValue <= span ? DateTimeOffset.MinValue : now - span;
    }

    internal static void Sweep(WorldState s, StateRetention p, HashSet<EntityRef> changed)
    {
        var now = s.Clock;
        PruneAgents(s, Cut(now, p.FinishedAgentDays), changed);
        PruneVerification(s, Cut(now, p.VerificationDays), changed);
        PruneFilesAndWorktrees(s, Cut(now, p.RemovedWorktreeDays), Cut(now, p.IdleFileDays), changed);

        var history = Cut(now, p.HistoryDays);
        Remove(s.Commits, c => c.At < history, EntityKinds.Commit, changed);
        Remove(s.TestRuns, t => (t.EndedAt ?? t.StartedAt) < history, EntityKinds.Test, changed);
        Remove(s.Risks, r => r.At < history, EntityKinds.Risk, changed); // the agent's counters stay cumulative
        var marks = Cut(now, p.UsageMarkDays);
        Remove(s.UsageMarks, m => m.At < marks, null, changed);
    }

    private static void Remove<T>(Dictionary<string, T> d, Func<T, bool> drop, string? kind, HashSet<EntityRef> changed)
    {
        List<string>? gone = null;
        foreach (var (k, v) in d)
            if (drop(v)) (gone ??= []).Add(k);
        if (gone is null) return;
        foreach (var k in gone)
        {
            d.Remove(k);
            if (kind is not null) changed.Add(new(kind, k));
        }
    }

    /// <summary>Roots of the agent forest (agents without a known parent).</summary>
    private static IEnumerable<Agent> Roots(WorldState s) =>
        s.Agents.Values.Where(a => a.ParentId is null || !s.Agents.ContainsKey(a.ParentId));

    private static DateTimeOffset LastSeen(Agent a)
    {
        var t = a.CreatedAt;
        if (a.StatusAt > t) t = a.StatusAt;
        if (a.LastActivityAt > t) t = a.LastActivityAt;
        if (a.CompletedAt is { } c && c > t) t = c;
        return t;
    }

    /// <summary>Members of a tree whose every member satisfies <paramref name="ok"/>, else null.</summary>
    private static List<Agent>? WholeTree(WorldState s, Agent root, Func<Agent, bool> ok)
    {
        var members = new List<Agent>();
        foreach (var id in Verifier.Tree(s, root.Id))
        {
            if (!s.Agents.TryGetValue(id, out var a)) continue;
            if (!ok(a)) return null;
            members.Add(a);
        }
        return members;
    }

    private static void PruneAgents(WorldState s, DateTimeOffset cut, HashSet<EntityRef> changed)
    {
        if (cut == DateTimeOffset.MinValue) return;
        var gone = new HashSet<string>();
        foreach (var root in Roots(s).ToList())
            if (WholeTree(s, root, a => !AgentStateMachine.IsActive(a.Status) && LastSeen(a) < cut) is { } tree)
                foreach (var a in tree) gone.Add(a.Id);
        if (gone.Count == 0) return;

        foreach (var id in gone)
        {
            s.Agents.Remove(id);
            s.Verification.Remove(id);
            changed.Add(new(EntityKinds.Agent, id));
        }
        Remove(s.UsageMarks, m => m.AgentId is not null && gone.Contains(m.AgentId), null, changed);
        Remove(s.Risks, r => r.AgentId is not null && gone.Contains(r.AgentId), EntityKinds.Risk, changed);
        Remove(s.Messages, m => gone.Contains(m.AgentId), EntityKinds.Message, changed);
        Remove(s.Terminals, t => !t.Alive && t.AgentId is not null && gone.Contains(t.AgentId), EntityKinds.Terminal, changed);
        foreach (var w in s.Worktrees.Values)
            if (w.Agents.Keys.Any(gone.Contains))
            {
                foreach (var id in w.Agents.Keys.Where(gone.Contains).ToList()) w.Agents.Remove(id);
                changed.Add(new(EntityKinds.Worktree, w.Id));
            }
        if (s.BuiltIndex is { } idx)
            foreach (var writers in idx.WritersByWorktree.Values)
                foreach (var id in writers.Keys.Where(gone.Contains).ToList()) writers.Remove(id);
    }

    private static void PruneVerification(WorldState s, DateTimeOffset cut, HashSet<EntityRef> changed)
    {
        if (cut == DateTimeOffset.MinValue || s.Verification.Count == 0) return;
        foreach (var root in Roots(s).ToList())
        {
            if (WholeTree(s, root, a => a.IsTerminal && LastSeen(a) < cut) is not { } tree) continue;
            foreach (var a in tree)
            {
                s.Verification.Remove(a.Id);
                if (a.Verification is null) continue;
                a.Verification = null; // the details behind the counts are gone; don't show counts nobody can open
                changed.Add(new(EntityKinds.Agent, a.Id));
            }
        }
    }

    private static void PruneFilesAndWorktrees(WorldState s, DateTimeOffset removedCut, DateTimeOffset idleCut, HashSet<EntityRef> changed)
    {
        var goneWts = removedCut == DateTimeOffset.MinValue
            ? []
            : s.Worktrees.Values.Where(w => w.Removed && w.UpdatedAt < removedCut).Select(w => w.Id).ToHashSet();
        if (goneWts.Count == 0 && idleCut == DateTimeOffset.MinValue) return;

        List<FileChange>? drop = null;
        foreach (var f in s.Files.Values)
            if (goneWts.Contains(f.WorktreeId) || (f.GitStatus is null or "clean" && f.LastChangedAt < idleCut))
                (drop ??= []).Add(f);
        foreach (var f in drop ?? [])
        {
            s.Files.Remove(f.Key);
            s.BuiltIndex?.RemoveFile(f);
            changed.Add(new(EntityKinds.File, f.Key));
        }
        if (goneWts.Count == 0) return;

        foreach (var log in s.Verification.Values) log.Edits.RemoveAll(x => goneWts.Contains(x.WorktreeId));
        if (s.BuiltIndex is { } idx)
            foreach (var w in goneWts) idx.WritersByWorktree.Remove(w);
        // The worktree itself goes once no agent still in state refers to it (agents age out on their own).
        var referenced = new HashSet<string>();
        foreach (var a in s.Agents.Values)
        {
            if (a.CurrentWorktreeId is { } cw) referenced.Add(cw);
            foreach (var w in a.Worktrees.Keys) referenced.Add(w);
        }
        foreach (var w in goneWts)
        {
            if (referenced.Contains(w)) continue;
            s.Worktrees.Remove(w);
            changed.Add(new(EntityKinds.Worktree, w));
        }
    }
}
