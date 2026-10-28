using Amc.Core.Domain;

namespace Amc.Core.Risk;

/// <summary>How a path word ended: no glob, a whole-directory glob (<c>*</c>, <c>.*</c>), or a partial pattern (<c>*.log</c>).</summary>
public enum GlobKind { None, All, Partial }

/// <summary>A path word resolved lexically (no filesystem access, symlinks are not followed).</summary>
/// <param name="Path">Normalized absolute path with '/' separators (Windows: "C:/Users/me"); for globs, the directory before the first glob segment.</param>
/// <param name="UnknownVariable">The word starts with a variable the classifier cannot expand (e.g. <c>$BUILD_DIR/</c>); Path is null.</param>
public readonly record struct ResolvedPath(string? Path, GlobKind Glob, string? UnknownVariable = null);

/// <summary>
/// Lexical path helpers for the risk classifier: home / variable expansion, normalization for POSIX and
/// Windows paths, containment checks and the well-known system, temporary and credential locations.
/// Never touches the filesystem.
/// </summary>
public static class RiskPaths
{
    private static readonly string[] HomeVars = ["$HOME", "${HOME}", "$env:HOME", "$env:USERPROFILE", "%USERPROFILE%", "%HOME%"];
    private static readonly string[] TempVars = ["$TMPDIR", "${TMPDIR}", "$TMP", "$TEMP", "${TMP}", "${TEMP}", "$env:TEMP", "$env:TMP", "%TEMP%", "%TMP%"];
    private static readonly string[] CwdVars = ["$PWD", "${PWD}", "$(pwd)", "$(pwd -P)", "$pwd"];

    /// <summary>Windows environment variables for well-known directories, expanded lexically (cmd, PowerShell and ${env:…} spellings).</summary>
    private static readonly (string Name, string? HomeRel, string Fixed)[] WindowsDirVars =
    [
        ("SystemRoot", null, "C:/Windows"), ("windir", null, "C:/Windows"), ("SystemDrive", null, "C:/"),
        ("ProgramFiles", null, "C:/Program Files"), ("ProgramFiles(x86)", null, "C:/Program Files (x86)"),
        ("ProgramW6432", null, "C:/Program Files"), ("CommonProgramFiles", null, "C:/Program Files/Common Files"),
        ("ProgramData", null, "C:/ProgramData"), ("ALLUSERSPROFILE", null, "C:/ProgramData"), ("PUBLIC", null, "C:/Users/Public"),
        ("APPDATA", "AppData/Roaming", ""), ("LOCALAPPDATA", "AppData/Local", ""),
    ];

    private static string? ExpandWindowsDirVar(string w, RiskContext ctx)
    {
        foreach (var (name, homeRel, fixedPath) in WindowsDirVars)
        {
            if (MatchPrefix(w, ["%" + name + "%", "$env:" + name, "${env:" + name + "}"]) is not { } v) continue;
            string? dir = homeRel is null ? fixedPath : ctx.HomeDir is { } h ? Normalize(h, true) + "/" + homeRel : null;
            return dir is null ? null : dir.TrimEnd('/') + "/" + w[v.Length..];
        }
        return null;
    }

    public static ResolvedPath Resolve(string word, string? cwd, RiskContext ctx)
    {
        if (string.IsNullOrEmpty(word)) return default;
        var w = word;
        var win = ctx.Windows;
        if (w == "~" || w.StartsWith("~/") || (win && w.StartsWith("~\\")))
        {
            if (ctx.HomeDir is null) return default;
            w = ctx.HomeDir + "/" + w[1..];
        }
        else if (w.StartsWith('~')) return default; // ~user: unknown
        else if (MatchPrefix(w, HomeVars) is { } hv)
        {
            if (ctx.HomeDir is null) return default;
            w = ctx.HomeDir + "/" + w[hv.Length..];
        }
        else if (MatchPrefix(w, TempVars) is { } tv)
        {
            w = (DefaultTemp(ctx) ?? "/tmp") + "/" + w[tv.Length..];
        }
        else if (MatchPrefix(w, CwdVars) is { } cv)
        {
            if (cwd is null) return default;
            w = cwd + "/" + w[cv.Length..];
        }
        else if (win && ExpandWindowsDirVar(w, ctx) is { } wv)
        {
            w = wv;
        }
        else if (w[0] == '$' || (w[0] == '%' && w.IndexOf('%', 1) > 1))
        {
            var end = w[0] == '%' ? w.IndexOf('%', 1) + 1 : VarEnd(w);
            return new ResolvedPath(null, GlobKind.None, w[..end]);
        }

        if (!IsAbsolute(w, win))
        {
            if (cwd is null) return default;
            if (win && (w.StartsWith('/') || w.StartsWith('\\'))) w = Drive(cwd) + w; // drive-relative
            else w = cwd + "/" + w;
        }
        var norm = Normalize(w, win);
        // Cut at the first glob segment: the directory before it is what the command reaches into.
        var segs = Segments(norm, win, out var prefix);
        for (var i = 0; i < segs.Count; i++)
        {
            if (segs[i].IndexOfAny(['*', '?', '[', '{']) < 0) continue;
            var kind = segs[i] is "*" or ".*" or "{*,.*}" or "{.*,*}" && i == segs.Count - 1 ? GlobKind.All : GlobKind.Partial;
            return new ResolvedPath(Join(prefix, segs.Take(i)), kind);
        }
        return new ResolvedPath(norm, GlobKind.None);
    }

    /// <summary>
    /// Lexically normalizes an absolute path: '/' separators, no '.', '..' or duplicate separators, upper-case drive letter.
    /// On Windows the root follows <see cref="PathText"/> (Git Bash "/c/x" → "C:/x", UNC, "\\?\" prefixes).
    /// </summary>
    public static string Normalize(string path, bool windows)
    {
        var segs = Segments(path, windows, out var prefix);
        return Join(prefix, segs);
    }

    public static bool IsAbsolute(string p, bool windows) =>
        windows
            ? p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '/' or '\\' || p.StartsWith(@"\\") || p.StartsWith("//") || IsGitBashDrive(p)
            : p.StartsWith('/');

    /// <summary>Git Bash / MSYS drive path: "/c" or "/c/Users/me" (PathText maps it to "C:/Users/me").</summary>
    private static bool IsGitBashDrive(string p) =>
        p.Length >= 2 && p[0] == '/' && char.IsAsciiLetter(p[1]) && (p.Length == 2 || p[2] == '/');

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies inside it (both normalized).</summary>
    public static bool IsUnderOrEqual(string path, string root, bool windows)
    {
        var cmp = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(path, root, cmp)) return true;
        var r = root.EndsWith('/') ? root : root + "/";
        return path.StartsWith(r, cmp);
    }

    public static bool Equal(string a, string b, bool windows) =>
        string.Equals(a, b, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>"/" or "C:/" or "//server/share".</summary>
    public static bool IsFilesystemRoot(string path, bool windows)
    {
        if (path == "/") return true;
        if (!windows) return false;
        if (path.Length == 3 && path[1] == ':' && path[2] == '/') return true;
        return path.StartsWith("//") && path[2..].Count(c => c == '/') <= 1;
    }

    public static int Depth(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static readonly string[] PosixSystem =
    [
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32", "/opt", "/proc", "/root", "/sbin", "/srv",
        "/sys", "/usr", "/var", "/nix", "/snap", "/System", "/Library", "/Applications", "/private/etc", "/private/var",
        "/Volumes", "/mnt", "/media", "/cores",
    ];
    private static readonly string[] WindowsSystem =
        ["C:/Windows", "C:/Program Files", "C:/Program Files (x86)", "C:/ProgramData", "C:/Recovery", "C:/System Volume Information"];

    /// <summary>Operating-system directories (temp directories excluded: check <see cref="IsTemp"/> first).</summary>
    public static bool IsSystem(string path, bool windows) =>
        (windows ? WindowsSystem : PosixSystem).Any(r => IsUnderOrEqual(path, r, windows));

    public static string? DefaultTemp(RiskContext ctx) =>
        ctx.TempDirs.Count > 0 ? Normalize(ctx.TempDirs[0], ctx.Windows) :
        ctx.Windows ? (ctx.HomeDir is { } h ? Normalize(h, true) + "/AppData/Local/Temp" : "C:/Windows/Temp") : "/tmp";

    public static bool IsTemp(string path, RiskContext ctx)
    {
        var win = ctx.Windows;
        IEnumerable<string> roots = win
            ? ["C:/Windows/Temp", "C:/Temp", "C:/tmp"]
            : ["/tmp", "/var/tmp", "/private/tmp", "/private/var/tmp", "/var/folders", "/private/var/folders", "/dev/shm"];
        if (win && ctx.HomeDir is { } h) roots = roots.Append(Normalize(h, true) + "/AppData/Local/Temp");
        roots = roots.Concat(ctx.TempDirs.Select(t => Normalize(t, win)));
        // The temp root itself is not "temporary data": wiping all of /tmp is still reported by the caller.
        return roots.Any(r => IsUnderOrEqual(path, r, win) && !Equal(path, r, win));
    }

    /// <summary>Device files such as /dev/null and /dev/stdout (redirect targets, never files on disk).</summary>
    public static bool IsDevice(string path, bool windows) =>
        windows ? path.Equals("NUL", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/NUL", StringComparison.OrdinalIgnoreCase) || path.Equals("$null", StringComparison.OrdinalIgnoreCase)
                : path.StartsWith("/dev/") && !path.StartsWith("/dev/shm/");

    /// <summary>Per-user directories AI coding tools and package managers write to as part of normal operation.</summary>
    private static readonly string[] ToolHomes =
    [
        ".claude", ".codex", ".gemini", ".cursor", ".copilot", ".config/github-copilot", ".grok", ".aider", ".continue",
        ".opencode", ".config/opencode", ".cache", ".npm", ".nuget", ".cargo/registry", ".m2", ".gradle", ".local/share/pnpm",
    ];

    public static bool IsToolHome(string path, RiskContext ctx) =>
        ctx.HomeDir is { } h && HomeRelative(path, h, ctx.Windows) is { } rel &&
        ToolHomes.Any(t => rel == t || rel.StartsWith(t + "/", ctx.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    /// <summary>Path relative to home ("" for home itself), or null when outside home.</summary>
    public static string? HomeRelative(string path, string home, bool windows)
    {
        var h = Normalize(home, windows);
        if (Equal(path, h, windows)) return "";
        return IsUnderOrEqual(path, h, windows) ? path[(h.TrimEnd('/').Length + 1)..] : null;
    }

    /// <summary>Well-known credential stores under the home directory (matched by path only; contents are never read).</summary>
    private static readonly (string Rel, bool Tree, string Label, string Severity)[] HomeCredentials =
    [
        (".ssh", true, "SSH keys directory", RiskSeverity.Critical),
        (".aws/credentials", false, "AWS credentials", RiskSeverity.Critical),
        (".aws/sso", true, "AWS SSO token cache", RiskSeverity.Critical),
        (".aws/cli/cache", true, "AWS CLI credential cache", RiskSeverity.Critical),
        (".aws/config", false, "AWS config", RiskSeverity.Warn),
        (".config/gcloud", true, "Google Cloud credentials", RiskSeverity.Critical),
        (".azure", true, "Azure CLI credentials", RiskSeverity.Critical),
        (".netrc", false, ".netrc credentials", RiskSeverity.Critical),
        ("_netrc", false, ".netrc credentials", RiskSeverity.Critical),
        (".docker/config.json", false, "Docker registry credentials", RiskSeverity.Critical),
        (".kube/config", false, "Kubernetes credentials", RiskSeverity.Critical),
        (".gnupg", true, "GnuPG keyring", RiskSeverity.Critical),
        (".git-credentials", false, "Git stored credentials", RiskSeverity.Critical),
        (".config/gh/hosts.yml", false, "GitHub CLI token", RiskSeverity.Critical),
        (".password-store", true, "pass password store", RiskSeverity.Critical),
        ("Library/Keychains", true, "macOS keychain", RiskSeverity.Critical),
        (".local/share/keyrings", true, "GNOME keyring", RiskSeverity.Critical),
        ("AppData/Roaming/Microsoft/Credentials", true, "Windows credential store", RiskSeverity.Critical),
        ("AppData/Local/Microsoft/Credentials", true, "Windows credential store", RiskSeverity.Critical),
        (".local/state/agent-mission-control/token", false, "Overhive API token", RiskSeverity.Critical),
        (".npmrc", false, "npm auth config", RiskSeverity.Warn),
        (".pypirc", false, "PyPI credentials", RiskSeverity.Warn),
        (".gem/credentials", false, "RubyGems credentials", RiskSeverity.Warn),
        (".cargo/credentials", false, "Cargo registry token", RiskSeverity.Warn),
        (".cargo/credentials.toml", false, "Cargo registry token", RiskSeverity.Warn),
    ];

    private static readonly string[] PrivateKeyNames = ["id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "id_ecdsa_sk", "id_ed25519_sk"];
    private static readonly string[] KeyExtensions = [".pem", ".key", ".p12", ".pfx", ".jks", ".keystore", ".ppk"];
    private static readonly string[] EnvTemplates = ["example", "sample", "template", "dist", "defaults", "default", "schema", "tpl"];

    /// <summary>Credential label and base severity for a normalized absolute path, or null. Path-only; nothing is opened.</summary>
    public static (string Label, string Severity)? Credential(string path, RiskContext ctx)
    {
        var win = ctx.Windows;
        var cmp = win ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (ctx.HomeDir is { } home && HomeRelative(path, home, win) is { Length: > 0 } rel)
        {
            if (rel.StartsWith(".ssh/", cmp) && (name.StartsWith("known_hosts", cmp) || name.EndsWith(".pub", cmp))) return null;
            if (rel.Equals(".ssh/config", cmp)) return ("SSH client config", RiskSeverity.Warn);
            foreach (var (r, tree, label, sev) in HomeCredentials)
                if (rel.Equals(r, cmp) || (tree && rel.StartsWith(r + "/", cmp))) return (label, sev);
        }
        if (PrivateKeyNames.Any(n => name.Equals(n, cmp))) return ("SSH private key", RiskSeverity.Critical);
        if (name.Equals(".env", cmp) || (name.StartsWith(".env.", cmp) && !EnvTemplates.Any(t => name.EndsWith("." + t, StringComparison.OrdinalIgnoreCase))))
            return (".env secrets file", RiskSeverity.Warn);
        if (KeyExtensions.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase)) && name.Length > 4)
            return ("private key / certificate file", RiskSeverity.Warn);
        return null;
    }

    /// <summary>Directories whose recursive deletion loses irreplaceable user data even though they are "only" under home.</summary>
    public static bool IsSensitiveHomeDir(string path, RiskContext ctx)
    {
        if (ctx.HomeDir is not { } home || HomeRelative(path, home, ctx.Windows) is not { Length: > 0 } rel) return false;
        var cmp = ctx.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string[] dirs = [".ssh", ".gnupg", ".aws", ".config", ".kube", "Documents", "Desktop", "Pictures", "Library", ".local", "AppData"];
        return dirs.Any(d => rel.Equals(d, cmp));
    }

    // ---- internals -------------------------------------------------------------------

    private static string? MatchPrefix(string w, string[] vars)
    {
        foreach (var v in vars)
        {
            var cmp = v.StartsWith('%') || v.StartsWith("$env:") || v.StartsWith("${env:") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (w.StartsWith(v, cmp) && (w.Length == v.Length || w[v.Length] is '/' or '\\'))
                return w[..v.Length];
        }
        return null;
    }

    private static int VarEnd(string w)
    {
        if (w.StartsWith("${")) { var e = w.IndexOf('}'); return e < 0 ? w.Length : e + 1; }
        if (w.StartsWith("$(")) { var e = w.IndexOf(')'); return e < 0 ? w.Length : e + 1; }
        var i = 1;
        if (w.Length > 5 && w[1..].StartsWith("env:", StringComparison.OrdinalIgnoreCase)) i = 5;
        while (i < w.Length && (char.IsAsciiLetterOrDigit(w[i]) || w[i] == '_')) i++;
        return i;
    }

    private static string Drive(string cwd) => cwd.Length >= 2 && cwd[1] == ':' ? cwd[..2] : "C:";

    private static List<string> Segments(string path, bool windows, out string prefix)
    {
        var p = path;
        prefix = "/";
        if (windows)
        {
            // PathText decides the root (drive, UNC share, Git Bash /c/…, \\?\ prefixes); the tail is cleaned below.
            p = PathText.Normalize(path, true);
            prefix = PathText.Root(p, true);
            p = p[prefix.Length..];
            if (!prefix.EndsWith('/')) prefix += "/"; // "//server/share" → "//server/share/"
        }
        var parts = new List<string>();
        foreach (var seg in p.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return parts;
    }

    private static string Join(string prefix, IEnumerable<string> segs)
    {
        var tail = string.Join('/', segs);
        // A UNC share root is "//server/share" (no trailing slash), like PathText.
        return tail.Length == 0 && prefix.Length > 3 && prefix.StartsWith("//") ? prefix.TrimEnd('/') : prefix + tail;
    }
}
