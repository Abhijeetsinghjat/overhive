using System.Net;
using System.Net.Http.Json;
using Amc.Core.Events;
using Amc.Daemon;
using Amc.Daemon.Collectors.Git;
using Amc.Daemon.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Amc.Tests.Git;

public sealed class GitEndpointsTests
{
    private static async Task<(WebApplication App, HttpClient Http)> ServeAsync(GitTestEnv env)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(env.State);
        builder.Services.AddSingleton(env.Git);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy);
        var app = builder.Build();
        GitModule.MapGitEndpoints(app);
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task Diff_file_log_and_blame_serve_worktree_content()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var dir = env.InitRepo("repo", ("src/a.txt", "one\ntwo\n"));
        var wt = await env.DiscoverAsync(dir);
        GitTestEnv.Write(dir, "src/a.txt", "one\nTWO\n");
        GitTestEnv.Write(dir, "untracked.txt", "fresh\n");
        File.WriteAllBytes(Path.Combine(dir, "bin.dat"), [1, 0, 2, 3]);
        var (app, http) = await ServeAsync(env);
        await using var _ = app;

        var diff = await http.GetFromJsonAsync<DiffResponse>($"/api/worktrees/{wt}/diff?path=src/a.txt", Json.Options);
        Assert.Contains("+TWO", diff!.Diff);
        Assert.DoesNotContain("fresh", diff.Diff);

        var untracked = await http.GetFromJsonAsync<DiffResponse>($"/api/worktrees/{wt}/diff?path=untracked.txt", Json.Options);
        Assert.Contains("new file mode", untracked!.Diff);
        Assert.Contains("+fresh", untracked.Diff);

        var all = await http.GetFromJsonAsync<DiffResponse>($"/api/worktrees/{wt}/diff", Json.Options);
        Assert.Contains("+TWO", all!.Diff);
        Assert.Contains("+fresh", all.Diff);
        Assert.False(all.Truncated);

        var file = await http.GetFromJsonAsync<FileResponse>($"/api/worktrees/{wt}/file?path=src/a.txt", Json.Options);
        Assert.Equal(("one\nTWO\n", false), (file!.Content, file.Binary));
        var bin = await http.GetFromJsonAsync<FileResponse>($"/api/worktrees/{wt}/file?path=bin.dat", Json.Options);
        Assert.True(bin!.Binary);
        Assert.Null(bin.Content);

        var log = await http.GetFromJsonAsync<LogResponse>($"/api/worktrees/{wt}/log?path=src/a.txt&limit=5", Json.Options);
        Assert.Equal("initial", Assert.Single(log!.Commits).Subject);

        var blame = await http.GetFromJsonAsync<BlameResponse>($"/api/worktrees/{wt}/blame?path=src/a.txt", Json.Options);
        Assert.Equal(2, blame!.Lines.Count);
        Assert.Equal(("one", 1), (blame.Lines[0].Content, blame.Lines[0].Line));

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/worktrees/{wt}/file?path=missing.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/worktrees/wt:unknown/diff")).StatusCode);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("-p")]
    [InlineData(".git/config")]
    [InlineData("link-dir/secret.txt")]
    [InlineData("link-file")]
    [InlineData("dangling")]
    [InlineData("%2Fetc%2Fpasswd")]
    [InlineData("a%00b")]
    public async Task Traversal_and_symlink_escapes_are_rejected(string path)
    {
        await using var env = await GitTestEnv.CreateAsync();
        var dir = env.InitRepo("repo", ("src/a.txt", "a\n"));
        var secrets = Directory.CreateDirectory(Path.Combine(env.Dir, "secrets")).FullName;
        File.WriteAllText(Path.Combine(secrets, "secret.txt"), "top secret\n");
        File.WriteAllText(Path.Combine(env.Dir, "outside.txt"), "outside\n");
        File.CreateSymbolicLink(Path.Combine(dir, "link-dir"), secrets);
        File.CreateSymbolicLink(Path.Combine(dir, "link-file"), Path.Combine(secrets, "secret.txt"));
        File.CreateSymbolicLink(Path.Combine(dir, "dangling"), Path.Combine(env.Dir, "nope", "x"));
        var wt = await env.DiscoverAsync(dir);
        var (app, http) = await ServeAsync(env);
        await using var _ = app;

        foreach (var endpoint in new[] { "file", "diff", "log", "blame" })
        {
            var res = await http.GetAsync($"/api/worktrees/{wt}/{endpoint}?path={path}");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.DoesNotContain("secret", await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Agent_diff_returns_files_attributed_to_the_agent()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var dir = env.InitRepo("repo", ("a.txt", "a\n"), ("b.txt", "b\n"));
        var wt = await env.DiscoverAsync(dir);
        await env.Sink.PublishAndWaitAsync(AgentEvent.Create(EventTypes.AgentCreated, EventSources.Hook,
            new AgentCreatedData(Amc.Core.Domain.AgentKind.Main, "test")) with { AgentId = "test:agent-1" });
        env.Collector.NotifyActivity(new WorktreeActivity(wt, "test:agent-1", "t1", ActivityKinds.Shell, ActivityKinds.Start, DateTimeOffset.UtcNow));
        GitTestEnv.Write(dir, "a.txt", "a\nby agent\n");
        env.Collector.NotifyActivity(new WorktreeActivity(wt, "test:agent-1", "t1", ActivityKinds.Shell, ActivityKinds.End, DateTimeOffset.UtcNow));
        await env.Collector.RefreshAsync(wt, CancellationToken.None);
        GitTestEnv.Write(dir, "b.txt", "b\nby nobody\n");
        await env.Collector.RefreshAsync(wt, CancellationToken.None);
        await env.Applied();
        var (app, http) = await ServeAsync(env);
        await using var _ = app;

        var res = await http.GetFromJsonAsync<AgentDiffResponse>("/api/agents/test:agent-1/diff", Json.Options);
        var f = Assert.Single(res!.Files);
        Assert.Equal(("a.txt", wt), (f.Path, f.WorktreeId));
        Assert.Contains("+by agent", f.Diff);
    }
}
