using System.Diagnostics;
using System.Text;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Collectors.Git;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Tools;

/// <summary>Config section "NewWorktree": where new worktrees go and an optional per-repository setup command.</summary>
public sealed class NewWorktreeSettings
{
    /// <summary>
    /// Folder that holds new worktrees, e.g. "~/worktrees/{repo}" (absolute or starting with "~"; "{repo}" is the
    /// repository's folder name). Empty: "&lt;repo-parent&gt;/&lt;repo&gt;.worktrees".
    /// </summary>
    public string? Root { get; set; }
    /// <summary>
    /// Per repository (key: repository folder name or main worktree path), a command run in a new worktree before the
    /// agent starts when the user ticks it, e.g. { "my-app": ["npm", "ci"] }. An argument list, never a shell line.
    /// </summary>
    public Dictionary<string, List<string>> SetupCommand { get; set; } = [];
}

/// <summary>What to create: a branch (new, or an existing one), the base for a new branch and an optional location.</summary>
public sealed record NewWorktreeSpec(string Branch, string? Base = null, string? Path = null, bool ExistingBranch = false,
    bool Fetch = false, bool RunSetup = false);

/// <summary>Body of POST /api/new-worktree (what `agentctl run --worktree` sends).</summary>
public sealed record CreateWorktreeRequest(string Cwd, string Branch, string? Base = null, string? Path = null,
    bool ExistingBranch = false, bool Fetch = false, string? TerminalId = null, string? Tool = null);

public sealed record NewWorktreePlan
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public int StatusCode { get; init; } = 200;
    public string? RepositoryId { get; init; }
    public string? RepoName { get; init; }
    public string? RepoRoot { get; init; }
    public string? CurrentBranch { get; init; }
    public string? DefaultBase { get; init; }
    public IReadOnlyList<string> LocalBranches { get; init; } = [];
    public IReadOnlyList<string> RemoteBranches { get; init; } = [];
    public string? Branch { get; init; }
    public string? BranchError { get; init; }
    public bool BranchExists { get; init; }
    public string? Base { get; init; }
    public string? BaseError { get; init; }
    public string? Path { get; init; }
    public string? PathError { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>The configured setup command for this repository, for display ("npm ci"), or null.</summary>
    public string? SetupCommand { get; init; }
    public IReadOnlyList<string>? SetupArgs { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] internal string? GitDir { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] internal string? CommonDir { get; init; }
}

public sealed record NewWorktreeResult(bool Ok, string? Error, int StatusCode, string? Path = null, string? WorktreeId = null,
    string? Branch = null, string? Base = null, bool NewBranch = false, IReadOnlyList<string>? Warnings = null,
    string? SetupCommand = null, IReadOnlyList<string>? SetupArgs = null)
{
    /// <summary>One line for audit/session details: "new worktree /x on branch b (new, from origin/main)".</summary>
    public string Describe() => $"new worktree {Path} on branch {Branch} ({(NewBranch ? $"new, from {Base}" : "existing branch")})";
}

public sealed record SetupResult(bool Ok, string Detail);

/// <summary>
/// Creates a git worktree to start a session in: validates the branch (<c>git check-ref-format --branch</c>), the base
/// and the location, optionally fetches the base (only when asked: Overhive does no network actions by itself), runs
/// <c>git worktree add</c> with an argument list (no shell), waits until discovery knows the worktree and marks it
/// <c>worktree.provisioned</c> (state: <see cref="Worktree.Origin"/>). It never deletes anything: when a later step fails
/// the worktree stays and the caller reports it.
/// </summary>
public sealed class NewWorktreeService(GitCli git, GitCollector collector, StateStore state, IEventSink sink,
    IOptions<AmcOptions> options, ILogger<NewWorktreeService> log)
{
    private const int MaxRefs = 1000;
    private static readonly TimeSpan AddTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);
    internal static TimeSpan SetupTimeout { get; set; } = TimeSpan.FromMinutes(15);

    private readonly AmcOptions _options = options.Value;

    /// <summary>
    /// Everything the dialog needs for a folder (branches, default base and location) plus the validation of what was
    /// typed so far. <paramref name="branch"/> may be empty (then only the lists are returned).
    /// </summary>
    public async Task<NewWorktreePlan> PlanAsync(string? cwd, string? branch, string? baseRef, string? path, bool existing, CancellationToken ct)
    {
        // Discover the folder's repository first (a CLI run may be the first time Overhive sees it), then apply the
        // same rule as any session start: inside the home directory, a known repository/worktree or a workspace root.
        if (string.IsNullOrWhiteSpace(cwd) || cwd.Contains('\0') || !PathText.IsAbsolute(cwd))
            return Fail("Working directory must be an absolute path", 400);
        if (Directory.Exists(cwd) && SessionLauncher.InsideAllowedRoot(GitPaths.RealPath(cwd) ?? cwd))
        {
            try { await collector.ResolveAsync(PathText.Normalize(GitPaths.RealPath(cwd) ?? cwd), ct); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { log.LogDebug(ex, "resolve {Cwd}", cwd); }
        }
        var dir = SessionLauncher.ValidateCwd(cwd, state, _options, out var why);
        if (dir is null) return Fail(why, 400);
        if (state.Resolver.Resolve(PathText.Normalize(dir)) is not { } hit || hit.Worktree.Removed)
            return Fail("The folder is not inside a git repository Overhive knows", 400);
        var wt = hit.Worktree;
        var repo = state.Read(s => s.Repositories.GetValueOrDefault(wt.RepositoryId));
        var mainRoot = repo?.MainWorktree ?? wt.Root;
        var repoName = repo?.Name ?? NewWorktreePaths.RepoName(mainRoot);
        var gitDir = Native(wt.Root);

        var refs = await git.RunAsync(gitDir, ["for-each-ref", "--sort=-committerdate", "--format=%(refname)", "refs/heads", "refs/remotes"], ct);
        var local = new List<string>();
        var remote = new List<string>();
        foreach (var line in refs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("refs/heads/", StringComparison.Ordinal)) { if (local.Count < MaxRefs) local.Add(line[11..]); }
            else if (line.StartsWith("refs/remotes/", StringComparison.Ordinal) && !line.EndsWith("/HEAD", StringComparison.Ordinal))
            { if (remote.Count < MaxRefs) remote.Add(line[13..]); }
        }
        var defaultBase = await DefaultBaseAsync(gitDir, wt, remote, ct);
        var (setupDisplay, setupArgs) = SetupFor(repoName, mainRoot);

        var plan = new NewWorktreePlan
        {
            Ok = true, RepositoryId = wt.RepositoryId, RepoName = repoName, RepoRoot = mainRoot, CurrentBranch = wt.Branch,
            DefaultBase = defaultBase, LocalBranches = local, RemoteBranches = remote, SetupCommand = setupDisplay, SetupArgs = setupArgs,
            GitDir = gitDir, CommonDir = repo?.CommonDir,
        };
        if (string.IsNullOrWhiteSpace(branch)) return plan;

        // Branch
        var warnings = new List<string>();
        var branchError = NewWorktreePaths.BranchPrecheck(branch);
        var exists = false;
        if (branchError is null)
        {
            var fmt = await git.RunAsync(gitDir, ["check-ref-format", "--branch", branch], ct);
            if (!fmt.Ok || fmt.Stdout.Trim() != branch) branchError = $"'{branch}' is not a valid branch name";
        }
        if (branchError is null)
        {
            exists = (await git.RunAsync(gitDir, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], ct)).Ok;
            if (!existing && exists) branchError = $"Branch '{branch}' already exists; choose another name or use the existing branch";
            else if (existing && !exists && !remote.Any(r => r.EndsWith("/" + branch, StringComparison.Ordinal)))
                branchError = $"There is no branch '{branch}'";
            else if (existing && state.Read(s => s.Worktrees.Values.FirstOrDefault(w => !w.Removed && w.RepositoryId == wt.RepositoryId && w.Branch == branch)?.Root) is { } usedBy)
                branchError = $"Branch '{branch}' is already checked out in {usedBy}";
        }

        // Base (new branches only)
        string? baseError = null;
        var baseUsed = existing ? null : string.IsNullOrWhiteSpace(baseRef) ? defaultBase : baseRef.Trim();
        if (!existing)
        {
            baseError = NewWorktreePaths.BasePrecheck(baseUsed);
            if (baseError is null && !(await git.RunAsync(gitDir, ["rev-parse", "--verify", "--quiet", "--end-of-options", baseUsed + "^{commit}"], ct)).Ok)
                baseError = $"'{baseUsed}' is not a branch or commit in this repository";
        }

        // Location
        string? target = null, pathError = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (path.Contains('\0') || !PathText.IsAbsolute(path.Trim())) pathError = "The location must be an absolute path";
            else target = PathText.Normalize(path.Trim());
        }
        else if (branchError is null || NewWorktreePaths.BranchPrecheck(branch) is null)
        {
            target = NewWorktreePaths.DefaultPath(mainRoot, branch, _options.NewWorktree.Root, AmcOptions.Home);
            if (target is null) pathError = "NewWorktree:Root in config.json must be an absolute path (or start with ~)";
        }
        if (target is not null && pathError is null) pathError = CheckLocation(target, repo?.CommonDir);
        if (target is not null && NewWorktreePaths.LongPathWarning(target) is { } lw) warnings.Add(lw);

        return plan with
        {
            Branch = branch, BranchError = branchError, BranchExists = exists, Base = baseUsed, BaseError = baseError,
            Path = target, PathError = pathError, Warnings = warnings,
        };
    }

    /// <summary>Validates, optionally fetches, runs <c>git worktree add</c>, waits for discovery and records the origin.</summary>
    public async Task<NewWorktreeResult> CreateAsync(CreateWorktreeRequest req, string via, CancellationToken ct)
    {
        if (!_options.EnableControl) return new(false, "Creating worktrees is disabled (EnableControl=false)", 403);
        if (string.IsNullOrWhiteSpace(req.Branch)) return new(false, "Enter a branch name", 400);
        var plan = await PlanAsync(req.Cwd, req.Branch, req.Base, req.Path, req.ExistingBranch, ct);
        if (!plan.Ok) return new(false, plan.Error, plan.StatusCode);
        var warnings = plan.Warnings.ToList();
        if (plan.BranchError is { } be) return new(false, be, 400);
        if (plan.PathError is { } pe) return new(false, pe, 400);

        if (!req.ExistingBranch && req.Fetch && plan.Base is { } fetchBase)
        {
            var remotes = (await git.RunAsync(plan.GitDir!, ["remote"], ct)).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var slash = fetchBase.IndexOf('/');
            var remote = slash > 0 ? fetchBase[..slash] : null;
            if (remote is not null && remotes.Contains(remote, StringComparer.Ordinal) && slash < fetchBase.Length - 1)
            {
                var f = await git.RunAsync(plan.GitDir!, ["fetch", "--no-tags", "--", remote, fetchBase[(slash + 1)..]], ct, timeout: FetchTimeout);
                if (!f.Ok) return new(false, $"git fetch {remote} failed: {FirstLines(f.Stderr)}", 502);
            }
            else warnings.Add($"'{fetchBase}' is not a remote-tracking branch; nothing was fetched");
            // Re-check the base after the fetch (a remote branch may only exist now).
            if (!(await git.RunAsync(plan.GitDir!, ["rev-parse", "--verify", "--quiet", "--end-of-options", fetchBase + "^{commit}"], ct)).Ok)
                return new(false, $"'{fetchBase}' is not a branch or commit in this repository", 400);
        }
        else if (plan.BaseError is { } baseErr) return new(false, baseErr, 400);

        var path = plan.Path!;
        List<string> args = req.ExistingBranch
            ? ["worktree", "add", "--", Native(path), req.Branch]
            : ["worktree", "add", "--no-track", "-b", req.Branch, "--", Native(path), plan.Base!];
        var add = await git.RunAsync(plan.GitDir!, args, ct, timeout: AddTimeout);
        if (!add.Ok)
        {
            var leftover = Directory.Exists(Native(path)) && Directory.EnumerateFileSystemEntries(Native(path)).Any()
                ? $" Nothing was removed: check {path}." : "";
            return new(false, $"git worktree add failed: {FirstLines(add.Stderr)}{leftover}", 409);
        }

        // Make discovery see it now (and wait until the reducer applied it), then mark it as ours.
        if (plan.CommonDir is { } commonDir) await collector.DiscoverRepositoryAsync(commonDir, ct);
        var real = GitPaths.RealPath(Native(path)) ?? path;
        var known = state.Resolver.Resolve(path) is { RelativePath: "" } r1 ? r1.Worktree
            : state.Resolver.Resolve(real) is { RelativePath: "" } r2 ? r2.Worktree : null;
        var root = known?.Root ?? PathText.Normalize(real);
        var wtId = known?.Id ?? Ids.Worktree(root);
        var newBranch = !req.ExistingBranch;
        var baseUsed = newBranch ? plan.Base : null;
        await sink.PublishAndWaitAsync(new AgentEvent
        {
            Type = EventTypes.WorktreeProvisioned, Source = EventSources.User, Evidence = Evidence.Observed,
            RepositoryId = plan.RepositoryId, WorktreeId = wtId,
            Data = Json.ToObject(new WorktreeProvisionedData(root, req.Branch, baseUsed, newBranch, via, req.TerminalId, req.Tool)),
        }, ct);
        var result = new NewWorktreeResult(true, null, 201, root, wtId, req.Branch, baseUsed, newBranch, warnings, plan.SetupCommand, plan.SetupArgs);
        await sink.PublishAsync(new AgentEvent
        {
            Type = EventTypes.AuditControl, Source = EventSources.User, Evidence = Evidence.Observed,
            RepositoryId = plan.RepositoryId, WorktreeId = wtId,
            Data = Json.ToObject(new AuditData("worktree.add", root, "ok", result.Describe() + $" via {via}")),
        }, ct);
        log.LogInformation("Created worktree {Path} (branch {Branch}, base {Base}) via {Via}", root, req.Branch, baseUsed ?? "-", via);
        return result;
    }

    /// <summary>
    /// Runs the repository's configured setup command (argument list, no shell) in the new worktree. Output is not
    /// streamed; the last lines are returned on failure.
    /// </summary>
    public async Task<SetupResult> RunSetupAsync(NewWorktreeResult wt, CancellationToken ct)
    {
        if (wt.SetupArgs is not { Count: > 0 } a) return new(false, "No setup command is configured for this repository (NewWorktree:SetupCommand)");
        var exe = ToolRegistry.FindExecutable(a[0]);
        if (exe is null) return new(false, $"Setup command `{a[0]}` was not found on PATH");
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Native(wt.Path!), UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in a.Skip(1)) psi.ArgumentList.Add(arg);
        psi.Environment.Remove("CLAUDECODE");
        var tail = new StringBuilder();
        void Keep(string? line)
        {
            if (line is null) return;
            lock (tail) { tail.AppendLine(line); if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000); }
        }
        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, $"Setup command could not be started: {ex.Message}");
        }
        proc.StandardInput.Close();
        proc.OutputDataReceived += (_, e) => Keep(e.Data);
        proc.ErrorDataReceived += (_, e) => Keep(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SetupTimeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return new(false, $"Setup command timed out after {SetupTimeout.TotalMinutes:0} minutes");
        }
        proc.WaitForExit(); // flush the async readers
        if (proc.ExitCode == 0) return new(true, $"setup `{wt.SetupCommand}` ok");
        string last;
        lock (tail) last = FirstLines(string.Join('\n', tail.ToString().Split('\n').TakeLast(8)), 8);
        return new(false, $"Setup command `{wt.SetupCommand}` exited with {proc.ExitCode}: {last}");
    }

    /// <summary>Null when <paramref name="target"/> may hold a new worktree, otherwise why not.</summary>
    internal string? CheckLocation(string target, string? commonDir)
    {
        var real = GitPaths.RealPath(Native(target));
        if (real is null) return "The location cannot be resolved";
        if (!SessionLauncher.InsideAllowedRoot(target) || !SessionLauncher.InsideAllowedRoot(real))
            return "The location must be inside your home directory";
        var roots = state.Read(s => s.Worktrees.Values.Where(w => !w.Removed).Select(w => w.Root).ToList());
        if (roots.FirstOrDefault(r => PathText.IsUnder(r, target) || PathText.IsUnder(r, real)) is { } inside)
            return $"The location is inside the worktree {inside}";
        if (roots.FirstOrDefault(r => PathText.IsUnder(target, r) || PathText.IsUnder(real, r)) is { } holds)
            return $"The location contains the worktree {holds}";
        if (commonDir is not null && (PathText.IsUnder(commonDir, target) || PathText.IsUnder(commonDir, real)))
            return "The location is inside the repository's .git directory";
        var native = Native(target);
        if (File.Exists(native)) return "A file exists at the location";
        try
        {
            if (Directory.Exists(native) && Directory.EnumerateFileSystemEntries(native).Any())
                return "The location exists and is not empty";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "The location is not accessible"; }
        return null;
    }

    private async Task<string> DefaultBaseAsync(string gitDir, Worktree wt, List<string> remote, CancellationToken ct)
    {
        var head = await git.RunAsync(gitDir, ["symbolic-ref", "--quiet", "refs/remotes/origin/HEAD"], ct);
        if (head.Ok && head.Stdout.Trim() is { } h && h.StartsWith("refs/remotes/", StringComparison.Ordinal) && remote.Contains(h[13..]))
            return h[13..];
        foreach (var guess in new[] { "origin/main", "origin/master" })
            if (remote.Contains(guess)) return guess;
        return wt.Branch ?? "HEAD";
    }

    private (string? Display, IReadOnlyList<string>? Args) SetupFor(string repoName, string mainRoot)
    {
        foreach (var (key, args) in _options.NewWorktree.SetupCommand)
        {
            if (args is not { Count: > 0 } || string.IsNullOrWhiteSpace(args[0])) continue;
            var match = key.Equals(repoName, StringComparison.Ordinal)
                || PathText.IsAbsolute(key) && PathText.Normalize(key).Equals(PathText.Normalize(mainRoot), PathText.Comparison);
            if (match) return (Display(args), args);
        }
        return (null, null);
    }

    /// <summary>Readable form of an argument list ("npm ci", quoting arguments with spaces).</summary>
    internal static string Display(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? a : "\"" + a.Replace("\"", "\\\"") + "\""));

    /// <summary>The OS form of a canonical path (backslashes on Windows) for the filesystem APIs and git arguments.</summary>
    internal static string Native(string path) => PathText.Windows ? path.Replace('/', '\\') : path;

    private static string FirstLines(string s, int n = 3) =>
        string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(n)) is { Length: > 0 } t
            ? (t.Length > 600 ? t[..600] + "…" : t) : "(no output)";

    private static NewWorktreePlan Fail(string error, int status) => new() { Ok = false, Error = error, StatusCode = status };
}
