namespace Amc.Core.Domain;

/// <summary>
/// Absolute paths in one canonical text form on every OS: forward slashes, no "." / ".." / "//" segments and
/// no trailing slash. On Windows "c:\Repo\a.cs" becomes "C:/Repo/a.cs" (the form git prints), Git Bash's
/// "/c/Repo" becomes "C:/Repo", "\\server\share\x" becomes "//server/share/x" and comparisons ignore case.
/// Purely lexical: no filesystem access. On Linux and macOS a backslash is an ordinary file-name character
/// and is left alone.
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
        var (root, rest) = Split(path, windows);
        var parts = new List<string>();
        foreach (var seg in rest.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        if (parts.Count == 0) return root;
        return root.EndsWith('/') ? root + string.Join('/', parts) : root + "/" + string.Join('/', parts);
    }

    /// <summary>The root of a path's canonical form: "/", and on Windows "C:/" or "//server/share".</summary>
    public static string Root(string path) => Root(path, Windows);

    internal static string Root(string path, bool windows) => Split(path, windows).Root;

    /// <summary>Canonical parent directory, or null for a root.</summary>
    public static string? Parent(string path) => Parent(path, Windows);

    internal static string? Parent(string path, bool windows)
    {
        path = Normalize(path, windows);
        var root = Split(path, windows).Root;
        if (path.Length <= root.Length) return null;
        var i = path.LastIndexOf('/');
        return i < root.Length ? root : path[..i];
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

    /// <summary><paramref name="path"/> is <paramref name="root"/> or inside it (both normalized first).</summary>
    public static bool IsUnder(string root, string path) => Relative(root, path) is not null;

    /// <summary>A relative path with '/' separators on every OS (what git and the dashboard expect).</summary>
    public static string Slashes(string relative) => Slashes(relative, Windows);

    internal static string Slashes(string relative, bool windows) => windows ? relative.Replace('\\', '/') : relative;

    /// <summary>Splits into the canonical root and the tail (still to be cleaned of "." / ".." / "//").</summary>
    private static (string Root, string Tail) Split(string path, bool windows)
    {
        if (!windows) return ("/", path);
        path = path.Replace('\\', '/');
        // Win32 device / long-path prefixes: //?/C:/x, //./C:/x, //?/UNC/server/share/x.
        if (path.StartsWith("//?/", StringComparison.Ordinal) || path.StartsWith("//./", StringComparison.Ordinal))
        {
            path = path[4..];
            if (path.StartsWith("UNC/", StringComparison.OrdinalIgnoreCase)) path = "//" + path[4..];
            else if (!IsDrivePath(path)) path = "/" + path;
        }
        if (IsDrivePath(path)) return (char.ToUpperInvariant(path[0]) + ":/", path[2..]);
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            var names = path[2..].Split('/', 3, StringSplitOptions.None);
            if (names.Length >= 2 && names[0].Length > 0 && names[1].Length > 0)
                return ("//" + names[0] + "/" + names[1], names.Length == 3 ? names[2] : "");
            return ("/", path); // "//" without a server and share: an ordinary rooted path
        }
        if (path.Length >= 2 && path[0] == '/' && char.IsAsciiLetter(path[1]) && (path.Length == 2 || path[2] == '/'))
            return (char.ToUpperInvariant(path[1]) + ":/", path[2..]); // Git Bash / MSYS: /c/Users → C:/Users
        return ("/", path);
    }

    private static bool IsDrivePath(string p) =>
        p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p.Length == 2 || p[2] is '/' or '\\');
}
