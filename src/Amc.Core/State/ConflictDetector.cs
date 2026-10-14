using Amc.Core.Domain;

namespace Amc.Core.State;

/// <summary>
/// Derives conflicts from state (pure). Incremental: each batch re-evaluates only the scopes
/// its changes touch (files, their worktrees and repo-path groups, files of agents whose
/// status changed). A full pass runs when <see cref="FullScanEvery"/> of event time has
/// elapsed (so activity windows expire) or for very large batches. Results are identical
/// to a full pass; <see cref="Detect"/> is the reference implementation used by the full pass.
/// </summary>
public static class ConflictDetector
{
    /// <summary>Agents must have touched a worktree within this window to count as concurrently active in it.</summary>
    public static readonly TimeSpan ActivityWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FullScanEvery = TimeSpan.FromSeconds(30);
    private const int FullScanChangeThreshold = 2000;

    public static void Recompute(WorldState s, HashSet<EntityRef> changed)
    {
        if (s.LastFullConflictScan is null || s.Clock - s.LastFullConflictScan >= FullScanEvery || changed.Count > FullScanChangeThreshold)
        {
            Apply(s, changed, Detect(s).ToDictionary(c => c.Key), inScope: _ => true);
            s.LastFullConflictScan = s.Clock;
            s.ConflictDirtyAgents.Clear();
            s.ConflictDirtyWorktrees.Clear();
            return;
        }

        var idx = s.Index;
        var files = new HashSet<string>();
        var groups = new HashSet<string>();
        void AddFile(string key)
        {
            files.Add(key);
            var (wt, path) = SplitFileKey(key);
            if (s.Worktrees.TryGetValue(wt, out var w)) groups.Add(GroupKey(w.RepositoryId, path));
        }
        foreach (var r in changed)
            if (r.Kind == EntityKinds.File) AddFile(r.Id);
        // Only genuine triggers expand to all of an agent's / worktree's files.
        foreach (var a in s.ConflictDirtyAgents)
            if (idx.FilesByAgent.TryGetValue(a, out var fs)) foreach (var f in fs) AddFile(f);
        foreach (var w in s.ConflictDirtyWorktrees)
            if (idx.FilesByWorktree.TryGetValue(w, out var fs)) foreach (var f in fs) AddFile(f);
        s.ConflictDirtyAgents.Clear();
        s.ConflictDirtyWorktrees.Clear();

        var next = new Dictionary<string, Conflict>();
        foreach (var key in files)
            if (s.Files.TryGetValue(key, out var f) && SameFile(s, f) is { } c) next[c.Key] = c;
        foreach (var g in groups)
            if (idx.FilesByGroup.TryGetValue(g, out var gfs) && CrossWorktree(s, gfs.Select(k => s.Files[k]).ToList()) is { } c) next[c.Key] = c;
        // Worktree-level rules are cheap (indexed) and time-dependent, so they are evaluated for every worktree.
        foreach (var w in s.Worktrees.Values)
        {
            if (w.Removed) continue;
            if (SameWorktree(s, w) is { } c3) next[c3.Key] = c3;
            if (GitUnmerged(s, w) is { } c4) next[c4.Key] = c4;
        }
        Apply(s, changed, next, k =>
            k.StartsWith("same-worktree|", StringComparison.Ordinal) || k.StartsWith("git|", StringComparison.Ordinal)
            || (k.StartsWith("same-file|", StringComparison.Ordinal) && files.Contains(k["same-file|".Length..]))
            || (k.StartsWith("cross-worktree|", StringComparison.Ordinal) && groups.Contains(k["cross-worktree|".Length..])));
    }

    private static void Apply(WorldState s, HashSet<EntityRef> changed, Dictionary<string, Conflict> next, Func<string, bool> inScope)
    {
        foreach (var key in s.Conflicts.Keys.Where(k => inScope(k) && !next.ContainsKey(k)).ToList())
        {
            s.Conflicts.Remove(key);
            changed.Add(new(EntityKinds.Conflict, key));
        }
        foreach (var (key, c) in next)
        {
            if (s.Conflicts.TryGetValue(key, out var old))
            {
                c.DetectedAt = old.DetectedAt;
                if (old.AgentIds.SequenceEqual(c.AgentIds) && old.WorktreeIds.SequenceEqual(c.WorktreeIds) && old.Description == c.Description)
                    continue;
            }
            s.Conflicts[key] = c;
            changed.Add(new(EntityKinds.Conflict, key));
        }
    }

    /// <summary>Full (reference) detection over the whole state.</summary>
    public static IEnumerable<Conflict> Detect(WorldState s)
    {
        foreach (var f in s.Files.Values)
            if (SameFile(s, f) is { } c) yield return c;
        foreach (var g in s.Files.Values.Where(f => f.RepositoryId is not null).GroupBy(f => GroupKey(f.RepositoryId!, f.Path)))
            if (CrossWorktree(s, g.ToList()) is { } c) yield return c;
        foreach (var w in s.Worktrees.Values.Where(w => !w.Removed))
        {
            if (SameWorktree(s, w) is { } c3) yield return c3;
            if (GitUnmerged(s, w) is { } c4) yield return c4;
        }
    }

    // ---- rules ---------------------------------------------------------------------------

    private static bool IsAncestor(WorldState s, string ancestorId, string agentId)
    {
        for (var (id, hops) = (agentId, 0); hops < 16 && s.Agents.TryGetValue(id, out var a) && a.ParentId is { } p; id = p, hops++)
            if (p == ancestorId) return true;
        return false;
    }

    private static bool Active(WorldState s, string agentId) => s.Agents.TryGetValue(agentId, out var a) && !a.IsTerminal;
    private static bool Dirty(FileChange f) => f.GitStatus is not null and not "clean";
    private static string Name(WorldState s, string agentId) => s.Agents.TryGetValue(agentId, out var a) ? a.Title ?? a.AgentType ?? agentId : agentId;

    /// <summary>1. Same file, same worktree, ≥2 active agents.</summary>
    private static Conflict? SameFile(WorldState s, FileChange f)
    {
        if (f.GitStatus == "clean" || f.Agents.Count < 2) return null;
        var agents = f.Agents.Keys.Where(a => Active(s, a)).Order().ToList();
        if (agents.Count < 2) return null;
        return new Conflict
        {
            Key = $"same-file|{f.Key}", Kind = ConflictKind.SameFile, WorktreeId = f.WorktreeId,
            RepositoryId = f.RepositoryId, Path = f.Path, AgentIds = agents, WorktreeIds = [f.WorktreeId],
            Description = $"{agents.Count} active agents are changing {f.Path}: {string.Join(", ", agents.Select(a => Name(s, a)))}",
            DetectedAt = s.Clock,
        };
    }

    /// <summary>2. Same repo-relative path dirty in ≥2 worktrees of one repository → likely merge conflict.</summary>
    private static Conflict? CrossWorktree(WorldState s, List<FileChange> group)
    {
        var live = group.Where(f => Dirty(f) && f.Agents.Count > 0 && s.Worktrees.TryGetValue(f.WorktreeId, out var w) && !w.Removed).ToList();
        var wts = live.Select(f => f.WorktreeId).Distinct().Order().ToList();
        if (wts.Count < 2) return null;
        var first = live[0];
        return new Conflict
        {
            Key = "cross-worktree|" + GroupKey(first.RepositoryId!, first.Path), Kind = ConflictKind.CrossWorktree,
            RepositoryId = first.RepositoryId, Path = first.Path,
            AgentIds = live.SelectMany(f => f.Agents.Keys).Distinct().Order().ToList(), WorktreeIds = wts,
            Description = $"{first.Path} is changed in {wts.Count} worktrees of the same repository; merging will likely conflict",
            DetectedAt = s.Clock,
        };
    }

    /// <summary>3. ≥2 active agents writing in one worktree within the activity window. An agent and its own
    /// sub-agents count as one writer (the parent delegates while it waits); siblings still count separately.</summary>
    private static Conflict? SameWorktree(WorldState s, Worktree wt)
    {
        if (!s.Index.WritersByWorktree.TryGetValue(wt.Id, out var writersAt) || writersAt.Count < 2) return null;
        var since = s.Clock > DateTimeOffset.MinValue + ActivityWindow ? s.Clock - ActivityWindow : DateTimeOffset.MinValue;
        var writers = writersAt.Where(kv => kv.Value >= since && Active(s, kv.Key)).Select(kv => kv.Key).Order().ToList();
        if (writers.Count < 2) return null;
        var heads = writers.Where(w => !writers.Any(other => other != w && IsAncestor(s, w, other))).ToList();
        if (heads.Count < 2) return null;
        return new Conflict
        {
            Key = $"same-worktree|{wt.Id}", Kind = ConflictKind.SameWorktree, WorktreeId = wt.Id,
            RepositoryId = wt.RepositoryId, AgentIds = writers, WorktreeIds = [wt.Id],
            Description = $"{writers.Count} active agents are writing in the same worktree {wt.Root}",
            DetectedAt = s.Clock,
        };
    }

    /// <summary>4. Git-reported conflicts.</summary>
    private static Conflict? GitUnmerged(WorldState s, Worktree wt)
    {
        if (wt.Conflicted == 0 && wt.Operation is null) return null;
        return new Conflict
        {
            Key = $"git|{wt.Id}", Kind = ConflictKind.GitUnmerged, WorktreeId = wt.Id, RepositoryId = wt.RepositoryId,
            AgentIds = [.. wt.Agents.Keys.Where(a => Active(s, a)).Order()], WorktreeIds = [wt.Id],
            Description = wt.Conflicted > 0
                ? $"{wt.Conflicted} unmerged file(s) in {wt.Root}" + (wt.Operation is null ? "" : $" ({wt.Operation} in progress)")
                : $"{wt.Operation} in progress in {wt.Root}",
            DetectedAt = s.Clock,
        };
    }

    public static string GroupKey(string repositoryId, string path) => repositoryId + "|" + path;

    private static (string Worktree, string Path) SplitFileKey(string key)
    {
        var i = key.IndexOf('|');
        return (key[..i], key[(i + 1)..]);
    }
}
