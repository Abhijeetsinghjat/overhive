using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Amc.Core.Attention;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Api;
using Amc.Daemon.Collectors.Processes;
using Amc.Daemon.Worktrees;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Git;

/// <summary>Stands in for the gh CLI: answers `pr list` with the configured merged pull requests.</summary>
internal sealed class FakeGh : IGitHubCli
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public List<(int Number, string Branch, string Oid)> Merged { get; } = [];
    public bool Installed { get; set; } = true;

    public Task<GhResult> RunAsync(string workDir, IReadOnlyList<string> args, CancellationToken ct)
    {
        Calls.Enqueue(string.Join(' ', args));
        if (!Installed) return Task.FromResult(new GhResult(-1, "", "not found", NotInstalled: true));
        var head = args.SkipWhile(a => a != "--head").Skip(1).FirstOrDefault();
        var prs = new JsonArray(Merged.Where(m => head is null || m.Branch == head).Select(m => (JsonNode)new JsonObject
        {
            ["number"] = m.Number, ["headRefName"] = m.Branch, ["headRefOid"] = m.Oid,
            ["mergedAt"] = "2026-11-01T10:00:00Z", ["url"] = $"https://github.com/acme/repo/pull/{m.Number}",
        }).ToArray());
        return Task.FromResult(new GhResult(0, prs.ToJsonString(), ""));
    }
}

/// <summary>Process source with fixed working directories (pid → cwd).</summary>
internal sealed class FakeProcs : IProcSource
{
    public Dictionary<int, string> Cwds { get; } = [];
    public IEnumerable<int> ListPids() => Cwds.Keys.ToList();
    public string? ReadStat(int pid) => null;
    public byte[]? ReadCmdline(int pid) => "vim\0file\0"u8.ToArray();
    public string? ReadStatus(int pid) => null;
    public string? ReadLink(int pid, string name) => name == "cwd" ? Cwds.GetValueOrDefault(pid) : null;
    public byte[]? ReadEnviron(int pid) => null;
    public long ClockTicksPerSecond => 100;
    public DateTimeOffset BootTime => DateTimeOffset.UnixEpoch;
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class WorktreeCleanupTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Branch = "feat/x";

    private sealed record Setup(GitTestEnv Env, string Main, string Wt, string WtId, FakeGh Gh, FakeProcs Procs, TestClock Clock,
        MergeDetector Detector, WorktreeCleaner Cleaner, WorktreeCleanupService Service) : IAsyncDisposable
    {
        public WorktreeCleanupOptions Opt => Env.Options.WorktreeCleanup;
        public Worktree W => Env.State.Read(s => Json.Deserialize<Worktree>(Json.Serialize(s.Worktrees[WtId]))!);
        public string Head => GitTestEnv.Head(Wt);
        public bool BranchExists(string b = Branch) => GitTestEnv.RunGit(Main, "branch", "--list", b).Out.Trim().Length > 0;
        public bool RemoteBranchExists(string b = Branch) => GitTestEnv.RunGit(Main, "ls-remote", "--heads", "origin", "refs/heads/" + b).Out.Trim().Length > 0;

        /// <summary>gh reports the PR of feat/x merged with the given head (default: the worktree's HEAD).</summary>
        public async Task MergedViaGhAsync(string? oid = null)
        {
            Gh.Merged.Add((7, Branch, oid ?? Head));
            await Detector.CheckAsync(force: true, None);
            await Env.Applied();
        }

        public async Task RefreshAsync()
        {
            await Env.Collector.RefreshAsync(WtId, None);
            await Env.Applied();
        }

        public ValueTask DisposeAsync() => Env.DisposeAsync();
    }

    private static void Git(string dir, params string[] args)
    {
        var r = GitTestEnv.RunGit(dir, args);
        Assert.True(r.Code == 0, $"git {string.Join(' ', args)} failed: {r.Out}");
    }

    /// <summary>
    /// ws/repo (main, pushed) with a GitHub origin that git rewrites to a local bare repository, and a linked worktree
    /// ws/wt-x on feat/x with one commit, pushed with upstream.
    /// </summary>
    private static async Task<Setup> CreateAsync(string mode = "suggest", bool useRealProcs = false)
    {
        var env = await GitTestEnv.CreateAsync();
        env.Options.WorktreeCleanup.Mode = mode;
        var origin = Path.Combine(env.Dir, "origin.git");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-q", "--bare", "-b", "main");
        var main = env.InitRepo("ws/repo", ("src/a.txt", "a\n"));
        Git(main, "remote", "add", "origin", "https://github.com/acme/repo.git");
        Git(main, "config", $"url.{PathText.Normalize(origin)}.insteadOf", "https://github.com/acme/repo.git");
        Git(main, "push", "-q", "-u", "origin", "main");
        var wt = Path.Combine(env.Dir, "ws", "wt-x");
        Git(main, "worktree", "add", "-q", "-b", Branch, wt);
        GitTestEnv.Write(wt, "src/x.txt", "x\n");
        Git(wt, "add", "-A");
        Git(wt, "commit", "-q", "-m", "feature x");
        Git(wt, "push", "-q", "-u", "origin", Branch);
        var wtId = await env.DiscoverAsync(wt);

        var gh = new FakeGh();
        var procs = new FakeProcs();
        var clock = new TestClock();
        var opts = Options.Create(env.Options);
        var detector = new MergeDetector(env.Git, gh, env.State, env.Sink, opts, clock, NullLogger<MergeDetector>.Instance);
        var cleaner = new WorktreeCleaner(env.Git, env.State, env.Sink, useRealProcs ? ProcSources.Default : procs, opts, env.Collector,
            NullLogger<WorktreeCleaner>.Instance);
        var service = new WorktreeCleanupService(detector, cleaner, env.State, opts, env.Health, clock, NullLogger<WorktreeCleanupService>.Instance);
        return new Setup(env, main, PathText.Normalize(wt), wtId, gh, procs, clock, detector, cleaner, service);
    }

    [Fact]
    public async Task Merged_pr_observed_via_gh_is_cleaned_up_worktree_and_local_branch_removed()
    {
        await using var t = await CreateAsync(useRealProcs: true);
        await t.MergedViaGhAsync();
        var m = t.W.Merged;
        Assert.NotNull(m);
        Assert.Equal((MergeSources.GitHub, 7, Evidence.Observed, false), (m.Source, m.Pr, m.Evidence, m.Likely));
        Assert.Equal("https://github.com/acme/repo/pull/7", m.Url);
        var ev = Assert.Single(t.Env.Sink.OfType(EventTypes.WorktreeMerged));
        Assert.Equal(Evidence.Observed, ev.Evidence);

        // Suggest mode: a low-priority "needs you" item that opens the worktree.
        var item = Assert.Single(t.Env.State.Read(s => new MergedWorktreeAttentionProvider().Collect(s, new AttentionContext(DateTimeOffset.UtcNow, AttentionSettings.Default)).ToList()));
        Assert.Equal((AttentionPriority.Low, AttentionActions.OpenWorktree, t.WtId), (item.Priority, item.Action.Kind, item.Action.Target));

        var plan = await t.Cleaner.PlanAsync(t.WtId, None);
        Assert.True(plan!.CanRemove, WorktreeCleaner.Blockers(plan));
        Assert.Null(plan.RemoteBranch);

        var r = await t.Cleaner.CleanupAsync(t.WtId, confirmed: true, dryRun: false, auto: false, None);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal(CleanupOutcomes.Removed, r.Outcome);
        Assert.Equal(["remove-worktree", "delete-branch"], r.Steps.Select(s => s.Name));
        Assert.Contains("git branch -d", r.Steps[1].Command);
        Assert.False(Directory.Exists(t.Wt));
        Assert.False(t.BranchExists());
        Assert.True(t.RemoteBranchExists()); // remote deletion is off by default
        await t.Env.Applied();
        var cleaned = Assert.Single(t.Env.Sink.OfType(EventTypes.WorktreeCleaned)).Payload<WorktreeCleanedData>()!;
        Assert.Equal((CleanupOutcomes.Removed, false), (cleaned.Outcome, cleaned.Auto));
        Assert.Contains(t.Env.Sink.OfType(EventTypes.AuditControl), e => e.Payload<AuditData>()!.Action == WorktreeCleaner.AuditAction);
        Assert.True(t.W.Removed); // the collector re-listed the repository
    }

    [Fact]
    public async Task Gh_is_not_called_without_candidates_nor_more_often_than_the_poll_interval()
    {
        await using var t = await CreateAsync();
        Git(t.Main, "worktree", "remove", t.Wt);
        await t.Env.Collector.DiscoverRepositoryAsync(t.Env.State.Read(s => s.Repositories.Values.Single().CommonDir), None);
        await t.Env.Applied();
        await t.Detector.CheckAsync(force: false, None);
        Assert.Empty(t.Gh.Calls);

        await using var u = await CreateAsync();
        await u.Detector.CheckAsync(force: false, None);
        var n = u.Gh.Calls.Count;
        Assert.True(n >= 1);
        await u.Detector.CheckAsync(force: false, None);
        Assert.Equal(n, u.Gh.Calls.Count);
        u.Clock.Now += TimeSpan.FromMinutes(11);
        await u.Detector.CheckAsync(force: false, None);
        Assert.True(u.Gh.Calls.Count > n);
    }

    [Fact]
    public async Task Reused_branch_with_new_commits_is_not_merged()
    {
        await using var t = await CreateAsync();
        var prHead = t.Head;
        await t.MergedViaGhAsync(prHead);
        Assert.NotNull(t.W.Merged);

        // New work on the same branch after the merge: the merged status no longer holds ...
        GitTestEnv.Write(t.Wt, "src/more.txt", "more\n");
        Git(t.Wt, "add", "-A");
        Git(t.Wt, "commit", "-q", "-m", "more work");
        await t.RefreshAsync();
        Assert.Null(t.W.Merged);
        // ... and is not detected again from the old PR (HEAD is not contained in the PR head).
        await t.Detector.CheckAsync(force: true, None);
        t.Clock.Now += TimeSpan.FromMinutes(1);
        await t.Detector.CheckAsync(force: true, None);
        await t.Env.Applied();
        Assert.Null(t.W.Merged);
        var r = await t.Cleaner.CleanupAsync(t.WtId, confirmed: true, dryRun: false, auto: false, None);
        Assert.Equal(CleanupOutcomes.Blocked, r.Outcome);
        Assert.True(Directory.Exists(t.Wt));
    }

    /// <summary>Squash merge on main, remote branch deleted and pruned: git branch -d refuses, -D is used because the tip is the PR head.</summary>
    [Fact]
    public async Task Squash_merge_uses_branch_D_only_when_the_tip_is_the_merged_pr_head()
    {
        await using var t = await CreateAsync();
        SquashMergeAndDeleteRemote(t);
        await t.MergedViaGhAsync();
        var r = await t.Cleaner.CleanupAsync(t.WtId, confirmed: true, dryRun: false, auto: false, None);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal("git branch -D feat/x", r.Steps[1].Command);
        Assert.False(t.BranchExists());

        // Local tip behind the PR head (PR got one more commit): merged, but the branch is kept (not fully merged, tip ≠ PR head).
        await using var u = await CreateAsync();
        GitTestEnv.Write(u.Wt, "src/y.txt", "y\n");
        Git(u.Wt, "add", "-A");
        Git(u.Wt, "commit", "-q", "-m", "review fix");
        var prHead = u.Head;
        Git(u.Wt, "reset", "-q", "--hard", "HEAD~1");
        await u.RefreshAsync();
        SquashMergeAndDeleteRemote(u);
        await u.MergedViaGhAsync(prHead);
        Assert.NotNull(u.W.Merged);
        var p = await u.Cleaner.CleanupAsync(u.WtId, confirmed: true, dryRun: false, auto: false, None);
        Assert.Equal(CleanupOutcomes.Partial, p.Outcome);
        Assert.Equal("failed", p.Steps[1].Outcome);
        Assert.Contains("kept", p.Steps[1].Detail);
        Assert.False(Directory.Exists(u.Wt));
        Assert.True(u.BranchExists());
    }

    private static void SquashMergeAndDeleteRemote(Setup t)
    {
        Git(t.Main, "merge", "-q", "--squash", Branch);
        Git(t.Main, "commit", "-q", "-m", "feature x (#7)");
        Git(t.Main, "push", "-q", "origin", "main");
        Git(t.Main, "push", "-q", "origin", "--delete", Branch);
        Git(t.Main, "fetch", "-q", "--prune", "origin");
    }

    [Fact]
    public async Task Without_gh_git_infers_likely_merged_but_not_for_a_fresh_branch()
    {
        await using var t = await CreateAsync();
        t.Gh.Installed = false;
        var fresh = Path.Combine(t.Env.Dir, "ws", "wt-fresh");
        Git(t.Main, "worktree", "add", "-q", "-b", "fix/fresh", fresh);
        var freshId = await t.Env.DiscoverAsync(fresh);
        Git(t.Main, "merge", "-q", "--no-ff", "-m", "Merge feat/x", Branch);
        Git(t.Main, "push", "-q", "origin", "main");

        await t.Detector.CheckAsync(force: true, None);
        await t.Env.Applied();
        var m = t.W.Merged;
        Assert.NotNull(m);
        Assert.Equal((MergeSources.Git, Evidence.Inferred, true, (int?)null), (m.Source, m.Evidence, m.Likely, m.Pr));
        Assert.Contains("origin/main", m.Reason);
        Assert.Null(t.Env.State.Read(s => s.Worktrees[freshId].Merged));
        Assert.Equal(Evidence.Inferred, Assert.Single(t.Env.Sink.OfType(EventTypes.WorktreeMerged)).Evidence);

        // Upstream gone and every commit in origin/main by patch id (cherry-picked): likely merged too.
        var z = Path.Combine(t.Env.Dir, "ws", "wt-z");
        Git(t.Main, "worktree", "add", "-q", "-b", "feat/z", z);
        GitTestEnv.Write(z, "src/z.txt", "z\n");
        Git(z, "add", "-A");
        Git(z, "commit", "-q", "-m", "feature z");
        Git(z, "push", "-q", "-u", "origin", "feat/z");
        var zId = await t.Env.DiscoverAsync(z);
        Git(t.Main, "cherry-pick", "-x", GitTestEnv.Head(z)); // -x: a different commit with the same patch
        Git(t.Main, "push", "-q", "origin", "main");
        Git(t.Main, "push", "-q", "origin", "--delete", "feat/z");
        Git(t.Main, "fetch", "-q", "--prune", "origin");
        await t.Detector.CheckAsync(force: true, None);
        await t.Env.Applied();
        Assert.Contains("gone", t.Env.State.Read(s => s.Worktrees[zId].Merged?.Reason));

        // A likely-merged worktree is cleaned with plain git branch -d.
        var r = await t.Cleaner.CleanupAsync(t.WtId, confirmed: true, dryRun: false, auto: false, None);
        Assert.True(r.Ok, r.Detail);
        Assert.False(t.BranchExists());
    }

    [Theory]
    [InlineData("locked", "unlocked")]
    [InlineData("modified", "clean")]
    [InlineData("untracked", "clean")]
    [InlineData("unpushed", "pushed")]
    [InlineData("agent", "no-agents")]
    [InlineData("terminal", "no-terminals")]
    [InlineData("process", "no-processes")]
    [InlineData("detached", "head-unchanged")]
    [InlineData("excluded", "enabled")]
    public async Task Every_safety_guard_blocks_removal(string situation, string failingCheck)
    {
        await using var t = await CreateAsync();
        await t.MergedViaGhAsync();
        var sink = t.Env.Sink;
        switch (situation)
        {
            case "locked": Git(t.Main, "worktree", "lock", t.Wt); break;
            // Not refreshed into state on purpose: the checks run live, not from cached state.
            case "modified": GitTestEnv.Write(t.Wt, "src/x.txt", "changed\n"); break;
            case "untracked": GitTestEnv.Write(t.Wt, "notes.md", "draft\n"); break;
            case "unpushed":
                GitTestEnv.Write(t.Wt, "src/x2.txt", "x2\n");
                Git(t.Wt, "add", "-A");
                Git(t.Wt, "commit", "-q", "-m", "unpushed");
                break;
            case "agent":
                await sink.PublishAndWaitAsync(new AgentEvent
                {
                    Type = EventTypes.AgentCreated, Source = EventSources.Hook, AgentId = "fake:a1", SessionId = "a1",
                    Data = Json.ToObject(new AgentCreatedData(AgentKind.Main, "fake", Title: "Refactor", Cwd: t.Wt)),
                });
                break;
            case "terminal":
                await sink.PublishAndWaitAsync(new AgentEvent
                {
                    Type = EventTypes.TerminalStarted, Source = EventSources.Pty,
                    Data = Json.ToObject(new TerminalData("t1", 4242, "bash", t.Wt + "/src")),
                });
                break;
            case "process": t.Procs.Cwds[4242] = t.Wt + "/src"; break;
            case "detached": Git(t.Wt, "checkout", "-q", "--detach"); break;
            case "excluded": t.Opt.ExcludeRepositories.Add("repo"); break;
        }
        var r = await t.Cleaner.CleanupAsync(t.WtId, confirmed: true, dryRun: false, auto: false, None);
        Assert.Equal((CleanupOutcomes.Blocked, 409), (r.Outcome, r.StatusCode));
        Assert.Contains(r.Plan!.Checks, c => c.Name == failingCheck && !c.Ok);
        Assert.Empty(r.Steps);
        Assert.True(Directory.Exists(t.Wt));
        Assert.True(t.BranchExists());
        await t.Env.Applied();
        Assert.Equal(CleanupOutcomes.Blocked, Assert.Single(sink.OfType(EventTypes.WorktreeCleaned)).Payload<WorktreeCleanedData>()!.Outcome);
    }

    [Fact]
    public async Task Main_worktree_is_never_removed()
    {
        await using var t = await CreateAsync();
        var mainId = await t.Env.DiscoverAsync(t.Main);
        var plan = await t.Cleaner.PlanAsync(mainId, None);
        Assert.Contains(plan!.Checks, c => c.Name == "not-main" && !c.Ok);
        var r = await t.Cleaner.CleanupAsync(mainId, confirmed: true, dryRun: false, auto: false, None);
        Assert.Equal(CleanupOutcomes.Blocked, r.Outcome);
        Assert.True(Directory.Exists(t.Main));
    }

    [Fact]
    public async Task Dry_run_and_unconfirmed_requests_change_nothing()
    {
        await using var t = await CreateAsync();
        await t.MergedViaGhAsync();
        var dry = await t.Cleaner.CleanupAsync(t.WtId, confirmed: true, dryRun: true, auto: false, None);
        Assert.True(dry.Ok, dry.Detail);
        Assert.Equal("dry-run", dry.Outcome);
        Assert.StartsWith("Would remove", dry.Detail);
        var unconfirmed = await t.Cleaner.CleanupAsync(t.WtId, confirmed: false, dryRun: false, auto: false, None);
        Assert.Equal(428, unconfirmed.StatusCode);
        Assert.True(Directory.Exists(t.Wt));
        Assert.True(t.BranchExists());
        await t.Env.Applied();
        Assert.Empty(t.Env.Sink.OfType(EventTypes.WorktreeCleaned));
    }

    [Fact]
    public async Task Auto_mode_removes_only_after_the_grace_period()
    {
        await using var t = await CreateAsync(mode: "auto");
        await t.MergedViaGhAsync();
        t.Clock.Now = t.W.Merged!.DetectedAt + TimeSpan.FromMinutes(29);
        Assert.Empty(await t.Service.RunAutoAsync(None));
        Assert.True(Directory.Exists(t.Wt));

        t.Clock.Now += TimeSpan.FromMinutes(2);
        var r = Assert.Single(await t.Service.RunAutoAsync(None));
        Assert.True(r.Ok, r.Detail);
        Assert.False(Directory.Exists(t.Wt));
        await t.Env.Applied();
        var ev = Assert.Single(t.Env.Sink.OfType(EventTypes.WorktreeCleaned));
        Assert.Equal(EventSources.System, ev.Source);
        Assert.True(ev.Payload<WorktreeCleanedData>()!.Auto);
    }

    [Fact]
    public async Task Remote_branch_is_deleted_only_when_enabled_and_its_tip_is_the_pr_head()
    {
        await using var t = await CreateAsync();
        t.Opt.DeleteRemoteBranch = true;
        await t.MergedViaGhAsync();
        var plan = await t.Cleaner.PlanAsync(t.WtId, None);
        Assert.Equal("origin/feat/x", plan!.RemoteBranch);
        var r = await t.Cleaner.CleanupAsync(t.WtId, confirmed: true, dryRun: false, auto: false, None);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal("ok", r.Steps.Single(s => s.Name == "delete-remote-branch").Outcome);
        Assert.False(t.RemoteBranchExists());
    }

    [Fact]
    public async Task Api_requires_token_confirmation_and_control_permission()
    {
        await using var t = await CreateAsync();
        await t.MergedViaGhAsync();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new ApiToken("secret"));
        builder.Services.AddSingleton(Options.Create(t.Env.Options));
        builder.Services.AddSingleton(t.Cleaner);
        builder.Services.AddSingleton(t.Detector);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
            foreach (var c in Json.Options.Converters) o.SerializerOptions.Converters.Add(c);
        });
        await using var app = builder.Build();
        app.UseMiddleware<SecurityMiddleware>();
        app.MapWorktreeCleanupEndpoints();
        await app.StartAsync();
        var http = app.GetTestClient();
        var url = $"/api/worktrees/{t.WtId}/cleanup";

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync(url, new { confirm = true })).StatusCode);
        http.DefaultRequestHeaders.Add(ApiToken.Header, "secret");
        using (var cross = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { confirm = true }) })
        {
            cross.Headers.Add("Origin", "https://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(cross)).StatusCode);
        }
        var plan = await http.GetFromJsonAsync<JsonObject>(url);
        Assert.True(plan!["checks"]!.AsArray().Count > 5);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/worktrees/wt:nope/cleanup")).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await http.PostAsJsonAsync(url, new { })).StatusCode);

        t.Env.Options.EnableControl = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(url, new { confirm = true })).StatusCode);
        t.Env.Options.EnableControl = true;
        t.Opt.Mode = "off";
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync(url, new { confirm = true })).StatusCode);
        Assert.True(Directory.Exists(t.Wt));
        t.Opt.Mode = "suggest";

        var ok = await http.PostAsJsonAsync(url, new { confirm = true });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = (await ok.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("removed", body["outcome"]!.ToString());
        Assert.Equal(2, body["steps"]!.AsArray().Count);
        Assert.False(Directory.Exists(t.Wt));
    }
}
