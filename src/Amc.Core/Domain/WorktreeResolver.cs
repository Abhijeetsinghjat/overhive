namespace Amc.Core.Domain;

/// <summary>
/// Maps absolute paths to the worktree that contains them (longest root prefix wins,
/// so nested worktrees resolve correctly). Immutable; rebuild when worktrees change.
/// </summary>
public sealed class WorktreeResolver
{
    private readonly (string Root, Worktree Wt)[] _roots;

    public WorktreeResolver(IEnumerable<Worktree> worktrees) =>
        _roots = worktrees.Where(w => !w.Removed)
            .Select(w => (Root: Normalize(w.Root), Wt: w))
            .OrderByDescending(x => x.Root.Length)
            .ToArray();

    public (Worktree Worktree, string RelativePath)? Resolve(string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath) || !absolutePath.StartsWith('/')) return null;
        var p = Normalize(absolutePath);
        foreach (var (root, wt) in _roots)
        {
            if (p == root) return (wt, "");
            if (p.StartsWith(root + "/", StringComparison.Ordinal)) return (wt, p[(root.Length + 1)..]);
        }
        return null;
    }

    /// <summary>Lexically normalizes a path (no filesystem access): collapses //, ., .. and trailing slashes.</summary>
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return "/" + string.Join('/', parts);
    }
}
