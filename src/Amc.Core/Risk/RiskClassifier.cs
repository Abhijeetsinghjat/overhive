using System.Text.RegularExpressions;

namespace Amc.Core.Risk;

/// <summary>
/// Pure, explainable classifier for risky agent actions (see docs/RISKS.md for every rule).
/// Input is a command line (or a file event) plus context; output is zero or more findings.
/// It only looks at the text of the command and at paths: it never runs anything, never touches the
/// filesystem and never opens a file (credential files are recognised by their path alone).
/// </summary>
public static partial class RiskClassifier
{
    private const int MaxDepth = 4;
    private const int MaxSpan = 300;

    public static IReadOnlyList<RiskFinding> ClassifyCommand(string? command, RiskContext ctx)
    {
        var findings = new List<RiskFinding>();
        if (string.IsNullOrWhiteSpace(command)) return findings;
        var a = new Analyzer(ctx, findings);
        a.Script(command, ctx.Cwd is { } c && RiskPaths.IsAbsolute(c, ctx.Windows) ? RiskPaths.Normalize(c, ctx.Windows) : null, 0);
        return Dedupe(findings);
    }

    /// <param name="kind">created, modified, deleted, renamed, read, or accessed (a tool was pointed at the path; only the credential rule applies).</param>
    public static IReadOnlyList<RiskFinding> ClassifyFileEvent(string kind, string? absolutePath, RiskContext ctx)
    {
        var findings = new List<RiskFinding>();
        if (string.IsNullOrEmpty(absolutePath) || !RiskPaths.IsAbsolute(absolutePath, ctx.Windows)) return findings;
        var a = new Analyzer(ctx, findings);
        a.FileEvent(kind, RiskPaths.Normalize(absolutePath, ctx.Windows));
        return Dedupe(findings);
    }

    private static List<RiskFinding> Dedupe(List<RiskFinding> findings)
    {
        var seen = new HashSet<(string, string, string?)>();
        return findings.Where(f => seen.Add((f.RuleId, f.Span, f.Target))).ToList();
    }

    // ---- vocabulary ------------------------------------------------------------------

    private static readonly HashSet<string> Shells = ["sh", "bash", "zsh", "dash", "ksh", "ash", "fish", "mksh", "busybox"];
    private static readonly HashSet<string> Interpreters = ["python", "python3", "python2", "perl", "ruby", "node", "php", "deno", "bun"];
    private static readonly HashSet<string> Downloaders = ["curl", "wget", "fetch", "http", "https", "xh", "irm", "iwr", "invoke-restmethod", "invoke-webrequest"];
    private static readonly HashSet<string> CdCommands = ["cd", "pushd", "chdir", "set-location", "sl"];
    private static readonly HashSet<string> DbClients =
        ["psql", "mysql", "mariadb", "sqlite3", "sqlite", "sqlcmd", "clickhouse-client", "clickhouse", "cqlsh", "mongo", "mongosh", "duckdb", "cockroach", "snowsql"];
    /// <summary>Commands that use a credential rather than read it out (ssh -i, ls ~/.ssh …): reported one level lower.</summary>
    private static readonly HashSet<string> UseCommands =
        ["ssh", "ssh-add", "ssh-keygen", "ssh-copy-id", "ls", "dir", "stat", "test", "[", "chmod", "chown", "file", "git", "get-childitem", "gci", "mkdir", "touch"];
    private static readonly HashSet<string> NoPathArgs = ["echo", "printf", "write-host", "write-output", "say"];
    private static readonly HashSet<string> Protected = ["main", "master"];

    [GeneratedRegex(@"\bdrop\s+(?:database|schema|table)\b(?:\s+if\s+exists)?(?:\s+[^\s;'""]+)?", RegexOptions.IgnoreCase)]
    private static partial Regex SqlDrop();
    [GeneratedRegex(@"\.dropDatabase\s*\(|\bdb\.\w+\.drop\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex MongoDrop();
    [GeneratedRegex(@"\b(?:iex|invoke-expression)\b[\s(&]+[^\n;]*?\b(?:irm|iwr|invoke-restmethod|invoke-webrequest|downloadstring|curl|wget)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellIex();

    // ---- analysis ----------------------------------------------------------------------

    private enum Loc { Unknown, Inside, WorktreeRoot, Temp, OtherWorktree, ToolHome, Outside, Catastrophic }

    private sealed record Unwrapped(string Name, List<string> Args, bool Elevated, string? Cwd, string? Nested);

    private sealed record Cmd(string Name, List<string> Args, bool Elevated, string? Cwd, string Span, ShellCommand Shell, int Index, List<ShellCommand> All);

    private sealed class Analyzer
    {
        private readonly RiskContext _ctx;
        private readonly List<RiskFinding> _out;
        private readonly bool _win;
        private readonly string? _home;
        private readonly List<string> _roots;
        private readonly List<string> _known;

        public Analyzer(RiskContext ctx, List<RiskFinding> findings)
        {
            _ctx = ctx; _out = findings; _win = ctx.Windows;
            _home = ctx.HomeDir is { } h ? RiskPaths.Normalize(h, _win) : null;
            _roots = ctx.WorktreeRoots.Where(r => RiskPaths.IsAbsolute(r, _win)).Select(r => RiskPaths.Normalize(r, _win)).Distinct().ToList();
            _known = ctx.KnownWorktreeRoots.Where(r => RiskPaths.IsAbsolute(r, _win)).Select(r => RiskPaths.Normalize(r, _win)).Distinct().ToList();
            // Outside a Git worktree the agent's working directory is its workspace, unless that is / or home or a system dir.
            if (_roots.Count == 0 && ctx.Cwd is { } cwd && RiskPaths.IsAbsolute(cwd, _win))
            {
                var c = RiskPaths.Normalize(cwd, _win);
                if (!RiskPaths.IsFilesystemRoot(c, _win) && !(_home is not null && RiskPaths.IsUnderOrEqual(_home, c, _win))
                    && !RiskPaths.IsSystem(c, _win) && RiskPaths.Depth(c) >= 2)
                    _roots.Add(c);
            }
        }

        private void Add(string rule, string severity, string explanation, string span, string? target = null) =>
            _out.Add(new RiskFinding(rule, severity, explanation, span, target));

        public void Script(string text, string? cwd, int depth)
        {
            if (depth > MaxDepth || string.IsNullOrWhiteSpace(text)) return;
            if (PowerShellIex().Match(text) is { Success: true } iex)
                Add(RiskRules.PipeToShell, RiskSeverity.Warn, "Downloads a script and runs it with Invoke-Expression: remote code runs unreviewed",
                    Cap(iex.Value));

            var cmds = ShellLexer.Parse(text, _win);
            var cur = cwd;
            for (var k = 0; k < cmds.Count; k++)
            {
                var c = cmds[k];
                foreach (var sub in c.Substitutions) Script(sub, cur, depth + 1);
                var u = Unwrap(c.Words);
                if (u is null) continue;
                if (u.Nested is not null) Script(u.Nested, u.Cwd is { } nc ? Dir(nc, cur) : cur, depth + 1);
                if (Shells.Contains(u.Name) && u.Nested is null)
                    foreach (var body in c.Heredocs) Script(body, cur, depth + 1);
                if (CdCommands.Contains(u.Name))
                {
                    var target = u.Args.FirstOrDefault(a => !a.StartsWith('-') || a == "-");
                    cur = target is null ? _home : target == "-" ? null : Dir(target, cur);
                    continue;
                }
                var x = new Cmd(u.Name, u.Args, u.Elevated, u.Cwd is { } ec ? Dir(ec, cur) : cur, Span(c), c, k, cmds);
                Deletes(x);
                Writes(x);
                Git(x);
                Permissions(x);
                PipeToShell(x);
                Credentials(x);
                Publish(x);
                Database(x);
            }
        }

        public void FileEvent(string kind, string path)
        {
            var span = Cap($"{kind} {path}");
            var cred = RiskPaths.Credential(path, _ctx);
            if (cred is { } cr)
            {
                var sev = Downgrade(cr, path, null);
                var verb = kind switch { "read" => "Read", "accessed" => "Accessed", "deleted" => "Deleted", "created" => "Created", _ => "Wrote" };
                Add(RiskRules.CredentialAccess, sev, $"{verb} {cr.Label}: {path} (matched by path; Overhive never opens it)", span, path);
            }
            if (kind is "read" or "accessed") return;
            var (loc, reason) = Locate(path);
            var deleted = kind == "deleted";
            var rule = deleted ? RiskRules.DeleteOutsideWorktree : RiskRules.WriteOutsideWorktree;
            var what = deleted ? "Deleted" : kind == "created" ? "Created" : kind == "renamed" ? "Renamed" : "Modified";
            if (loc == Loc.Catastrophic && RiskPaths.IsSystem(path, _win))
                Add(rule, RiskSeverity.Critical, $"{what} a file in {reason}: {path}", span, path);
            else if (loc is Loc.Outside || (deleted && loc is Loc.ToolHome))
                Add(rule, RiskSeverity.Warn, $"{what} {path}, outside the agent's worktree", span, path);
        }

        // ---- command structure -------------------------------------------------------

        private Unwrapped? Unwrap(List<string> words)
        {
            var i = 0;
            var elevated = false;
            string? cwd = null;
            while (true)
            {
                while (i < words.Count && IsAssignment(words[i])) i++;
                if (i >= words.Count) return null;
                var n = Name(words[i]);
                if (n is "sudo" or "doas" or "run0")
                {
                    elevated = true; i++;
                    while (i < words.Count && words[i].StartsWith('-'))
                    {
                        var o = words[i++];
                        if (o == "--") break;
                        if (o is "-u" or "-g" or "-C" or "-D" or "-h" or "-p" or "-r" or "-t" or "-U" or "-T" or "--user" or "--group" or "--chdir")
                        {
                            if (o is "-D" or "--chdir" && i < words.Count) cwd = words[i];
                            i++;
                        }
                    }
                    continue;
                }
                if (n == "env")
                {
                    i++;
                    while (i < words.Count)
                    {
                        var o = words[i];
                        if (IsAssignment(o)) { i++; continue; }
                        if (o is "-u" or "--unset") { i += 2; continue; }
                        if (o is "-C" or "--chdir") { if (i + 1 < words.Count) cwd = words[i + 1]; i += 2; continue; }
                        if (o.StartsWith("--chdir=")) { cwd = o[8..]; i++; continue; }
                        if (o is "-S" or "--split-string" && i + 1 < words.Count)
                            return new Unwrapped("env", [], elevated, cwd, string.Join(' ', words.Skip(i + 1)));
                        if (o.StartsWith('-')) { i++; continue; }
                        break;
                    }
                    continue;
                }
                if (n is "nohup" or "time" or "exec" or "builtin" or "!" or "{" or "caffeinate" or "unbuffer")
                {
                    i++;
                    while (i < words.Count && words[i].StartsWith('-') && n is "time" or "caffeinate") i++;
                    continue;
                }
                if (n == "command")
                {
                    i++;
                    if (i < words.Count && words[i] is "-v" or "-V") return null; // a lookup, not an execution
                    if (i < words.Count && words[i] == "-p") i++;
                    continue;
                }
                if (n is "nice" or "ionice" or "stdbuf" or "timeout" or "xargs" or "chrt" or "taskset")
                {
                    i++;
                    // Options that take a separate value: nice -n N, ionice -c N, timeout -s SIG / -k D, xargs -I R / -n N / -P N …
                    var valued = n switch { "nice" => "n", "ionice" => "cnp", "timeout" => "sk", "xargs" => "ILPnadsE", _ => "" };
                    while (i < words.Count && words[i].StartsWith('-'))
                    {
                        var o = words[i++];
                        if (o.Length == 2 && valued.Contains(o[1])) i++;
                    }
                    if (n is "timeout" or "chrt" or "taskset" && i < words.Count) i++; // duration / priority / mask
                    continue;
                }
                break;
            }

            var name = Name(words[i]);
            var args = words.Skip(i + 1).ToList();
            string? nested = null;
            if (Shells.Contains(name))
            {
                for (var j = 0; j < args.Count; j++)
                {
                    var a = args[j];
                    if (!a.StartsWith('-') || a.StartsWith("--")) { if (a.StartsWith("--")) continue; break; }
                    if (!a.Contains('c')) continue;
                    var script = args.Skip(j + 1).FirstOrDefault(s => !s.StartsWith('-'));
                    nested = script ?? "";
                    break;
                }
            }
            else if (name is "pwsh" or "powershell")
            {
                for (var j = 0; j < args.Count; j++)
                {
                    var a = args[j].ToLowerInvariant();
                    if (a.StartsWith("-e") && "-encodedcommand".StartsWith(a)) break; // opaque base64: not decoded
                    if (a is "-c" or "-command" or "-com" or "-comm" or "-comma" or "-comman") { nested = string.Join(' ', args.Skip(j + 1)); break; }
                }
            }
            else if (name == "cmd")
            {
                var j = args.FindIndex(a => a.Equals("/c", StringComparison.OrdinalIgnoreCase) || a.Equals("/k", StringComparison.OrdinalIgnoreCase));
                if (j >= 0) nested = string.Join(' ', args.Skip(j + 1));
            }
            else if (name == "eval")
                nested = string.Join(' ', args);
            return new Unwrapped(name, args, elevated, cwd, nested);
        }

        private static bool IsAssignment(string w)
        {
            var eq = w.IndexOf('=');
            if (eq <= 0) return false;
            for (var i = 0; i < eq; i++)
                if (!(char.IsAsciiLetterOrDigit(w[i]) || w[i] == '_') || (i == 0 && char.IsAsciiDigit(w[i]))) return false;
            return true;
        }

        private static string Name(string word)
        {
            var n = word[(Math.Max(word.LastIndexOf('/'), word.LastIndexOf('\\')) + 1)..].ToLowerInvariant();
            foreach (var ext in (string[])[".exe", ".cmd", ".bat"])
                if (n.EndsWith(ext)) return n[..^ext.Length];
            return n;
        }

        private static string Span(ShellCommand c)
        {
            var parts = c.Words.Concat(c.Redirects.Where(r => r.Target is not null).Select(r => r.Op + " " + r.Target));
            return Cap(string.Join(' ', parts));
        }

        private string? Dir(string word, string? cwd)
        {
            var r = RiskPaths.Resolve(word, cwd, _ctx);
            return r.Glob == GlobKind.None ? r.Path : null;
        }

        /// <summary>Positional arguments (options removed; everything after "--" is positional).</summary>
        private static List<string> Positionals(List<string> args, params string[] valueOptions)
        {
            var list = new List<string>();
            var end = false;
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i];
                if (!end && a == "--") { end = true; continue; }
                if (!end && a.Length > 1 && a[0] == '-')
                {
                    if (valueOptions.Contains(a, StringComparer.OrdinalIgnoreCase)) i++;
                    continue;
                }
                list.Add(a);
            }
            return list;
        }

        // ---- locations ------------------------------------------------------------------

        private (Loc Loc, string Reason) Locate(string p)
        {
            if (RiskPaths.IsFilesystemRoot(p, _win)) return (Loc.Catastrophic, "the filesystem root");
            if (_home is not null && RiskPaths.IsUnderOrEqual(_home, p, _win))
                return (Loc.Catastrophic, RiskPaths.Equal(p, _home, _win) ? "the home directory" : "a directory that contains the home directory");
            foreach (var r in _roots)
                if (RiskPaths.IsUnderOrEqual(r, p, _win) && !RiskPaths.Equal(r, p, _win))
                    return (Loc.Catastrophic, "a parent of the agent's worktree");
            foreach (var r in _roots)
            {
                if (RiskPaths.Equal(p, r, _win)) return (Loc.WorktreeRoot, "the agent's worktree");
                if (RiskPaths.IsUnderOrEqual(p, r, _win)) return (Loc.Inside, "the agent's worktree");
            }
            if (RiskPaths.IsTemp(p, _ctx)) return (Loc.Temp, "a temporary directory");
            if (RiskPaths.IsSystem(p, _win)) return (Loc.Catastrophic, "a system directory");
            if (RiskPaths.IsSensitiveHomeDir(p, _ctx)) return (Loc.Catastrophic, "a personal data / credential directory");
            foreach (var r in _known)
                if (RiskPaths.IsUnderOrEqual(p, r, _win)) return (Loc.OtherWorktree, "another worktree");
            if (RiskPaths.IsToolHome(p, _ctx)) return (Loc.ToolHome, "a tool's data directory");
            return _roots.Count == 0 ? (Loc.Unknown, "") : (Loc.Outside, "outside the agent's worktree");
        }

        private static string Show(ResolvedPath r) =>
            r.Path + (r.Glob switch { GlobKind.All => (r.Path!.EndsWith('/') ? "" : "/") + "*", GlobKind.Partial => (r.Path!.EndsWith('/') ? "" : "/") + "<pattern>", _ => "" });

        // ---- rules: deletes ---------------------------------------------------------------

        private void Deletes(Cmd x)
        {
            var recursive = false;
            var filtered = false;
            List<string> targets;
            switch (x.Name)
            {
                case "rm" or "unlink" or "shred":
                    targets = ParseRm(x.Args, out recursive, out var whatIf);
                    if (whatIf) return;
                    break;
                case "remove-item" or "ri" or "del" or "erase" or "rd" or "rmdir":
                    targets = ParseRemoveItem(x.Name, x.Args, out recursive, out var dry);
                    if (dry) return;
                    break;
                case "find":
                {
                    var i = 0;
                    targets = [];
                    while (i < x.Args.Count && !(x.Args[i].StartsWith('-') || x.Args[i] is "(" or "!" or ")")) targets.Add(x.Args[i++]);
                    if (targets.Count == 0) targets.Add(".");
                    var exec = x.Args.FindIndex(a => a is "-exec" or "-execdir" or "-ok" or "-okdir");
                    var del = x.Args.Contains("-delete") || (exec >= 0 && x.Args.Skip(exec + 1).Take(3).Any(a => Name(a) == "rm"));
                    if (!del) return;
                    recursive = true;
                    filtered = x.Args.Any(a => a is "-name" or "-iname" or "-path" or "-ipath" or "-regex" or "-iregex" or "-type" or "-mtime"
                        or "-mmin" or "-newer" or "-size" or "-empty" or "-user" or "-perm" or "-wholename" or "-atime" or "-ctime");
                    break;
                }
                default:
                    return;
            }
            foreach (var t in targets) EvaluateDelete(x, t, recursive, filtered, "Deletes");
        }

        private static List<string> ParseRm(List<string> args, out bool recursive, out bool whatIf)
        {
            recursive = false; whatIf = false;
            var targets = new List<string>();
            var end = false;
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i];
                if (!end && a == "--") { end = true; continue; }
                if (!end && a.StartsWith("--")) { if (a == "--recursive") recursive = true; continue; }
                if (!end && a.Length > 1 && a[0] == '-')
                {
                    // PowerShell's rm alias: -Recurse, -Force, -Path x …
                    if (a.Length > 3 && char.IsAsciiLetterUpper(a[1]) || a.Equals("-recurse", StringComparison.OrdinalIgnoreCase))
                    {
                        var n = a[1..].ToLowerInvariant();
                        if ("recurse".StartsWith(n)) recursive = true;
                        else if (n is "path" or "literalpath" or "lp" or "pspath" && i + 1 < args.Count) targets.Add(args[++i]);
                        else if (n is "whatif") whatIf = true;
                        continue;
                    }
                    if (a.Contains('r') || a.Contains('R')) recursive = true;
                    continue;
                }
                targets.Add(a);
            }
            return targets;
        }

        private static List<string> ParseRemoveItem(string name, List<string> args, out bool recursive, out bool dry)
        {
            recursive = false; dry = false;
            var targets = new List<string>();
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i];
                if (a.Length == 2 && a[0] == '/' && char.IsAsciiLetter(a[1]) && name is "rd" or "rmdir" or "del" or "erase")
                {
                    if (a[1] is 's' or 'S') recursive = true;
                    continue;
                }
                if (a.Length > 1 && a[0] == '-')
                {
                    var n = a[1..].ToLowerInvariant();
                    if (n.Length > 0 && "recurse".StartsWith(n)) recursive = true;
                    else if (n is "path" or "literalpath" or "lp" or "pspath" && i + 1 < args.Count) targets.Add(args[++i]);
                    else if (n is "include" or "exclude" or "filter" or "stream" && i + 1 < args.Count) i++;
                    else if (n == "whatif") dry = true;
                    else if (name == "rmdir" && n is "p" or "-parents") { /* posix rmdir: empty dirs only */ }
                    continue;
                }
                targets.AddRange(a.Contains(',') && name is "remove-item" or "ri" ? a.Split(',', StringSplitOptions.RemoveEmptyEntries) : [a]);
            }
            return targets;
        }

        private void EvaluateDelete(Cmd x, string word, bool recursive, bool filtered, string verb)
        {
            var r = RiskPaths.Resolve(word, x.Cwd, _ctx);
            if (r.UnknownVariable is { } v)
            {
                if (recursive && word.Length > v.Length && word[v.Length] is '/' or '\\')
                    Add(RiskRules.RecursiveDelete, RiskSeverity.Warn,
                        $"Recursive delete under {v}: if {v} is empty or unset this deletes from the filesystem root", x.Span, word);
                return;
            }
            if (r.Path is null)
            {
                if (recursive && (word is ".." or "../" or "..\\" || word.StartsWith("../") || word.StartsWith("..\\")))
                    Add(RiskRules.RecursiveDelete, RiskSeverity.Warn, "Recursive delete of a parent directory (working directory unknown)", x.Span, word);
                return;
            }
            var p = r.Path;
            var (loc, reason) = Locate(p);
            var shown = Show(r);
            if (recursive)
            {
                switch (loc)
                {
                    case Loc.Catastrophic:
                        var partial = r.Glob == GlobKind.Partial || filtered;
                        Add(RiskRules.RecursiveDelete, partial ? RiskSeverity.Warn : RiskSeverity.Critical,
                            $"Recursive delete {(partial ? "of matching files in" : "of")} {reason} ({shown})", x.Span, p);
                        break;
                    case Loc.WorktreeRoot when r.Glob != GlobKind.Partial && !filtered:
                        Add(RiskRules.RecursiveDelete, RiskSeverity.Warn, $"Recursive delete of the whole worktree ({shown})", x.Span, p);
                        break;
                    case Loc.OtherWorktree:
                        Add(RiskRules.RecursiveDelete, RiskSeverity.Warn, $"Recursive delete inside another worktree ({shown})", x.Span, p);
                        break;
                    case Loc.Outside or Loc.ToolHome:
                        Add(RiskRules.RecursiveDelete, RiskSeverity.Warn, $"Recursive delete outside the agent's worktree ({shown})", x.Span, p);
                        break;
                }
                return;
            }
            if (loc == Loc.Catastrophic && RiskPaths.IsSystem(p, _win))
                Add(RiskRules.DeleteOutsideWorktree, RiskSeverity.Critical, $"{verb} {shown} in {reason}", x.Span, p);
            else if (loc is Loc.Outside or Loc.OtherWorktree or Loc.ToolHome)
                Add(RiskRules.DeleteOutsideWorktree, RiskSeverity.Warn, $"{verb} {shown}, outside the agent's worktree", x.Span, p);
        }

        // ---- rules: writes -------------------------------------------------------------------

        private void Writes(Cmd x)
        {
            foreach (var r in x.Shell.Redirects)
                if (r.WritesFile) CheckWrite(x, r.Target!, "Redirects output to");
            var args = x.Args;
            switch (x.Name)
            {
                case "tee":
                    foreach (var t in Positionals(args)) CheckWrite(x, t, "Writes");
                    break;
                case "cp" or "install" or "ln" or "rsync" or "scp" or "copy-item" or "cpi" or "copy" or "xcopy":
                {
                    string[] valued = x.Name switch
                    {
                        "scp" => ["-P", "-i", "-o", "-F", "-J", "-l", "-S", "-c"],
                        "rsync" => ["-e", "--rsh", "--exclude", "--include", "--filter", "-f"],
                        "copy-item" or "cpi" => ["-Destination", "-Filter", "-Include", "-Exclude"],
                        _ => ["-t", "--target-directory", "-m", "--mode", "-o", "-g", "-S", "--suffix"],
                    };
                    var pos = Positionals(args, valued);
                    var tdir = OptionValue(args, "-t", "--target-directory", "-Destination");
                    if (tdir is not null) CheckWrite(x, tdir, "Copies into");
                    else if (pos.Count >= 2 && !IsRemote(pos[^1])) CheckWrite(x, pos[^1], "Copies to");
                    break;
                }
                case "mv" or "move-item" or "mi" or "move":
                {
                    var pos = Positionals(args, "-t", "--target-directory", "-S");
                    var dest = OptionValue(args, "-t", "--target-directory", "-Destination");
                    var sources = dest is null && pos.Count >= 2 ? pos.Take(pos.Count - 1).ToList() : pos;
                    if ((dest ?? (pos.Count >= 2 ? pos[^1] : null)) is { } d) CheckWrite(x, d, "Moves files to");
                    foreach (var s in sources) EvaluateDelete(x, s, recursive: false, filtered: false, "Moves away");
                    break;
                }
                case "touch" or "truncate":
                    foreach (var t in Positionals(args, "-r", "-d", "-t", "-s", "--size", "--reference")) CheckWrite(x, t, "Writes");
                    break;
                case "set-content" or "sc" or "add-content" or "ac" or "out-file" or "new-item" or "ni":
                {
                    var path = OptionValue(args, "-Path", "-FilePath", "-LiteralPath") ?? Positionals(args, "-Value", "-Encoding", "-ItemType", "-Name", "-Type").FirstOrDefault();
                    if (path is not null) CheckWrite(x, path, "Writes");
                    break;
                }
                case "dd":
                    foreach (var a in args.Where(a => a.StartsWith("of="))) CheckWrite(x, a[3..], "Writes");
                    break;
                case "sed" when args.Any(a => a == "-i" || a.StartsWith("-i") || a.StartsWith("--in-place")):
                {
                    var pos = Positionals(args, "-e", "-f", "--expression", "--file");
                    var hasScriptOpt = args.Any(a => a is "-e" or "-f" or "--expression" or "--file");
                    foreach (var f in hasScriptOpt ? pos : pos.Skip(1)) CheckWrite(x, f, "Edits");
                    break;
                }
            }
        }

        private static bool IsRemote(string p) => p.Contains(':') && !(p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':') && !p.StartsWith('/');

        private static string? OptionValue(List<string> args, params string[] names)
        {
            for (var i = 0; i < args.Count; i++)
            {
                foreach (var n in names)
                {
                    if (args[i].Equals(n, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) return args[i + 1];
                    if (n.StartsWith("--") && args[i].StartsWith(n + "=")) return args[i][(n.Length + 1)..];
                }
            }
            return null;
        }

        private void CheckWrite(Cmd x, string word, string verb)
        {
            var r = RiskPaths.Resolve(word, x.Cwd, _ctx);
            if (r.Path is not { } p || RiskPaths.IsDevice(p, _win) || RiskPaths.IsDevice(word, _win)) return;
            if (RiskPaths.Credential(p, _ctx) is not null) return; // reported by the credential rule
            var (loc, reason) = Locate(p);
            if (loc == Loc.Catastrophic && RiskPaths.IsSystem(p, _win))
                Add(RiskRules.WriteOutsideWorktree, RiskSeverity.Critical, $"{verb} {Show(r)} in {reason}", x.Span, p);
            else if (loc is Loc.Outside or Loc.Catastrophic)
                Add(RiskRules.WriteOutsideWorktree, RiskSeverity.Warn, $"{verb} {Show(r)}, outside the agent's worktree", x.Span, p);
        }

        // ---- rules: git ------------------------------------------------------------------------

        private void Git(Cmd x)
        {
            if (x.Name != "git") return;
            var a = x.Args;
            var i = 0;
            while (i < a.Count && a[i].StartsWith('-'))
                i += a[i] is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" ? 2 : 1;
            if (i >= a.Count) return;
            var sub = a[i];
            var rest = a.Skip(i + 1).ToList();
            switch (sub)
            {
                case "push": GitPush(x, rest); break;
                case "reset" when rest.Contains("--hard"):
                    Add(RiskRules.GitResetHard, RiskSeverity.Warn,
                        "git reset --hard discards uncommitted changes" + (_ctx.HasUncommittedChanges == true ? " (the worktree has uncommitted changes)" : ""), x.Span);
                    break;
                case "clean": GitClean(x, rest); break;
                case "checkout" or "restore": GitDiscard(x, sub, rest); break;
                case "branch": GitBranch(x, rest); break;
            }
        }

        private void GitPush(Cmd x, List<string> rest)
        {
            bool force = false, lease = false, del = false, mirror = false;
            var pos = new List<string>();
            for (var i = 0; i < rest.Count; i++)
            {
                var r = rest[i];
                if (r == "--force" || r == "--force-if-includes") force = true;
                else if (r.StartsWith("--force-with-lease")) { force = true; lease = true; }
                else if (r == "--delete") del = true;
                else if (r is "--dry-run") return;
                else if (r == "--mirror") mirror = true;
                else if (r is "--repo" or "--receive-pack" or "--exec" or "-o" or "--push-option") i++;
                else if (r.StartsWith("--")) { }
                else if (r.StartsWith('-') && r.Length > 1)
                {
                    if (r.Contains('n')) return;
                    if (r.Contains('f')) force = true;
                    if (r.Contains('d')) del = true;
                }
                else pos.Add(r);
            }
            var refspecs = pos.Skip(1).ToList();
            if (refspecs.Any(s => s.StartsWith('+'))) force = true;
            var deleting = refspecs.Where(s => del || s.TrimStart('+').StartsWith(':')).Select(Dest).Where(d => d is not null).ToList();
            var dests = refspecs.Count == 0 ? [_ctx.Branch] : refspecs.Select(Dest).ToList();
            var onProtected = dests.Any(d => d is not null && Protected.Contains(d));
            if (del || deleting.Count > 0)
            {
                var names = deleting.Count > 0 ? string.Join(", ", deleting) : "a branch";
                Add(RiskRules.GitRemoteDelete, deleting.Any(d => Protected.Contains(d!)) ? RiskSeverity.Critical : RiskSeverity.Warn,
                    $"Deletes remote branch {names}", x.Span);
            }
            if (force || mirror)
                Add(RiskRules.GitForcePush, onProtected ? RiskSeverity.Critical : RiskSeverity.Warn,
                    (mirror ? "git push --mirror overwrites every remote ref" : lease ? "Force-push (with lease) rewrites remote history" : "Force-push rewrites remote history")
                    + (onProtected ? $" on {dests.First(d => d is not null && Protected.Contains(d))}" : ""), x.Span);

            string? Dest(string spec)
            {
                var s = spec.TrimStart('+');
                var d = s.Contains(':') ? s[(s.IndexOf(':') + 1)..] : s;
                if (d.StartsWith("refs/heads/")) d = d["refs/heads/".Length..];
                return d is "" or "HEAD" or "@" ? _ctx.Branch : d;
            }
        }

        private void GitClean(Cmd x, List<string> rest)
        {
            bool f = false, d = false, xi = false;
            for (var i = 0; i < rest.Count; i++)
            {
                var r = rest[i];
                if (r is "--dry-run" or "--interactive") return;
                if (r == "--force") { f = true; continue; }
                if (r == "--exclude") { i++; continue; }
                if (r.StartsWith("--") || !r.StartsWith('-')) continue;
                foreach (var c in r[1..])
                {
                    if (c is 'n' or 'i') return;
                    if (c == 'f') f = true; else if (c == 'd') d = true; else if (c is 'x' or 'X') xi = true;
                    else if (c == 'e') { i++; break; }
                }
            }
            if (!f) return;
            if (d || xi)
                Add(RiskRules.GitClean, RiskSeverity.Warn,
                    $"git clean permanently deletes untracked{(d ? " files and directories" : " files")}{(xi ? ", including ignored ones (.env, local config, build caches)" : "")}", x.Span);
            else
                Add(RiskRules.GitClean, RiskSeverity.Info, "git clean -f permanently deletes untracked files", x.Span);
        }

        private void GitDiscard(Cmd x, string sub, List<string> rest)
        {
            var dash = rest.IndexOf("--");
            var force = sub == "checkout" && rest.Any(r => r is "-f" or "--force");
            if (sub == "restore")
            {
                var staged = rest.Any(r => r is "--staged" or "-S");
                var worktree = rest.Any(r => r is "--worktree" or "-W");
                if (staged && !worktree) return; // only unstages
            }
            var paths = dash >= 0 ? rest.Skip(dash + 1).ToList() : rest.Where(r => !r.StartsWith('-')).ToList();
            var whole = paths.Any(p => p is "." or "./" or ":/" or "*" or ":(top)" or ":/*"
                || (RiskPaths.Resolve(p, x.Cwd, _ctx).Path is { } rp && _roots.Any(r => RiskPaths.Equal(r, rp, _win))));
            if (!whole && !(force && dash < 0)) return;
            if (_ctx.HasUncommittedChanges == false) return;
            Add(RiskRules.GitDiscardChanges, _ctx.HasUncommittedChanges == true ? RiskSeverity.Warn : RiskSeverity.Info,
                $"git {sub} {(whole ? "of the whole tree" : "--force")} discards uncommitted changes" +
                (_ctx.HasUncommittedChanges == true ? " (the worktree has uncommitted changes)" : " (if there are any)"), x.Span);
        }

        private void GitBranch(Cmd x, List<string> rest)
        {
            bool D = false, d = false, f = false;
            var names = new List<string>();
            foreach (var r in rest)
            {
                if (r == "--delete") d = true;
                else if (r == "--force") f = true;
                else if (r.StartsWith("--")) { }
                else if (r.StartsWith('-') && r.Length > 1)
                {
                    if (r.Contains('D')) D = true;
                    if (r.Contains('d')) d = true;
                    if (r.Contains('f')) f = true;
                }
                else names.Add(r);
            }
            if (!(D || (d && f))) return;
            var prot = names.Any(Protected.Contains);
            Add(RiskRules.GitBranchForceDelete, prot ? RiskSeverity.Critical : RiskSeverity.Warn,
                $"Force-deletes branch {(names.Count > 0 ? string.Join(", ", names) : "?")}: commits not merged elsewhere are lost", x.Span);
        }

        // ---- rules: permissions ---------------------------------------------------------------

        private void Permissions(Cmd x)
        {
            if (x.Name is not ("chmod" or "chown" or "chgrp")) return;
            var recursive = x.Args.Any(a => a == "--recursive" || (a.StartsWith('-') && !a.StartsWith("--") && a.Contains('R')));
            if (!recursive) return;
            var pos = Positionals(x.Args);
            if (x.Args.Any(a => a.StartsWith("--reference"))) pos.Insert(0, "");
            if (pos.Count < 2) return;
            var mode = pos[0];
            foreach (var t in pos.Skip(1))
            {
                var r = RiskPaths.Resolve(t, x.Cwd, _ctx);
                if (r.Path is not { } p) continue;
                var (loc, reason) = Locate(p);
                var inside = loc is Loc.Inside or Loc.WorktreeRoot or Loc.Temp;
                if (x.Name == "chmod")
                {
                    if (WorldWritable(mode))
                        Add(RiskRules.ChmodOpen, inside ? RiskSeverity.Warn : RiskSeverity.Critical,
                            $"chmod -R {mode} makes everything under {Show(r)} world-writable" + (inside ? "" : $" ({reason})"), x.Span, p);
                    else if (loc == Loc.Catastrophic)
                        Add(RiskRules.ChmodOpen, RiskSeverity.Warn, $"Recursive permission change on {reason} ({Show(r)})", x.Span, p);
                }
                else if (loc == Loc.Catastrophic)
                    Add(RiskRules.ChownRecursive, RiskSeverity.Critical, $"Recursive ownership change on {reason} ({Show(r)})", x.Span, p);
                else if (loc is Loc.Outside or Loc.OtherWorktree or Loc.ToolHome)
                    Add(RiskRules.ChownRecursive, RiskSeverity.Warn, $"Recursive ownership change outside the agent's worktree ({Show(r)})", x.Span, p);
            }
        }

        private static bool WorldWritable(string mode)
        {
            if (mode.Length is 3 or 4 && mode.All(c => c is >= '0' and <= '7')) return ((mode[^1] - '0') & 2) != 0;
            foreach (var clause in mode.Split(','))
            {
                var m = SymbolicMode().Match(clause);
                if (!m.Success) continue;
                var who = m.Groups[1].Value;
                if ((who.Contains('o') || who.Contains('a')) && m.Groups[3].Value.Contains('w')) return true;
            }
            return false;
        }

        // ---- rules: pipe to shell -----------------------------------------------------------------

        private void PipeToShell(Cmd x)
        {
            var sev = x.Elevated ? RiskSeverity.Critical : RiskSeverity.Warn;
            if (x.Shell.Before == "|" && ExecutesStdin(x))
            {
                for (var j = x.Index - 1; j >= 0; j--)
                {
                    if (Unwrap(x.All[j].Words) is { } up && Downloaders.Contains(up.Name))
                    {
                        Add(RiskRules.PipeToShell, sev,
                            $"Pipes a download ({up.Name}) straight into {x.Name}{(x.Elevated ? " as root" : "")}: remote code runs unreviewed",
                            Cap(string.Join(" | ", x.All.Skip(j).Take(x.Index - j + 1).Select(c => string.Join(' ', c.Words)))));
                        break;
                    }
                    if (x.All[j].Before != "|") break;
                }
            }
            if (Shells.Contains(x.Name) || Interpreters.Contains(x.Name) || x.Name is "eval" or "source" or ".")
            {
                foreach (var sub in x.Shell.Substitutions)
                {
                    var inner = ShellLexer.Parse(sub, _win).FirstOrDefault();
                    if (inner is not null && Unwrap(inner.Words) is { } d && Downloaders.Contains(d.Name))
                    {
                        Add(RiskRules.PipeToShell, sev, $"Runs a script downloaded with {d.Name} without saving or reviewing it", x.Span);
                        break;
                    }
                }
            }
        }

        private static bool ExecutesStdin(Cmd x)
        {
            if (x.Name is "iex" or "invoke-expression") return true;
            if (x.Name is "pwsh" or "powershell") return x.Args.Contains("-");
            if (Interpreters.Contains(x.Name))
            {
                var first = x.Args.FirstOrDefault(a => !a.StartsWith('-') || a == "-");
                return (first is null || first == "-") && !x.Args.Any(a => a is "-c" or "-e" or "-m" or "-r" or "--eval" or "-p" or "--print");
            }
            if (!Shells.Contains(x.Name)) return false;
            foreach (var a in x.Args)
            {
                if (a is "-s" or "--") return true;
                if (a.StartsWith('-') && !a.StartsWith("--") && a.Contains('c')) return false;
                if (!a.StartsWith('-')) return false; // a script file
            }
            return true;
        }

        // ---- rules: credentials ------------------------------------------------------------------

        private void Credentials(Cmd x)
        {
            if (x.Name == "security" && x.Args.FirstOrDefault() is "find-generic-password" or "find-internet-password" or "dump-keychain" or "export" or "find-certificate")
                Add(RiskRules.CredentialAccess, RiskSeverity.Critical, $"Reads the macOS keychain (security {x.Args[0]})", x.Span);
            if (x.Name == "secret-tool" && x.Args.FirstOrDefault() is "lookup" or "search")
                Add(RiskRules.CredentialAccess, RiskSeverity.Critical, "Reads the desktop keyring (secret-tool)", x.Span);
            if (x.Name is "gpg" or "gpg2" && x.Args.Any(a => a.StartsWith("--export-secret")))
                Add(RiskRules.CredentialAccess, RiskSeverity.Critical, "Exports GnuPG secret keys", x.Span);
            if (NoPathArgs.Contains(x.Name)) return;

            var words = x.Args.Concat(x.Shell.Redirects.Where(r => r.Target is not null).Select(r => r.Target!));
            foreach (var w0 in words)
            {
                var w = w0;
                if (w.Contains("://")) continue;
                if (w.StartsWith('-')) { var eq = w.IndexOf('='); if (eq < 0) continue; w = w[(eq + 1)..]; }
                else if (!w.StartsWith('$') && w.IndexOf('=') is var e and > 0 && !w[..e].Contains('/')) w = w[(e + 1)..]; // of=… key=…
                if (w.StartsWith('@')) w = w[1..];
                if (w.Length == 0) continue;
                var r = RiskPaths.Resolve(w, x.Cwd, _ctx);
                if (r.Path is not { } p || RiskPaths.Credential(p, _ctx) is not { } cred) continue;
                var sev = Downgrade(cred, p, x.Name);
                Add(RiskRules.CredentialAccess, sev, $"Command references {cred.Label}: {Show(r)} (matched by path; Overhive never opens it)", x.Span, p);
            }
        }

        private string Downgrade((string Label, string Severity) cred, string path, string? command)
        {
            var sev = cred.Severity;
            if (sev == RiskSeverity.Warn && cred.Label is ".env secrets file" or "private key / certificate file"
                && _roots.Any(r => RiskPaths.IsUnderOrEqual(path, r, _win)))
                sev = RiskSeverity.Info; // the project's own .env / test certificates
            if (sev == RiskSeverity.Critical && command is not null && UseCommands.Contains(command))
                sev = RiskSeverity.Warn;
            return sev;
        }

        // ---- rules: publish / deploy -------------------------------------------------------------

        private void Publish(Cmd x)
        {
            var a = x.Args;
            if (a.Any(s => s.StartsWith("--dry-run") || s is "--dryrun" or "-WhatIf" or "--whatif" or "--plan")) return;
            var pos = a.Where(s => !s.StartsWith('-')).ToList();
            string? Sub(int i) => i < pos.Count ? pos[i].ToLowerInvariant() : null;
            var what = (x.Name, Sub(0), Sub(1)) switch
            {
                ("npm" or "pnpm" or "bun", "publish", _) => "Publishes a package to the npm registry",
                ("yarn", "publish", _) or ("yarn", "npm", "publish") => "Publishes a package to the npm registry",
                ("cargo", "publish", _) => "Publishes a crate to crates.io",
                ("gem", "push", _) => "Publishes a gem",
                ("twine", "upload", _) => "Uploads a package to PyPI",
                ("poetry" or "uv" or "flit" or "hatch", "publish", _) => "Publishes a Python package",
                ("docker" or "podman" or "buildah" or "nerdctl", "push", _) or ("docker" or "podman", "image", "push") => "Pushes an image to a registry",
                ("dotnet", "nuget", "push") or ("nuget", "push", _) => "Publishes a NuGet package",
                ("terraform" or "tofu" or "terragrunt", "apply", _) => "Applies infrastructure changes (terraform apply)",
                ("terraform" or "tofu" or "terragrunt", "destroy", _) => "Destroys infrastructure (terraform destroy)",
                ("pulumi", "up" or "update", _) => "Applies infrastructure changes (pulumi up)",
                ("pulumi", "destroy", _) => "Destroys infrastructure (pulumi destroy)",
                ("cdk", "deploy", _) => "Deploys a CDK stack",
                ("cdk", "destroy", _) => "Destroys a CDK stack",
                ("helm", "uninstall" or "delete", _) => "Uninstalls a Helm release",
                ("firebase", "deploy", _) => "Deploys to Firebase",
                ("fly" or "flyctl", "deploy", _) => "Deploys to Fly.io",
                ("wrangler", "deploy" or "publish", _) => "Deploys a Cloudflare Worker",
                ("serverless" or "sls", "deploy" or "remove", _) => "Deploys with Serverless",
                ("eb", "deploy" or "terminate", _) => "Deploys to Elastic Beanstalk",
                ("aws", "cloudformation", "deploy" or "delete-stack") => "Changes a CloudFormation stack",
                ("netlify", "deploy", _) when a.Contains("--prod") => "Deploys to Netlify production",
                ("vercel", _, _) when a.Contains("--prod") => "Deploys to Vercel production",
                ("gcloud", _, _) when pos.Any(s => s == "deploy") => "Deploys to Google Cloud",
                ("kubectl" or "oc", _, _) when pos.Take(3).Contains("delete") => "Deletes Kubernetes resources (kubectl delete)",
                _ => null,
            };
            if (what is not null) Add(RiskRules.PublishDeploy, RiskSeverity.Warn, what, x.Span);
        }

        // ---- rules: databases ----------------------------------------------------------------------

        private void Database(Cmd x)
        {
            if (x.Name == "dropdb") { Add(RiskRules.DatabaseDrop, RiskSeverity.Warn, "Drops a PostgreSQL database (dropdb)", x.Span); return; }
            if (x.Name == "mysqladmin" && x.Args.Any(a => a.Equals("drop", StringComparison.OrdinalIgnoreCase)))
            { Add(RiskRules.DatabaseDrop, RiskSeverity.Warn, "Drops a MySQL database (mysqladmin drop)", x.Span); return; }
            if (x.Args.Any(a => a is "db:drop" or "db:reset") && x.Name is "rails" or "rake" or "bundle")
            { Add(RiskRules.DatabaseDrop, RiskSeverity.Warn, "Drops the application database (rails db:drop / db:reset)", x.Span); return; }
            if (x.Name is "prisma" or "npx" or "pnpx" or "bunx" && x.Args.Contains("migrate") && x.Args.Contains("reset")
                || x.Args.Contains("--force-reset") && x.Args.Contains("prisma"))
            { Add(RiskRules.DatabaseDrop, RiskSeverity.Warn, "Resets the database (prisma migrate reset drops all data)", x.Span); return; }
            if (x.Name == "dotnet" && x.Args.Take(3).SequenceEqual(["ef", "database", "drop"]))
            { Add(RiskRules.DatabaseDrop, RiskSeverity.Warn, "Drops the database (dotnet ef database drop)", x.Span); return; }

            if (!DbClients.Contains(x.Name)) return;
            // SQL given as arguments, heredoc, or piped in from an earlier stage (echo "DROP TABLE x" | psql).
            var texts = new List<string> { string.Join(' ', x.Args) };
            texts.AddRange(x.Shell.Heredocs);
            for (var j = x.Index - 1; j >= 0 && x.All[j + 1].Before == "|"; j--) texts.Add(string.Join(' ', x.All[j].Words));
            foreach (var t in texts)
            {
                if (SqlDrop().Match(t) is { Success: true } m)
                { Add(RiskRules.DatabaseDrop, RiskSeverity.Warn, $"Drops a database object: {Cap(m.Value.Trim())}", x.Span); return; }
                if (MongoDrop().Match(t) is { Success: true } mm)
                { Add(RiskRules.DatabaseDrop, RiskSeverity.Warn, $"Drops a MongoDB database or collection: {Cap(mm.Value.Trim())}", x.Span); return; }
            }
        }
    }

    [GeneratedRegex(@"^([ugoa]*)([+=])([rwxXst]*)$")]
    private static partial Regex SymbolicMode();

    private static string Cap(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= MaxSpan ? s : s[..MaxSpan] + "…";
    }
}
