namespace Amc.Daemon.Collectors.Git;

/// <summary>
/// Validation of client-supplied worktree-relative paths. A path is accepted only if it is
/// relative, contains no NUL / leading '-' / '..' / '.git' segment, and its canonical location
/// (all symlinks resolved, including in existing ancestors of a not-yet-existing path) stays
/// inside the canonical worktree root.
/// </summary>
public static class GitPaths
{
    private const int MaxLinks = 40;

    /// <summary><paramref name="fullPath"/> is the canonical (symlink-free) absolute path, inside the canonical root.</summary>
    public static bool TryResolve(string root, string? relative, out string normalized, out string fullPath, out string error)
    {
        normalized = fullPath = error = "";
        if (string.IsNullOrEmpty(relative)) { error = "path is required"; return false; }
        if (relative.Contains('\0')) { error = "path contains NUL"; return false; }
        if (relative.StartsWith('/') || Path.IsPathRooted(relative)) { error = "path must be relative to the worktree root"; return false; }
        if (relative.StartsWith('-')) { error = "path must not start with '-'"; return false; }
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
        if (segments.Count == 0) { error = "path is empty"; return false; }
        if (segments.Any(s => s == "..")) { error = "path must not contain '..'"; return false; }
        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase))) { error = "paths inside .git are not served"; return false; }
        if (segments[0].StartsWith('-')) { error = "path must not start with '-'"; return false; }

        normalized = string.Join('/', segments);
        var realRoot = RealPath(root);
        var real = RealPath(Path.Combine(root, normalized));
        if (realRoot is null || real is null) { error = "path cannot be resolved"; return false; }
        if (!IsInside(realRoot, real))
        {
            error = "path escapes the worktree";
            return false;
        }
        // Re-check .git on the canonical path: a symlink (e.g. "gl -> .git") must not reach it either.
        if (real.Length > realRoot.Length && real[(realRoot.Length + 1)..].Split('/').Any(seg => seg.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            error = "paths inside .git are not served";
            return false;
        }
        fullPath = real;
        return true;
    }

    public static bool IsInside(string root, string path) =>
        path == root || path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>
    /// Canonical absolute path with every symlink resolved (like realpath(3)), except that
    /// trailing components that do not exist are appended lexically. Null on symlink loops.
    /// </summary>
    public static string? RealPath(string path)
    {
        var pending = new Stack<string>(Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = "/";
        var links = 0;
        var missing = false;
        while (pending.Count > 0)
        {
            var seg = pending.Pop();
            if (seg == ".") continue;
            if (seg == "..") { current = Path.GetDirectoryName(current) ?? "/"; continue; }
            var next = current == "/" ? "/" + seg : current + "/" + seg;
            if (!missing)
            {
                FileSystemInfo info = new FileInfo(next);
                if (!info.Exists) info = new DirectoryInfo(next);
                if (!info.Exists && info.LinkTarget is null) missing = true;
                else if (info.LinkTarget is { } target)
                {
                    if (++links > MaxLinks) return null;
                    var resolved = target.StartsWith('/') ? target : (current == "/" ? "/" : current + "/") + target;
                    foreach (var s in resolved.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse()) pending.Push(s);
                    current = "/";
                    continue;
                }
            }
            current = next;
        }
        return current;
    }
}
