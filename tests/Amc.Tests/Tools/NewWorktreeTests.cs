using System.Net;
using System.Net.Http.Json;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Api;
using Amc.Daemon.Collectors.Git;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Tools;
using Amc.Tests.Git;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Tools;

/// <summary>"+ New session" in a new git worktree, against real temporary repositories and the real event pipeline.</summary>
public sealed class NewWorktreeTests
{
    /// <summary>Stands in for both the tool and agentctl: a real executable that starts (and exits) on every OS.</summary>
    private static readonly string GitExe = ToolRegistry.FindExecutable("git") ?? throw new InvalidOperationException("git is required");

    private sealed class Rig : IAsyncDisposable
    {
        public required GitTestEnv Env { get; init; }
        public required NewWorktreeService Service { get; init; }
        public required SessionLauncher Launcher { get; init; }
        public required string Repo { get; init; }
        public ValueTask DisposeAsync() => Env.DisposeAsync();

        /// <summary>The repository's folder in OS form (what the dialog and the CLI send).</summary>
        public string RepoNative => NewWorktreeService.Native(Repo);
        public string Ws => PathText.Normalize(Path.Combine(Env.Dir, "ws"));
    }

    private static async Task<Rig> CreateAsync(Action<AmcOptions>? configure = null)
    {
        var env = await GitTestEnv.CreateAsync();
        env.Options.Tools = [new AgentTool { Id = "faketool", Name = "Fake tool", Command = GitExe }];
        configure?.Invoke(env.Options);
        var repo = env.InitRepo("ws/app", ("src/a.txt", "one\n"));
        var opts = Options.Create(env.Options);
        var svc = new NewWorktreeService(env.Git, env.Collector, env.State, env.Sink, opts, NullLogger<NewWorktreeService>.Instance);
        var launcher = new SessionLauncher(new ToolRegistry(opts), env.State, env.Sink, opts, NullLogger<SessionLauncher>.Instance, svc)
        {
            AgentctlOverride = GitExe,
        };
        return new Rig { Env = env, Service = svc, Launcher = launcher, Repo = repo };
    }

    private static string Branch(string dir) => GitTestEnv.RunGit(dir, "rev-parse", "--abbrev-ref", "HEAD").Out.Trim();

    [Fact]
    public async Task Creates_the_worktree_on_a_new_branch_from_the_base_and_starts_the_tool_there()
    {
        await using var rig = await CreateAsync();
        var mainHead = GitTestEnv.Head(rig.Repo);
        GitTestEnv.RunGit(rig.Repo, "checkout", "-q", "-b", "other");
        GitTestEnv.Write(rig.Repo, "other.txt", "x\n");
        GitTestEnv.RunGit(rig.Repo, "add", "-A");
        GitTestEnv.RunGit(rig.Repo, "commit", "-q", "-m", "other");

        var r = await rig.Launcher.LaunchAsync(new LaunchRequest(rig.RepoNative, Tool: "faketool",
            Worktree: new NewWorktreeSpec("feat/login", Base: "main")), CancellationToken.None);

        Assert.True(r.Ok, r.Detail);
        Assert.Equal(202, r.StatusCode);
        var expected = rig.Ws + "/app.worktrees/feat-login";
        Assert.Equal(expected, r.WorktreePath);
        var native = NewWorktreeService.Native(expected);
        Assert.True(File.Exists(Path.Combine(native, "src", "a.txt")));
        Assert.False(File.Exists(Path.Combine(native, "other.txt")));      // from main, not from the folder's current branch
        Assert.Equal("feat/login", Branch(native));
        Assert.Equal(mainHead, GitTestEnv.Head(native));
        Assert.NotEqual(0, GitTestEnv.RunGit(native, "rev-parse", "--abbrev-ref", "feat/login@{upstream}").Code); // --no-track

        await rig.Env.Applied();
        var wt = rig.Env.State.Read(s => s.Worktrees[r.WorktreeId!]);
        Assert.Equal(expected, wt.Root, ignoreCase: PathText.Windows);
        Assert.NotNull(wt.Origin);
        Assert.Equal(("dashboard", "feat/login", "main", true, r.TerminalId, "faketool"),
            (wt.Origin!.Via, wt.Origin.Branch, wt.Origin.Base, wt.Origin.NewBranch, wt.Origin.TerminalId, wt.Origin.Tool));
        var launch = rig.Env.Sink.OfType(EventTypes.AuditControl).Select(e => e.Payload<AuditData>()!).Single(a => a.Action == "launch");
        Assert.Equal("ok", launch.Outcome);
        Assert.Contains("new worktree", launch.Detail);
        Assert.Contains("feat/login", launch.Detail);
        Assert.Contains("from main", launch.Detail);
        Assert.Single(rig.Env.Sink.OfType(EventTypes.WorktreeProvisioned));
    }

    [Fact]
    public async Task Checks_out_an_existing_branch_without_a_base()
    {
        await using var rig = await CreateAsync();
        GitTestEnv.RunGit(rig.Repo, "branch", "topic");

        var r = await rig.Service.CreateAsync(new CreateWorktreeRequest(rig.RepoNative, "topic", ExistingBranch: true, TerminalId: "T1", Tool: "claude"), "cli", CancellationToken.None);

        Assert.True(r.Ok, r.Error);
        Assert.False(r.NewBranch);
        Assert.Null(r.Base);
        Assert.Equal("topic", Branch(NewWorktreeService.Native(r.Path!)));
        var origin = rig.Env.State.Read(s => s.Worktrees[r.WorktreeId!].Origin);
        Assert.Equal(("cli", false, "T1"), (origin!.Via, origin.NewBranch, origin.TerminalId));

        // A branch checked out elsewhere, or one that does not exist, is refused.
        var busy = await rig.Service.CreateAsync(new CreateWorktreeRequest(rig.RepoNative, "main", ExistingBranch: true), "cli", CancellationToken.None);
        Assert.Equal(400, busy.StatusCode);
        Assert.Contains("already checked out", busy.Error);
        var missing = await rig.Service.CreateAsync(new CreateWorktreeRequest(rig.RepoNative, "nope", ExistingBranch: true), "cli", CancellationToken.None);
        Assert.Equal(400, missing.StatusCode);
    }

    [Theory]
    [InlineData("bad..name")]
    [InlineData("-x")]
    [InlineData("has space")]
    [InlineData("@{-1}")]
    [InlineData("x.lock")]
    [InlineData("feat/")]
    [InlineData("a~b")]
    [InlineData("HEAD")]
    [InlineData("main")] // exists already (and "use existing branch" was not chosen)
    public async Task Invalid_or_existing_branch_names_are_rejected_before_anything_is_created(string name)
    {
        await using var rig = await CreateAsync();
        var r = await rig.Service.CreateAsync(new CreateWorktreeRequest(rig.RepoNative, name), "cli", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(rig.Env.Dir, "ws", "app.worktrees")));
        Assert.Single(GitTestEnv.RunGit(rig.Repo, "worktree", "list", "--porcelain").Out.Split('\n').Where(l => l.StartsWith("worktree ")));
    }

    [Fact]
    public async Task Location_collisions_are_rejected_and_an_empty_directory_is_accepted()
    {
        await using var rig = await CreateAsync();
        GitTestEnv.RunGit(rig.Repo, "worktree", "add", "-q", "-b", "side", NewWorktreeService.Native(rig.Ws + "/side"));
        Directory.CreateDirectory(Path.Combine(rig.Env.Dir, "ws", "full"));
        File.WriteAllText(Path.Combine(rig.Env.Dir, "ws", "full", "x"), "x");
        File.WriteAllText(Path.Combine(rig.Env.Dir, "ws", "afile"), "x");
        Directory.CreateDirectory(Path.Combine(rig.Env.Dir, "ws", "empty"));

        async Task<NewWorktreeResult> At(string rel, string branch) =>
            await rig.Service.CreateAsync(new CreateWorktreeRequest(rig.RepoNative, branch, Path: NewWorktreeService.Native(rig.Ws + "/" + rel)), "cli", CancellationToken.None);

        var inMain = await At("app/sub", "b1");
        Assert.Equal(400, inMain.StatusCode);
        Assert.Contains("inside the worktree", inMain.Error);
        Assert.Contains("inside the worktree", (await At("side/nested", "b2")).Error);
        Assert.Contains("contains the worktree", (await At("", "b3")).Error);
        Assert.Contains("not empty", (await At("full", "b4")).Error);
        Assert.Contains("file exists", (await At("afile", "b5")).Error);
        Assert.Contains("absolute", (await rig.Service.CreateAsync(new CreateWorktreeRequest(rig.RepoNative, "b6", Path: "relative/dir"), "cli", CancellationToken.None)).Error);

        var ok = await At("empty", "b7");
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal("b7", Branch(NewWorktreeService.Native(rig.Ws + "/empty")));
    }

    [Fact]
    public async Task A_tool_start_failure_leaves_the_worktree_and_reports_it()
    {
        await using var rig = await CreateAsync();
        rig.Launcher.AgentctlOverride = Path.Combine(rig.Env.Dir, "no-such-agentctl" + (OperatingSystem.IsWindows() ? ".exe" : ""));

        var r = await rig.Launcher.LaunchAsync(new LaunchRequest(rig.RepoNative, Tool: "faketool", Worktree: new NewWorktreeSpec("wip")), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(500, r.StatusCode);
        Assert.NotNull(r.WorktreePath);
        Assert.Contains("left in place", r.Detail);
        Assert.Contains(r.WorktreePath!, r.Detail);
        Assert.True(Directory.Exists(NewWorktreeService.Native(r.WorktreePath!)));
        Assert.Contains(GitTestEnv.RunGit(rig.Repo, "worktree", "list", "--porcelain").Out.Split('\n'),
            l => l.StartsWith("worktree ") && PathText.Normalize(l[9..].Trim()).Equals(r.WorktreePath, PathText.Comparison));
        await rig.Env.Applied();
        Assert.NotNull(rig.Env.State.Read(s => s.Worktrees[r.WorktreeId!].Origin));
        var audit = rig.Env.Sink.OfType(EventTypes.AuditControl).Select(e => e.Payload<AuditData>()!).Single(a => a.Action == "launch");
        Assert.Equal("failed", audit.Outcome);
        Assert.Contains("left in place", audit.Detail);
    }

    [Fact]
    public async Task A_failing_setup_command_leaves_the_worktree_and_stops_before_the_tool()
    {
        await using var rig = await CreateAsync(o => o.NewWorktree.SetupCommand["app"] = [GitExe, "definitely-not-a-git-command"]);
        var plan = await rig.Service.PlanAsync(rig.RepoNative, null, null, null, false, CancellationToken.None);
        Assert.Contains("definitely-not-a-git-command", plan.SetupCommand);

        var r = await rig.Launcher.LaunchAsync(new LaunchRequest(rig.RepoNative, Tool: "faketool",
            Worktree: new NewWorktreeSpec("with-setup", RunSetup: true)), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("Setup command", r.Detail);
        Assert.Null(r.TerminalId);
        Assert.True(Directory.Exists(NewWorktreeService.Native(r.WorktreePath!)));
    }

    [Fact]
    public async Task Plan_lists_branches_and_defaults_the_base_to_the_remote_default_branch_and_fetches_only_when_asked()
    {
        await using var rig = await CreateAsync();
        var clone = Path.Combine(rig.Env.Dir, "ws", "clone");
        GitTestEnv.RunGit(rig.Env.Dir, "clone", "-q", rig.RepoNative, clone);
        GitTestEnv.RunGit(clone, "branch", "local-only");

        var plan = await rig.Service.PlanAsync(clone, null, null, null, false, CancellationToken.None);
        Assert.True(plan.Ok, plan.Error);
        Assert.Equal("origin/main", plan.DefaultBase);
        Assert.Contains("origin/main", plan.RemoteBranches);
        Assert.Contains("local-only", plan.LocalBranches);
        Assert.DoesNotContain(plan.RemoteBranches, b => b.EndsWith("/HEAD"));

        // A commit on the "remote" is only seen after a requested fetch.
        GitTestEnv.Write(rig.Repo, "new.txt", "n\n");
        GitTestEnv.RunGit(rig.Repo, "add", "-A");
        GitTestEnv.RunGit(rig.Repo, "commit", "-q", "-m", "upstream change");
        var noFetch = await rig.Service.CreateAsync(new CreateWorktreeRequest(clone, "nofetch"), "cli", CancellationToken.None);
        Assert.True(noFetch.Ok, noFetch.Error);
        Assert.False(File.Exists(Path.Combine(NewWorktreeService.Native(noFetch.Path!), "new.txt")));
        var fetched = await rig.Service.CreateAsync(new CreateWorktreeRequest(clone, "fetched", Fetch: true), "cli", CancellationToken.None);
        Assert.True(fetched.Ok, fetched.Error);
        Assert.Equal("origin/main", fetched.Base);
        Assert.True(File.Exists(Path.Combine(NewWorktreeService.Native(fetched.Path!), "new.txt")));
    }

    [Fact]
    public async Task Plan_reports_the_default_location_from_config_and_validation_errors()
    {
        await using var rig = await CreateAsync(o => o.NewWorktree.Root = Path.Combine(Path.GetTempPath(), "amc-wt-root-test", "{repo}"));
        var plan = await rig.Service.PlanAsync(rig.RepoNative, "feat/a", null, null, false, CancellationToken.None);
        Assert.True(plan.Ok, plan.Error);
        Assert.Null(plan.BranchError);
        Assert.Null(plan.PathError);
        Assert.Equal(PathText.Normalize(Path.Combine(Path.GetTempPath(), "amc-wt-root-test", "app", "feat-a")), plan.Path, ignoreCase: PathText.Windows);
        Assert.Equal("main", plan.Base);

        var bad = await rig.Service.PlanAsync(rig.RepoNative, "x", "no-such-ref", null, false, CancellationToken.None);
        Assert.NotNull(bad.BaseError);
        var outside = await rig.Service.PlanAsync(NewWorktreeService.Native(rig.Ws + "/nonexistent"), "x", null, null, false, CancellationToken.None);
        Assert.False(outside.Ok);
    }

    [Fact]
    public async Task Disabled_control_refuses_and_creates_nothing()
    {
        await using var rig = await CreateAsync(o => o.EnableControl = false);
        var created = await rig.Service.CreateAsync(new CreateWorktreeRequest(rig.RepoNative, "x"), "cli", CancellationToken.None);
        Assert.Equal(403, created.StatusCode);
        var launched = await rig.Launcher.LaunchAsync(new LaunchRequest(rig.RepoNative, Tool: "faketool", Worktree: new NewWorktreeSpec("y")), CancellationToken.None);
        Assert.Equal(403, launched.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(rig.Env.Dir, "ws", "app.worktrees")));
    }

    [Fact]
    public async Task Endpoints_are_gated_by_token_host_and_origin_like_every_api_call()
    {
        await using var rig = await CreateAsync();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new ApiToken("tok"));
        builder.Services.AddSingleton(Options.Create(rig.Env.Options));
        builder.Services.AddSingleton(rig.Service);
        builder.Services.AddSingleton(rig.Launcher);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy);
        await using var app = builder.Build();
        app.UseMiddleware<SecurityMiddleware>();
        ToolsModule.MapNewWorktreeEndpoints(app);
        app.MapPost("/api/sessions", async (LaunchRequest body, SessionLauncher launcher, CancellationToken ct) =>
        {
            var r = await launcher.LaunchAsync(body, ct);
            return Results.Json(r, statusCode: r.StatusCode);
        });
        await app.StartAsync();
        var http = app.GetTestClient();
        http.BaseAddress = new Uri("http://127.0.0.1:47821/");

        async Task<HttpStatusCode> Post(string path, object body, string? token, string? origin = null, string? host = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            if (token is not null) req.Headers.Add(ApiToken.Header, token);
            if (origin is not null) req.Headers.Add("Origin", origin);
            if (host is not null) req.Headers.Host = host;
            return (await http.SendAsync(req)).StatusCode;
        }
        var create = new { cwd = rig.RepoNative, branch = "gated" };
        var launch = new { cwd = rig.RepoNative, tool = "faketool", worktree = new { branch = "gated2" } };

        Assert.Equal(HttpStatusCode.Unauthorized, await Post("/api/new-worktree", create, null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Post("/api/sessions", launch, "wrong"));
        Assert.Equal(HttpStatusCode.Forbidden, await Post("/api/new-worktree", create, "tok", origin: "https://evil.example"));
        Assert.Equal((HttpStatusCode)421, await Post("/api/sessions", launch, "tok", host: "phone.tail1234.ts.net"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/api/new-worktree/plan?cwd={Uri.EscapeDataString(rig.RepoNative)}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(rig.Env.Dir, "ws", "app.worktrees")));

        Assert.Equal(HttpStatusCode.Created, await Post("/api/new-worktree", create, "tok"));
        Assert.True(Directory.Exists(Path.Combine(rig.Env.Dir, "ws", "app.worktrees", "gated")));
    }
}
