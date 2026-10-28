namespace Amc.Core.Domain;

/// <summary>
/// Absolute paths in one canonical text form on every OS: forward slashes, no "." / ".." / "//" segments and
/// no trailing slash. On Windows "c:\Repo\a.cs" becomes "C:/Repo/a.cs" (the form git prints), Git Bash's
/// "/c/Repo" becomes "C:/Repo" and comparisons ignore case. Purely lexical: no filesystem access.
/// On Linux and macOS a backslash is an ordinary file-name character and is left alone.
/// </summary>
public static class PathText
{
    public static bool Windows { get; } = OperatingSystem.IsWindows();

    /// <summary>How two canonical paths compare on this OS.</summary>
    public static StringComparison Comparison => ComparisonFor(Windows);
    internal static StringComparison ComparisonFor(bool windows) => windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>"/x", and on Windows also "C:\x", "C:/x" and "\\server\share".</summary>
    public static bool IsAbsolute(string? path) => IsAbsolute(path, Windows);

    internal static bool IsAbsolute(string? path, bool windows) =>
        !string.IsNullOrEmpty(path) && (path[0] == '/' || windows && (path[0] == '\\' || IsDrivePath(path)));

    /// <summary>Canonical form of an absolute path; a relative path is treated as relative to the root ("a/b" → "/a/b").</summary>
    public static string Normalize(string path) => Normalize(path, Windows);

    internal static string Normalize(string path, bool windows)
    {
        if (windows) path = path.Replace('\\', '/');
        string root;
        string rest;
        if (windows && IsDrivePath(path)) { root = char.ToUpperInvariant(path[0]) + ":/"; rest = path[2..]; }
        else if (windows && path.StartsWith("//", StringComparison.Ordinal)) { root = "//"; rest = path[2..]; }
        else if (windows && path.Length >= 2 && path[0] == '/' && char.IsAsciiLetter(path[1]) && (path.Length == 2 || path[2] == '/'))
        {
            root = char.ToUpperInvariant(path[1]) + ":/"; // Git Bash / MSYS: /c/Users → C:/Users
            rest = path[2..];
        }
        else { root = "/"; rest = path; }

        var parts = new List<string>();
        foreach (var seg in rest.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return root + string.Join('/', parts);
    }

    /// <summary><paramref name="path"/> made absolute against <paramref name="baseDir"/>, then normalized.</summary>
    public static string Combine(string baseDir, string path) => Combine(baseDir, path, Windows);

    internal static string Combine(string baseDir, string path, bool windows) =>
        Normalize(IsAbsolute(path, windows) ? path : baseDir + "/" + path, windows);

    /// <summary>Path relative to <paramref name="root"/> ("" for the root itself, always with '/'), or null when outside it.</summary>
    public static string? Relative(string root, string path) => Relative(root, path, Windows);

    internal static string? Relative(string root, string path, bool windows)
    {
        root = Normalize(root, windows);
        path = Normalize(path, windows);
        var cmp = ComparisonFor(windows);
        if (path.Equals(root, cmp)) return "";
        var prefix = root.EndsWith('/') ? root : root + "/";
        return path.StartsWith(prefix, cmp) ? path[prefix.Length..] : null;
    }

    public static bool IsUnder(string root, string path) => Relative(root, path) is not null;

    /// <summary>A relative path with '/' separators on every OS (what git and the dashboard expect).</summary>
    public static string Slashes(string relative) => Windows ? relative.Replace('\\', '/') : relative;

    private static bool IsDrivePath(string p) =>
        p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p.Length == 2 || p[2] is '/' or '\\');
}
