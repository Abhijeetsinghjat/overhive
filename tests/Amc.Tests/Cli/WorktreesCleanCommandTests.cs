using System.Net;
using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;

namespace Amc.Tests.Cli;

public sealed class WorktreesCleanCommandTests : IDisposable
{
    private readonly CliHarness _h = new();

    private const string Plan = """
        {"worktreeId":"wt:x","root":"/r/wt-x","branch":"feat/x","mode":"suggest","checks":[
          {"name":"merged","ok":true,"detail":"PR #7 merged (observed via gh)"},
          {"name":"clean","ok":true,"detail":"no uncommitted changes"}]}
        """;

    public WorktreesCleanCommandTests()
    {
        _h.Daemon
            .Get("/api/worktrees", new List<Worktree>
            {
                new() { Id = "wt:main", Root = "/r/repo", RepositoryId = "repo:1", Branch = "main", IsMain = true },
                new()
                {
                    Id = "wt:x", Root = "/r/wt-x", RepositoryId = "repo:1", Branch = "feat/x", Head = "abc1234",
                    Merged = new WorktreeMerge(MergeSources.GitHub, "feat/x", "abc1234", Evidence.Observed, CliHarness.Now, 7),
                },
                new() { Id = "wt:y", Root = "/r/wt-y", RepositoryId = "repo:1", Branch = "fix/y" },
            })
            .Get("/api/repositories", new List<Repository> { new() { Id = "repo:1", Name = "repo", CommonDir = "/r/repo/.git" } })
            .Get("/api/agents", new List<Agent>())
            .On("POST", "/api/worktrees/wt:x/cleanup", req =>
            {
                var body = JsonNode.Parse(req.Body!)!;
                return body["dryRun"]!.GetValue<bool>()
                    ? (HttpStatusCode.OK, $$"""{"ok":true,"outcome":"dry-run","detail":"Would remove /r/wt-x, local branch feat/x","plan":{{Plan}},"steps":[]}""")
                    : (HttpStatusCode.OK, $$"""{"ok":true,"outcome":"removed","detail":"Removed /r/wt-x","plan":{{Plan}},"steps":[{"name":"remove-worktree","command":"git worktree remove /r/wt-x","outcome":"ok"}]}""");
            });
    }

    [Fact]
    public async Task List_shows_merged_status()
    {
        Assert.Equal(0, await _h.Run("worktrees"));
        var line = _h.Out.ToString().Split('\n').Single(l => l.Contains("wt-x"));
        Assert.Contains("PR #7 (gh)", line);
    }

    [Fact]
    public async Task Clean_is_a_dry_run_unless_yes_is_given()
    {
        Assert.Equal(0, await _h.Run("worktrees", "clean"));
        var dry = JsonNode.Parse(_h.Daemon.Single("POST", "/cleanup").Body!)!;
        Assert.True(dry["dryRun"]!.GetValue<bool>());
        Assert.False(dry["confirm"]!.GetValue<bool>());
        var text = _h.Out.ToString();
        Assert.Contains("would remove", text);
        Assert.Contains("local branch   feat/x", text);
        Assert.Contains("Dry run: nothing was removed", text);
        Assert.DoesNotContain("wt-y", text); // only merged worktrees

        _h.Daemon.Requests.Clear();
        Assert.Equal(0, await _h.Run("worktrees", "clean", "--yes", "--dry-run"));
        Assert.True(JsonNode.Parse(_h.Daemon.Single("POST", "/cleanup").Body!)!["dryRun"]!.GetValue<bool>());

        _h.Daemon.Requests.Clear();
        Assert.Equal(0, await _h.Run("worktrees", "clean", "/r/wt-x", "--yes"));
        var real = JsonNode.Parse(_h.Daemon.Single("POST", "/cleanup").Body!)!;
        Assert.True(real["confirm"]!.GetValue<bool>());
        Assert.False(real["dryRun"]!.GetValue<bool>());
        Assert.Contains("$ git worktree remove /r/wt-x", _h.Out.ToString());
    }

    public void Dispose() => _h.Dispose();
}
