using System.Globalization;

namespace Amc.Daemon.Collectors.Git;

public sealed record WorktreeEntry(string Root, string? Head, string? Branch, bool Detached, bool Bare, bool Locked, bool Prunable);

/// <summary>Per-path status relative to HEAD. Status: A, M, D, R, U, ? (see GitFileStatusData).</summary>
public sealed record FileState(string Status, int? Added = null, int? Removed = null, string? OldPath = null);

public sealed record StatusSnapshot(
    string? Head, string? Branch, bool Detached, string? Upstream, int Ahead, int Behind,
    Dictionary<string, FileState> Files);

public sealed record LogEntry(string Sha, string Author, DateTimeOffset? CommittedAt, string Subject, int FilesChanged);

/// <summary>Pure parsers for git's machine-readable (porcelain / -z) output formats.</summary>
public static class GitParsers
{
    public const string BranchPrefix = "refs/heads/";
    public const string LogFormat = "--format=%x1e%H%x1f%an%x1f%cI%x1f%s";

    /// <summary>Parses <c>git worktree list --porcelain -z</c>. The first entry is the main worktree.</summary>
    public static List<WorktreeEntry> ParseWorktreeList(string z)
    {
        var result = new List<WorktreeEntry>();
        string? root = null, head = null, branch = null;
        bool detached = false, bare = false, locked = false, prunable = false;
        foreach (var field in z.Split('\0'))
        {
            if (field.Length == 0)
            {
                if (root is not null) result.Add(new(root, head, branch, detached, bare, locked, prunable));
                root = head = branch = null; detached = bare = locked = prunable = false;
                continue;
            }
            var (key, value) = Split(field, ' ');
            switch (key)
            {
                case "worktree": root = value; break;
                case "HEAD": head = value; break;
                case "branch": branch = value.StartsWith(BranchPrefix, StringComparison.Ordinal) ? value[BranchPrefix.Length..] : value; break;
                case "detached": detached = true; break;
                case "bare": bare = true; break;
                case "locked": locked = true; break;
                case "prunable": prunable = true; break;
            }
        }
        if (root is not null) result.Add(new(root, head, branch, detached, bare, locked, prunable));
        return result;
    }

    /// <summary>Parses <c>git status --porcelain=v2 --branch -z</c>.</summary>
    public static StatusSnapshot ParseStatus(string z)
    {
        string? head = null, branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var files = new Dictionary<string, FileState>(StringComparer.Ordinal);
        var fields = z.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            if (f.Length < 2) continue;
            switch (f[0])
            {
                case '#':
                    var (key, value) = Split(f[2..], ' ');
                    switch (key)
                    {
                        case "branch.oid": head = value == "(initial)" ? null : value; break;
                        case "branch.head": branch = value == "(detached)" ? null : value; break;
                        case "branch.upstream": upstream = value; break;
                        case "branch.ab":
                            var ab = value.Split(' ');
                            if (ab.Length == 2) { ahead = Math.Abs(Int(ab[0]) ?? 0); behind = Math.Abs(Int(ab[1]) ?? 0); }
                            break;
                    }
                    break;
                case '1': // 1 XY sub mH mI mW hH hI path
                {
                    var p = f.Split(' ', 9);
                    if (p.Length == 9) files[p[8]] = new FileState(Ordinary(p[1]));
                    break;
                }
                case '2': // 2 XY sub mH mI mW hH hI Xscore path \0 origPath
                {
                    var p = f.Split(' ', 10);
                    var orig = i + 1 < fields.Length ? fields[++i] : null;
                    if (p.Length == 10) files[p[9]] = new FileState(p[1].Contains('C') ? "A" : "R", OldPath: orig);
                    break;
                }
                case 'u': // u XY sub m1 m2 m3 mW h1 h2 h3 path
                {
                    var p = f.Split(' ', 11);
                    if (p.Length == 11) files[p[10]] = new FileState("U");
                    break;
                }
                case '?':
                    files[f[2..]] = new FileState("?");
                    break;
            }
        }
        return new StatusSnapshot(head, branch, branch is null, upstream, ahead, behind, files);
    }

    private static string Ordinary(string xy)
    {
        char x = xy[0], y = xy.Length > 1 ? xy[1] : '.';
        if (x == 'A') return "A";
        if (x == 'D' || y == 'D') return "D";
        return "M";
    }

    /// <summary>Parses <c>git diff --numstat -z</c>: path → (added, removed); binary files have null counts.</summary>
    public static Dictionary<string, (int? Added, int? Removed)> ParseNumstat(string z)
    {
        var result = new Dictionary<string, (int?, int?)>(StringComparer.Ordinal);
        var fields = z.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var p = fields[i].Split('\t', 3);
            if (p.Length != 3) continue;
            var path = p[2];
            if (path.Length == 0) // rename: "+\t-\t\0old\0new"
            {
                if (i + 2 >= fields.Length) break;
                i += 2;
                path = fields[i];
            }
            result[path] = (Int(p[0]), Int(p[1]));
        }
        return result;
    }

    /// <summary>Parses <c>git log</c> with <see cref="LogFormat"/> and optionally <c>--shortstat</c>.</summary>
    public static List<LogEntry> ParseLog(string output)
    {
        var result = new List<LogEntry>();
        foreach (var rec in output.Split('\x1e'))
        {
            if (rec.Length == 0) continue;
            var nl = rec.IndexOf('\n');
            var header = nl < 0 ? rec : rec[..nl];
            var p = header.Split('\x1f', 4);
            if (p.Length < 4) continue;
            var files = 0;
            if (nl >= 0)
            {
                var stat = rec[(nl + 1)..].Trim();
                var sp = stat.IndexOf(' ');
                if (stat.Contains("changed") && sp > 0) files = Int(stat[..sp]) ?? 0;
            }
            DateTimeOffset? at = DateTimeOffset.TryParse(p[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            result.Add(new LogEntry(p[0], p[1], at, p[3], files));
        }
        return result;
    }

    /// <summary>Counts lines of a small text file; null if binary, missing or too large.</summary>
    public static int? CountLines(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes || info.LinkTarget is not null) return null;
            var bytes = File.ReadAllBytes(path);
            if (IsBinary(bytes)) return null;
            var lines = bytes.Count(b => b == (byte)'\n');
            return bytes.Length > 0 && bytes[^1] != (byte)'\n' ? lines + 1 : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Git's heuristic: a NUL byte in the first 8000 bytes means binary.</summary>
    public static bool IsBinary(ReadOnlySpan<byte> bytes) => bytes[..Math.Min(bytes.Length, 8000)].Contains((byte)0);

    private static (string Key, string Value) Split(string s, char sep)
    {
        var i = s.IndexOf(sep);
        return i < 0 ? (s, "") : (s[..i], s[(i + 1)..]);
    }

    private static int? Int(string s) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
}
