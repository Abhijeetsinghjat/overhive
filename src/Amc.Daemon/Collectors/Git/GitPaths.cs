using Amc.Core.Domain;

namespace Amc.Daemon.Collectors.Git;

/// <summary>
/// Validation of client-supplied worktree-relative paths. A path is accepted only if it is
/// relative, contains no NUL / leading '-' / '..' / '.git' segment, and its canonical location
/// (all symlinks resolved, including in existing ancestors of a not-yet-existing path) stays
/// inside the canonical worktree root. On Windows '\' is a separator too, and names Windows would
/// silently rewrite (trailing dots or spaces, ':' streams, the 8.3 alias "GIT~1") are refused.
/// Canonical paths use <see cref="PathText"/> form ("C:/repo/a.cs" on Windows).
/// </summary>
public static class GitPaths
{
    private const int MaxLinks = 40;
    private static readonly char[] WindowsInvalid = ['<', '>', ':', '"', '|', '?', '*'];

    /// <summary><paramref name="fullPath"/> is the canonical (symlink-free) absolute path, inside the canonical root.</summary>
    public static bool TryResolve(string root, string? relative, out string normalized, out string fullPath, out string error)
    {
        normalized = fullPath = "";
        if (!TrySegments(relative, PathText.Windows, out var segments, out error)) return false;

        normalized = string.Join('/', segments);
        var realRoot = RealPath(root);
        var real = RealPath(root + "/" + normalized);
        if (realRoot is null || real is null) { error = "path cannot be resolved"; return false; }
        if (PathText.Relative(realRoot, real) is not { } inside)
        {
            error = "path escapes the worktree";
            return false;
        }
        // Re-check .git on the canonical path: a symlink (e.g. "gl -> .git") must not reach it either.
        if (inside.Split('/').Any(seg => IsDotGit(seg, PathText.Windows)))
        {
            error = "paths inside .git are not served";
            return false;
        }
        fullPath = real;
        return true;
    }

    /// <summary>The lexical checks on a client-supplied relative path; the segments are what git is given.</summary>
    internal static bool TrySegments(string? relative, bool windows, out List<string> segments, out string error)
    {
        segments = [];
        error = "";
        if (string.IsNullOrEmpty(relative)) { error = "path is required"; return false; }
        if (relative.Contains('\0')) { error = "path contains NUL"; return false; }
        if (PathText.IsAbsolute(relative, windows) || windows && relative.Length >= 2 && relative[1] == ':')
        {
            error = "path must be relative to the worktree root";
            return false;
        }
        if (relative.StartsWith('-')) { error = "path must not start with '-'"; return false; }
        segments = (windows ? relative.Replace('\\', '/') : relative).Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
        if (segments.Count == 0) { error = "path is empty"; return false; }
        if (segments.Any(s => s == "..")) { error = "path must not contain '..'"; return false; }
        if (segments.Any(s => IsDotGit(s, windows))) { error = "paths inside .git are not served"; return false; }
        if (windows && segments.Any(s => s.IndexOfAny(WindowsInvalid) >= 0 || s.EndsWith('.') || s.EndsWith(' ')))
        {
            error = "path is not a valid Windows file name";
            return false;
        }
        if (segments[0].StartsWith('-')) { error = "path must not start with '-'"; return false; }
        return true;
    }

    /// <summary>
    /// ".git" in any case; on Windows also the names NTFS resolves to it: ".git." / ".git " (trailing dots and
    /// spaces are dropped), ".git::$INDEX_ALLOCATION" (a stream of it) and the 8.3 alias "GIT~1".
    /// </summary>
    internal static bool IsDotGit(string segment, bool windows)
    {
        if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
        if (!windows) return false;
        var name = segment.Split(':')[0].TrimEnd('.', ' ');
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               name.Length > 4 && name.StartsWith("git~", StringComparison.OrdinalIgnoreCase) && name[4..].All(char.IsAsciiDigit);
    }

    public static bool IsInside(string root, string path) => PathText.IsUnder(root, path);

    /// <summary>
    /// Canonical absolute path with every symlink (and on Windows every junction) resolved, like realpath(3),
    /// except that trailing components that do not exist are appended lexically. Null on symlink loops and
    /// on paths the OS cannot resolve.
    /// </summary>
    public static string? RealPath(string path)
    {
        string full;
        try { full = PathText.Normalize(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var root = PathText.Root(full);
        var current = root;
        var pending = new Stack<string>();
        Push(pending, full[root.Length..]);
        var links = 0;
        var missing = false;
        while (pending.Count > 0)
        {
            var seg = pending.Pop();
            if (seg == ".") continue;
            if (seg == "..") { var i = current.LastIndexOf('/'); current = i < root.Length ? root : current[..i]; continue; }
            var next = current.EndsWith('/') ? current + seg : current + "/" + seg;
            if (!missing)
            {
                string? target;
                try
                {
                    FileSystemInfo info = new FileInfo(next);
                    if (!info.Exists) info = new DirectoryInfo(next);
                    target = info.LinkTarget;
                    if (!info.Exists && target is null) missing = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
                if (target is not null)
                {
                    if (++links > MaxLinks) return null;
                    if (!Follow(target, PathText.Windows, ref root, ref current, pending)) return null;
                    continue;
                }
            }
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Continues the walk at a link target: an absolute target restarts at its root, a relative one continues
    /// from the link's directory (<paramref name="current"/>). On Windows targets may use '\', the "\\?\" and
    /// "\??\" prefixes (junctions) or be root-relative ("\x", on the link's drive); drive-relative "C:x" is refused.
    /// </summary>
    internal static bool Follow(string target, bool windows, ref string root, ref string current, Stack<string> pending)
    {
        if (target.Length == 0) return false;
        if (windows)
        {
            target = target.Replace('\\', '/');
            if (target.StartsWith("/??/", StringComparison.Ordinal)) target = "//?/" + target[4..];
            if (target[0] == '/' && (target.Length == 1 || target[1] != '/') && root.Length == 3 && root[1] == ':')
                target = root[..2] + target; // root-relative: same drive as the link
            else if (target.Length >= 2 && target[1] == ':' && (target.Length == 2 || target[2] != '/')) return false;
        }
        if (PathText.IsAbsolute(target, windows))
        {
            var full = PathText.Normalize(target, windows);
            root = current = PathText.Root(full, windows);
            Push(pending, full[root.Length..]);
        }
        else Push(pending, target);
        return true;
    }

    private static void Push(Stack<string> pending, string relative)
    {
        foreach (var s in relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse()) pending.Push(s);
    }
}
