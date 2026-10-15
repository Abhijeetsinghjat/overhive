using System.Text;
using Amc.Daemon.Pipeline;

namespace Amc.Daemon.Collectors.Git;

public sealed record DiffResponse(string WorktreeId, string? Path, string Diff, bool Truncated);
public sealed record FileResponse(string WorktreeId, string Path, long Size, bool Binary, bool Truncated, string? Content);
public sealed record LogResponse(string WorktreeId, string? Path, IReadOnlyList<LogEntry> Commits);
public sealed record BlameLine(int Line, string Sha, string? Author, DateTimeOffset? At, string? Summary, string Content);
public sealed record BlameResponse(string WorktreeId, string Path, IReadOnlyList<BlameLine> Lines, bool Truncated);
public sealed record AgentFileDiff(string WorktreeId, string Root, string Path, string? Status, string Diff, bool Truncated);
public sealed record AgentDiffResponse(string AgentId, IReadOnlyList<AgentFileDiff> Files, bool Truncated);

/// <summary>
/// Read-only Git queries for the UI. Every client path is validated by <see cref="GitPaths"/>
/// (relative, canonicalized, symlinks resolved, inside the worktree) and passed after "--";
/// pathspec magic is disabled (GIT_LITERAL_PATHSPECS) by <see cref="GitCli"/>.
/// </summary>
public static class GitEndpoints
{
    public const int MaxDiffChars = 2 * 1024 * 1024;
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxBlameLines = 5000;
    public const int MaxUntrackedInDiff = 200;
    public const int MaxAgentFiles = 200;

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/worktrees/{id}/diff", async (string id, string? path, StateStore state, GitCli git, CancellationToken ct) =>
        {
            if (Root(state, id) is not { } root) return Results.NotFound(new { error = "unknown worktree" });
            string? rel = null;
            if (path is not null && !Validate(root, path, out rel, out _, out var bad)) return bad;
            var (diff, truncated) = await DiffAsync(git, root, rel, MaxDiffChars, ct);
            return Results.Ok(new DiffResponse(id, rel, diff, truncated));
        });

        api.MapGet("/worktrees/{id}/file", async (string id, string? path, StateStore state, CancellationToken ct) =>
        {
            if (Root(state, id) is not { } root) return Results.NotFound(new { error = "unknown worktree" });
            if (!Validate(root, path, out var rel, out var full, out var bad)) return bad;
            if (Directory.Exists(full)) return Results.BadRequest(new { error = "path is a directory" });
            if (!File.Exists(full)) return Results.NotFound(new { error = "file not found" });
            await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Re-check what was actually opened (defeats a symlink swapped in after validation).
            if (OpenedPath(fs) is { } opened && GitPaths.RealPath(root) is { } realRoot && !GitPaths.IsInside(realRoot, opened))
                return Results.BadRequest(new { error = "path escapes the worktree" });
            var buf = new byte[Math.Min(fs.Length, MaxFileBytes)];
            var n = await fs.ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false, ct);
            var bytes = buf.AsSpan(0, n);
            var binary = GitParsers.IsBinary(bytes);
            return Results.Ok(new FileResponse(id, rel, fs.Length, binary, fs.Length > n, binary ? null : Encoding.UTF8.GetString(bytes)));
        });

        api.MapGet("/worktrees/{id}/log", async (string id, string? path, int? limit, StateStore state, GitCli git, CancellationToken ct) =>
        {
            if (Root(state, id) is not { } root) return Results.NotFound(new { error = "unknown worktree" });
            string? rel = null;
            if (path is not null && !Validate(root, path, out rel, out _, out var bad)) return bad;
            List<string> args = ["log", "-n", Math.Clamp(limit ?? 50, 1, 500).ToString(), GitParsers.LogFormat, "--shortstat"];
            if (rel is not null) args.Add("--follow");
            args.AddRange(["HEAD", "--"]);
            if (rel is not null) args.Add(rel);
            var r = await git.RunAsync(root, args, ct);
            return Results.Ok(new LogResponse(id, rel, r.Ok ? GitParsers.ParseLog(r.Stdout) : []));
        });

        api.MapGet("/worktrees/{id}/blame", async (string id, string? path, StateStore state, GitCli git, CancellationToken ct) =>
        {
            if (Root(state, id) is not { } root) return Results.NotFound(new { error = "unknown worktree" });
            if (!Validate(root, path, out var rel, out var full, out var bad)) return bad;
            if (!File.Exists(full)) return Results.NotFound(new { error = "file not found" });
            var r = await git.RunAsync(root, ["blame", "--line-porcelain", "--", rel], ct, maxOutput: 8 * 1024 * 1024);
            if (!r.Ok && !r.Truncated) return Results.BadRequest(new { error = r.Stderr.Trim() });
            var lines = ParseBlame(r.Stdout, MaxBlameLines, out var capped);
            return Results.Ok(new BlameResponse(id, rel, lines, r.Truncated || capped));
        });

        api.MapGet("/agents/{agentId}/diff", async (string agentId, StateStore state, GitCli git, CancellationToken ct) =>
        {
            var files = state.Read(s => s.Files.Values
                .Where(f => f.Agents.ContainsKey(agentId) && f.GitStatus is not "clean"
                            && s.Worktrees.TryGetValue(f.WorktreeId, out var w) && !w.Removed)
                .OrderBy(f => f.WorktreeId).ThenBy(f => f.Path)
                .Select(f => (f.WorktreeId, Root: s.Worktrees[f.WorktreeId].Root, f.Path, f.GitStatus)).ToList());
            var result = new List<AgentFileDiff>();
            var budget = MaxDiffChars;
            var truncated = files.Count > MaxAgentFiles;
            foreach (var f in files.Take(MaxAgentFiles))
            {
                if (budget <= 0) { truncated = true; break; }
                if (!GitPaths.TryResolve(f.Root, f.Path, out var rel, out _, out _)) continue;
                var (diff, t) = await DiffAsync(git, f.Root, rel, budget, ct);
                budget -= diff.Length;
                truncated |= t;
                result.Add(new AgentFileDiff(f.WorktreeId, f.Root, rel, f.GitStatus, diff, t));
            }
            return Results.Ok(new AgentDiffResponse(agentId, result, truncated));
        });
    }

    private static string? Root(StateStore state, string id) =>
        state.Read(s => s.Worktrees.TryGetValue(id, out var w) && !w.Removed ? w.Root : null) is { } root && Directory.Exists(root) ? root : null;

    private static bool Validate(string root, string? path, out string rel, out string full, out IResult error)
    {
        var ok = GitPaths.TryResolve(root, path, out rel, out full, out var message);
        error = ok ? Results.Ok() : Results.BadRequest(new { error = message });
        return ok;
    }

    private static string? OpenedPath(FileStream fs)
    {
        if (OperatingSystem.IsMacOS()) return Amc.Daemon.Platform.MacFile.PathOf(fs.SafeFileHandle);
        if (!OperatingSystem.IsLinux()) return null;
        try { return new FileInfo($"/proc/self/fd/{fs.SafeFileHandle.DangerousGetHandle()}").LinkTarget; }
        catch (IOException) { return null; }
    }

    /// <summary>Unified diff vs HEAD (or the empty tree before the first commit) plus untracked files as new-file diffs.</summary>
    public static async Task<(string Diff, bool Truncated)> DiffAsync(GitCli git, string root, string? rel, int cap, CancellationToken ct)
    {
        var head = await git.RunAsync(root, ["rev-parse", "--verify", "-q", "HEAD^{commit}"], ct);
        var baseRev = head.Ok ? "HEAD" : (await git.RunAsync(root, ["hash-object", "-t", "tree", "/dev/null"], ct)).Stdout.Trim();
        List<string> args = ["diff", "--no-ext-diff", "--no-textconv", baseRev, "--"];
        if (rel is not null) args.Add(rel);
        var d = await git.RunAsync(root, args, ct, maxOutput: cap);
        var sb = new StringBuilder(d.Stdout);
        var truncated = d.Truncated;

        List<string> others = ["ls-files", "-z", "--others", "--exclude-standard", "--"];
        if (rel is not null) others.Add(rel);
        var o = await git.RunAsync(root, others, ct);
        var untracked = o.Ok ? o.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
        if (untracked.Length > MaxUntrackedInDiff) truncated = true;
        foreach (var file in untracked.Take(MaxUntrackedInDiff))
        {
            if (sb.Length >= cap) { truncated = true; break; }
            var nd = await git.RunAsync(root, ["diff", "--no-index", "--no-ext-diff", "--no-textconv", "--", "/dev/null", file], ct, maxOutput: cap - sb.Length);
            sb.Append(nd.Stdout);
            truncated |= nd.Truncated;
        }
        if (sb.Length > cap) { sb.Length = cap; truncated = true; }
        return (sb.ToString(), truncated);
    }

    public static List<BlameLine> ParseBlame(string porcelain, int maxLines, out bool capped)
    {
        var result = new List<BlameLine>();
        capped = false;
        string? sha = null, author = null, summary = null;
        DateTimeOffset? at = null;
        var line = 0;
        foreach (var l in porcelain.Split('\n'))
        {
            if (l.StartsWith('\t'))
            {
                if (result.Count >= maxLines) { capped = true; break; }
                result.Add(new BlameLine(line, sha ?? "", author, at, summary, l[1..]));
                continue;
            }
            var sp = l.IndexOf(' ');
            if (sp < 0) continue;
            var key = l[..sp];
            var value = l[(sp + 1)..];
            if (key.Length >= 40 && key.All(Uri.IsHexDigit))
            {
                sha = key;
                var parts = value.Split(' ');
                line = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : line + 1;
            }
            else if (key == "author") author = value;
            else if (key == "author-time" && long.TryParse(value, out var t)) at = DateTimeOffset.FromUnixTimeSeconds(t);
            else if (key == "summary") summary = value;
        }
        return result;
    }
}
