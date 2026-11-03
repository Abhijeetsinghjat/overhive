using System.Text;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Collectors.Git;
using Amc.Daemon.Collectors.Processes;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Worktrees;

/// <summary>What a cleanup would remove and the result of every safety check, computed live (never from cached state).</summary>
public sealed record CleanupPlan(
    string WorktreeId, string Root, string? Branch, string? RemoteBranch, WorktreeMerge? Merged, string Mode,
    IReadOnlyList<CleanupCheck> Checks)
{
    public bool CanRemove => Checks.All(c => c.Ok);
}

public sealed record CleanupResult(bool Ok, string Outcome, string Detail, CleanupPlan? Plan, IReadOnlyList<CleanupStep> Steps, int StatusCode = 200);

/// <summary>
/// Removes a merged linked worktree: <c>git worktree remove</c> (never --force), then <c>git branch -d</c> (<c>-D</c>
/// only when gh confirmed the merged PR's head equals the local branch tip, i.e. a squash or rebase merge), then,
/// only if enabled, <c>git push origin --delete</c> when origin's branch tip equals the PR head. Every safety check is
/// re-run immediately before removal; any failing check blocks it. Every attempt is recorded as
/// <see cref="EventTypes.WorktreeCleaned"/> plus an <see cref="EventTypes.AuditControl"/> entry. Failures are reported,
/// never retried destructively. One cleanup runs at a time.
/// </summary>
public sealed class WorktreeCleaner(GitCli git, StateStore state, IEventSink sink, IProcSource? procs, IOptions<AmcOptions> options,
    GitCollector? collector, ILogger<WorktreeCleaner> log)
{
    public const string AuditAction = "cleanup-worktree";
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Last recorded blocked signature per worktree, so automatic attempts do not log the same block every minute.</summary>
    private readonly Dictionary<string, string> _lastAutoBlock = new(StringComparer.Ordinal);

    private WorktreeCleanupOptions Opt => options.Value.WorktreeCleanup;

    private sealed record Snapshot(Worktree Wt, Repository? Repo, List<string> Agents, List<string> Terminals);

    /// <summary>Plan with live safety checks; null for an unknown or already removed worktree.</summary>
    public async Task<CleanupPlan?> PlanAsync(string worktreeId, CancellationToken ct)
    {
        var snap = state.Read(s =>
        {
            if (!s.Worktrees.TryGetValue(worktreeId, out var w) || w.Removed) return null;
            var copy = Json.Deserialize<Worktree>(Json.Serialize(w))!;
            var repo = s.Repositories.TryGetValue(w.RepositoryId, out var r) ? new Repository { Id = r.Id, Name = r.Name, CommonDir = r.CommonDir, MainWorktree = r.MainWorktree, WebUrl = r.WebUrl } : null;
            var agents = s.Agents.Values
                .Where(a => !a.IsTerminal && (w.Agents.ContainsKey(a.Id) || a.CurrentWorktreeId == w.Id || a.Cwd is { } cwd && PathText.IsAbsolute(cwd) && PathText.IsUnder(w.Root, cwd)))
                .Select(a => $"{a.Title ?? a.Id} ({a.Status.ToString().ToLowerInvariant()})").ToList();
            var terminals = s.Terminals.Values.Where(t => t.Alive && t.Cwd is { } cwd && PathText.IsAbsolute(cwd) && PathText.IsUnder(w.Root, cwd))
                .Select(t => $"{t.Id} ({t.Command ?? "terminal"})").ToList();
            return new Snapshot(copy, repo, agents, terminals);
        });
        if (snap is null) return null;
        var w = snap.Wt;
        var m = w.Merged;
        var checks = new List<CleanupCheck>();
        void Add(string name, bool ok, string detail) => checks.Add(new CleanupCheck(name, ok, detail));

        Add("enabled", Opt.EffectiveMode != CleanupMode.Off && !Opt.Excludes(snap.Repo),
            Opt.EffectiveMode == CleanupMode.Off ? "worktree cleanup is off (WorktreeCleanup:Mode=off)"
            : Opt.Excludes(snap.Repo) ? "this repository is in WorktreeCleanup:ExcludeRepositories" : $"mode {Opt.EffectiveMode.ToString().ToLowerInvariant()}");
        Add("merged", m is not null, m is null ? "the branch is not detected as merged"
            : m.Pr is { } pr ? $"PR #{pr} merged (observed via gh)" : $"likely merged (inferred from git: {m.Reason})");

        // Live worktree list: main / locked / still registered.
        var commonDir = snap.Repo?.CommonDir;
        var list = commonDir is null ? null : await git.RunAsync(commonDir, ["worktree", "list", "--porcelain", "-z"], ct);
        var entries = list is { Ok: true } ? GitParsers.ParseWorktreeList(list.Stdout) : [];
        var idx = entries.FindIndex(e => PathText.Normalize(e.Root).Equals(PathText.Normalize(w.Root), PathText.Comparison));
        var entry = idx >= 0 ? entries[idx] : null;
        Add("registered", entry is not null && Directory.Exists(w.Root),
            entry is null ? "git does not list this worktree (already removed?)" : !Directory.Exists(w.Root) ? "the folder does not exist" : "listed by git worktree list");
        var isMain = w.IsMain || idx == 0;
        Add("not-main", !isMain, isMain ? "this is the repository's main worktree" : "linked worktree");
        Add("unlocked", entry is not { Locked: true } && !w.Locked, entry is { Locked: true } || w.Locked ? "the worktree is locked (git worktree lock)" : "not locked");

        var exists = entry is not null && Directory.Exists(w.Root);
        string? liveHead = null, liveBranch = null;
        if (exists)
        {
            var head = await git.RunAsync(w.Root, ["rev-parse", "--verify", "-q", "HEAD"], ct);
            liveHead = head.Ok ? head.Stdout.Trim() : null;
            var br = await git.RunAsync(w.Root, ["symbolic-ref", "-q", "--short", "HEAD"], ct);
            liveBranch = br.Ok ? br.Stdout.Trim() : null;
        }
        Add("head-unchanged", m is not null && liveHead == m.Head && liveBranch == m.Branch,
            m is null ? "no merged status to compare with"
            : liveBranch is null ? "HEAD is detached"
            : liveHead != m.Head || liveBranch != m.Branch ? $"HEAD moved since the merge was detected (now {liveBranch}@{Short(liveHead)})" : $"{liveBranch}@{Short(liveHead)}");

        // Uncommitted changes and untracked, non-ignored files.
        if (exists)
        {
            var st = await git.RunAsync(w.Root, ["status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=none"], ct);
            var n = st.Ok ? st.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length : -1;
            Add("clean", st.Ok && n == 0, !st.Ok ? "git status failed: " + st.Stderr.Trim() : n == 0 ? "no uncommitted changes, no untracked files"
                : $"{n} uncommitted change(s) or untracked file(s)");
            var gitDir = await git.RunAsync(w.Root, ["rev-parse", "--absolute-git-dir"], ct);
            var op = gitDir.Ok ? GitCollector.DetectOperation(gitDir.Stdout.Trim()) : null;
            Add("no-operation", op is null, op is null ? "no merge, rebase or similar in progress" : $"a {op} is in progress");
        }
        else Add("clean", false, "cannot check: the worktree is missing");

        // Unpushed work: every commit must be in the merged PR's head (gh) or on some remote branch (git).
        if (liveHead is null) Add("pushed", false, "no HEAD commit");
        else if (m is { Source: MergeSources.GitHub, PrHeadOid: { Length: > 0 } oid })
        {
            var contained = liveHead == oid || (await git.RunAsync(w.Root, ["merge-base", "--is-ancestor", liveHead, oid], ct)).ExitCode == 0;
            Add("pushed", contained, contained ? $"every commit is in PR #{m.Pr} (head {Short(oid)})" : $"HEAD {Short(liveHead)} has commits that are not in PR #{m.Pr}");
        }
        else
        {
            var rl = await git.RunAsync(w.Root, ["rev-list", "--count", liveHead, "--not", "--remotes"], ct);
            var n = rl.Ok && int.TryParse(rl.Stdout.Trim(), out var x) ? x : -1;
            Add("pushed", n == 0, n == 0 ? "every commit is on a remote branch" : n < 0 ? "could not compare with remote branches"
                : $"{n} commit(s) are on no remote branch (unpushed work{(liveBranch is null ? ", detached HEAD" : "")})");
        }

        Add("no-agents", snap.Agents.Count == 0, snap.Agents.Count == 0 ? "no active agent works here" : "active agent(s): " + string.Join(", ", snap.Agents.Take(5)));
        Add("no-terminals", snap.Terminals.Count == 0, snap.Terminals.Count == 0 ? "no open Overhive terminal here" : "open terminal(s): " + string.Join(", ", snap.Terminals.Take(5)));
        var users = ProcessesInside(w.Root);
        Add("no-processes", users is { Count: 0 }, users is null ? "process directories are not readable on this system"
            : users.Count == 0 ? "no process has its current directory inside" : "in use by: " + string.Join(", ", users.Take(5)));

        var remote = Opt.DeleteRemoteBranch && m is { Source: MergeSources.GitHub } && liveBranch is not null ? "origin/" + liveBranch : null;
        return new CleanupPlan(w.Id, w.Root, liveBranch ?? w.Branch, remote, m, Opt.EffectiveMode.ToString().ToLowerInvariant(), checks);
    }

    /// <summary>Processes (any, best effort: those whose cwd we may read) whose current directory is inside the root. Null when unsupported.</summary>
    private List<string>? ProcessesInside(string root)
    {
        if (procs is null) return null;
        var real = GitPaths.RealPath(root) ?? root;
        var self = Environment.ProcessId;
        var result = new List<string>();
        try
        {
            foreach (var pid in procs.ListPids())
            {
                if (pid == self) continue;
                var cwd = procs.ReadLink(pid, "cwd");
                if (cwd is null) continue;
                if (cwd.EndsWith(" (deleted)", StringComparison.Ordinal)) cwd = cwd[..^10];
                if (!PathText.IsAbsolute(cwd) || !(PathText.IsUnder(root, cwd) || PathText.IsUnder(real, cwd))) continue;
                var argv0 = procs.ReadCmdline(pid) is { Length: > 0 } c ? Encoding.UTF8.GetString(c).Split('\0')[0] : "?";
                result.Add($"pid {pid} ({Path.GetFileName(argv0.Replace('\\', '/'))})");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.LogDebug(ex, "Process scan for {Root} failed", root);
            return null;
        }
        return result;
    }

    private static string Short(string? sha) => sha is null ? "?" : sha[..Math.Min(7, sha.Length)];

    /// <param name="confirmed">Manual cleanups need it (428 otherwise).</param>
    /// <param name="dryRun">Only plan and check; nothing is run or recorded.</param>
    /// <param name="auto">Automatic cleanup (auto mode): needs no confirmation, recorded with source system.</param>
    public async Task<CleanupResult> CleanupAsync(string worktreeId, bool confirmed, bool dryRun, bool auto, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await CleanupCoreAsync(worktreeId, confirmed, dryRun, auto, ct); }
        finally { _gate.Release(); }
    }

    private async Task<CleanupResult> CleanupCoreAsync(string worktreeId, bool confirmed, bool dryRun, bool auto, CancellationToken ct)
    {
        if (Opt.EffectiveMode == CleanupMode.Off && !dryRun)
            return new(false, "disabled", "Worktree cleanup is off (WorktreeCleanup:Mode=off)", null, [], 409);
        var plan = await PlanAsync(worktreeId, ct);
        if (plan is null) return new(false, "unknown", "Unknown worktree (it may already be removed)", null, [], 404);
        if (dryRun)
            return new(plan.CanRemove, "dry-run", plan.CanRemove ? "Would remove " + Describe(plan) : "Would not remove: " + Blockers(plan), plan, []);
        if (!auto && !confirmed) return new(false, "unconfirmed", "Removing a worktree is destructive and requires confirm=true", plan, [], 428);

        if (!plan.CanRemove)
        {
            var detail = Blockers(plan);
            if (!auto || !_lastAutoBlock.TryGetValue(worktreeId, out var prev) || prev != detail)
            {
                if (auto) _lastAutoBlock[worktreeId] = detail;
                await RecordAsync(plan, CleanupOutcomes.Blocked, auto, [], detail, ct);
            }
            return new(false, CleanupOutcomes.Blocked, "Not removed: " + detail, plan, [], 409);
        }
        _lastAutoBlock.Remove(worktreeId);

        var repo = state.Read(s => s.Worktrees.TryGetValue(worktreeId, out var w) && s.Repositories.TryGetValue(w.RepositoryId, out var r) ? (r.CommonDir, r.MainWorktree) : default);
        // Never run from inside the worktree being removed.
        var runDir = repo.MainWorktree is { } mw && Directory.Exists(mw) && !PathText.IsUnder(plan.Root, mw) ? mw : repo.CommonDir;
        var steps = new List<CleanupStep>();

        var rm = await git.RunAsync(runDir, ["worktree", "remove", plan.Root], ct, mutating: true);
        steps.Add(Step("remove-worktree", $"git worktree remove {plan.Root}", rm));
        if (!rm.Ok)
        {
            var failed = "git worktree remove failed: " + Err(rm);
            await RecordAsync(plan, CleanupOutcomes.Failed, auto, steps, failed, ct);
            await RediscoverAsync(repo.CommonDir, ct);
            return new(false, CleanupOutcomes.Failed, failed, plan, steps, 500);
        }

        var branchOk = true;
        if (plan.Branch is { } branch)
        {
            var d = await git.RunAsync(runDir, ["branch", "-d", branch], ct, mutating: true);
            if (d.Ok) steps.Add(Step("delete-branch", $"git branch -d {branch}", d));
            else if (Err(d).Contains("not fully merged", StringComparison.OrdinalIgnoreCase)
                     && plan.Merged is { Source: MergeSources.GitHub, PrHeadOid: { Length: > 0 } oid })
            {
                var tip = await git.RunAsync(runDir, ["rev-parse", "--verify", "-q", "refs/heads/" + branch], ct);
                if (tip.Ok && tip.Stdout.Trim() == oid)
                {
                    var force = await git.RunAsync(runDir, ["branch", "-D", branch], ct, mutating: true);
                    steps.Add(Step("delete-branch", $"git branch -D {branch}", force,
                        force.Ok ? $"squash/rebase merge: the tip {Short(oid)} is the head of merged PR #{plan.Merged.Pr} (confirmed via gh)" : null));
                    branchOk = force.Ok;
                }
                else
                {
                    steps.Add(new CleanupStep("delete-branch", $"git branch -d {branch}", "failed",
                        $"{Err(d)}; kept because its tip is not the merged PR's head"));
                    branchOk = false;
                }
            }
            else
            {
                steps.Add(Step("delete-branch", $"git branch -d {branch}", d));
                branchOk = false;
            }
        }

        if (Opt.DeleteRemoteBranch && plan.Branch is { } rb)
        {
            if (plan.Merged is not { Source: MergeSources.GitHub, PrHeadOid: { Length: > 0 } oid })
                steps.Add(new CleanupStep("delete-remote-branch", $"git push origin --delete {rb}", "skipped", "only done for pull requests confirmed via gh"));
            else
            {
                var ls = await git.RunAsync(runDir, ["ls-remote", "--heads", "origin", "refs/heads/" + rb], ct, timeout: TimeSpan.FromSeconds(60));
                var remoteTip = ls.Ok ? ls.Stdout.Split('\t', '\n').FirstOrDefault()?.Trim() : null;
                if (!ls.Ok) { steps.Add(Step("delete-remote-branch", "git ls-remote --heads origin", ls)); branchOk = false; }
                else if (string.IsNullOrEmpty(remoteTip))
                    steps.Add(new CleanupStep("delete-remote-branch", $"git push origin --delete {rb}", "skipped", "origin no longer has this branch"));
                else if (remoteTip != oid)
                    steps.Add(new CleanupStep("delete-remote-branch", $"git push origin --delete {rb}", "skipped",
                        $"origin/{rb} is at {Short(remoteTip)}, not the merged PR head {Short(oid)}: left alone"));
                else
                {
                    var push = await git.RunAsync(runDir, ["push", "origin", "--delete", "refs/heads/" + rb], ct, timeout: TimeSpan.FromSeconds(60), mutating: true);
                    steps.Add(Step("delete-remote-branch", $"git push origin --delete {rb}", push));
                    branchOk &= push.Ok;
                }
            }
        }

        var outcome = branchOk ? CleanupOutcomes.Removed : CleanupOutcomes.Partial;
        var summary = branchOk ? "Removed " + Describe(plan) : "Worktree removed; " + string.Join("; ", steps.Where(s => s.Outcome == "failed").Select(s => $"{s.Name}: {s.Detail}"));
        await RecordAsync(plan, outcome, auto, steps, summary, ct);
        await RediscoverAsync(repo.CommonDir, ct);
        log.LogInformation("Worktree cleanup of {Root}: {Outcome}{Auto}", plan.Root, outcome, auto ? " (automatic)" : "");
        return new(branchOk, outcome, summary, plan, steps, branchOk ? 200 : 500);
    }

    private static string Describe(CleanupPlan p) =>
        $"{p.Root}" + (p.Branch is { } b ? $", local branch {b}" : "") + (p.RemoteBranch is { } r ? $", remote branch {r}" : "");

    public static string Blockers(CleanupPlan p) => string.Join("; ", p.Checks.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}"));

    private static string Err(GitResult r)
    {
        var s = r.TimedOut ? "timed out" : r.Stderr.Trim() is { Length: > 0 } e ? e : r.Stdout.Trim();
        return s.Length > 500 ? s[..500] : s;
    }

    private static CleanupStep Step(string name, string command, GitResult r, string? okDetail = null) =>
        new(name, command, r.Ok ? "ok" : "failed", r.Ok ? okDetail : Err(r));

    private async Task RecordAsync(CleanupPlan plan, string outcome, bool auto, List<CleanupStep> steps, string detail, CancellationToken ct)
    {
        var repoId = state.Read(s => s.Worktrees.GetValueOrDefault(plan.WorktreeId)?.RepositoryId);
        var source = auto ? EventSources.System : EventSources.User;
        await sink.PublishAndWaitAsync(new AgentEvent
        {
            Type = EventTypes.WorktreeCleaned, Source = source, Evidence = Evidence.Observed, RepositoryId = repoId, WorktreeId = plan.WorktreeId,
            Data = Json.ToObject(new WorktreeCleanedData(plan.Root, plan.Branch, outcome, auto, steps, plan.Checks, detail)),
        }, ct);
        await sink.PublishAsync(new AgentEvent
        {
            Type = EventTypes.AuditControl, Source = source, Evidence = Evidence.Observed, RepositoryId = repoId, WorktreeId = plan.WorktreeId,
            Data = Json.ToObject(new AuditData(AuditAction, plan.Root, outcome, auto ? $"Automatic (WorktreeCleanup:Mode=auto). {detail}" : detail)),
        }, ct);
    }

    private async Task RediscoverAsync(string? commonDir, CancellationToken ct)
    {
        if (collector is null || commonDir is null) return;
        try { await collector.DiscoverRepositoryAsync(commonDir, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Re-listing worktrees after cleanup failed"); }
    }
}
