namespace Amc.Core.Domain;

/// <summary>
/// Maps absolute paths to the worktree that contains them (longest root prefix wins,
/// so nested worktrees resolve correctly). Immutable; rebuild when worktrees change.
/// Paths are compared in <see cref="PathText"/> canonical form (case-insensitively on Windows).
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
        if (!PathText.IsAbsolute(absolutePath)) return null;
        var p = Normalize(absolutePath!);
        var cmp = PathText.Comparison;
        foreach (var (root, wt) in _roots)
        {
            if (p.Equals(root, cmp)) return (wt, "");
            var prefix = root.EndsWith('/') ? root : root + "/";
            if (p.StartsWith(prefix, cmp)) return (wt, p[prefix.Length..]);
        }
        return null;
    }

    /// <summary>Lexically normalizes a path (no filesystem access): see <see cref="PathText.Normalize(string)"/>.</summary>
    public static string Normalize(string path) => PathText.Normalize(path);
}
