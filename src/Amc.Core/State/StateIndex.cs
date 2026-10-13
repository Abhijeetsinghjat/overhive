using Amc.Core.Domain;

namespace Amc.Core.State;

/// <summary>
/// Derived lookup indexes over <see cref="WorldState.Files"/> for incremental conflict detection.
/// Never serialized: rebuilt lazily from state after a restore, then maintained by the reducer.
/// </summary>
public sealed class StateIndex
{
    public Dictionary<string, HashSet<string>> FilesByWorktree { get; } = [];
    /// <summary>Key: <see cref="ConflictDetector.GroupKey"/> (repository + repo-relative path).</summary>
    public Dictionary<string, HashSet<string>> FilesByGroup { get; } = [];
    public Dictionary<string, HashSet<string>> FilesByAgent { get; } = [];
    /// <summary>Worktree → agent → latest time the agent WROTE a file there (for the same-worktree rule).</summary>
    public Dictionary<string, Dictionary<string, DateTimeOffset>> WritersByWorktree { get; } = [];

    public static StateIndex Build(WorldState s)
    {
        var i = new StateIndex();
        foreach (var f in s.Files.Values)
        {
            i.AddFile(f);
            foreach (var (a, attr) in f.Agents)
            {
                i.AddAttribution(a, f.Key);
                if (attr.Edits > 0) i.RecordWrite(f.WorktreeId, a, attr.LastAt);
            }
        }
        return i;
    }

    public void AddFile(FileChange f)
    {
        Add(FilesByWorktree, f.WorktreeId, f.Key);
        if (f.RepositoryId is not null) Add(FilesByGroup, ConflictDetector.GroupKey(f.RepositoryId, f.Path), f.Key);
    }

    public void RemoveFile(FileChange f)
    {
        Remove(FilesByWorktree, f.WorktreeId, f.Key);
        if (f.RepositoryId is not null) Remove(FilesByGroup, ConflictDetector.GroupKey(f.RepositoryId, f.Path), f.Key);
        foreach (var a in f.Agents.Keys) Remove(FilesByAgent, a, f.Key);
    }

    public void AddAttribution(string agentId, string fileKey) => Add(FilesByAgent, agentId, fileKey);

    public void RecordWrite(string worktreeId, string agentId, DateTimeOffset at)
    {
        if (!WritersByWorktree.TryGetValue(worktreeId, out var d)) WritersByWorktree[worktreeId] = d = [];
        if (!d.TryGetValue(agentId, out var prev) || at > prev) d[agentId] = at;
    }

    private static void Add(Dictionary<string, HashSet<string>> d, string k, string v)
    {
        if (!d.TryGetValue(k, out var set)) d[k] = set = [];
        set.Add(v);
    }

    private static void Remove(Dictionary<string, HashSet<string>> d, string k, string v)
    {
        if (d.TryGetValue(k, out var set) && set.Remove(v) && set.Count == 0) d.Remove(k);
    }
}
