using System.Net;
using Amc.Core.Domain;

namespace Amc.Tests.Cli;

public sealed class ConflictsCommandTests : IDisposable
{
    private const string Key = "cross-worktree|repo:1|src/auth.ts";
    private readonly CliHarness _h = new();

    public ConflictsCommandTests()
    {
        _h.Daemon
            .Get("/api/conflicts", new List<Conflict>
            {
                new()
                {
                    Key = Key, Kind = ConflictKind.CrossWorktree, Path = "src/auth.ts", Description = "d", WorktreeIds = ["wt:x", "wt:y"],
                    Insight = new ConflictInsight
                    {
                        Overlap = new ConflictOverlap
                        {
                            Verdict = OverlapVerdict.Overlapping, AnalyzedAt = CliHarness.Now.AddMinutes(-2), Margin = 3,
                            Pairs = [new OverlapPair("wt:x", "wt:y", "abc", [new(40, 80)], [new(52, 70)], [new(40, 80)], [new(52, 70)], [new(45, 60)], true, CommittedConflict: true)],
                        },
                    },
                },
                new() { Key = "same-worktree|wt:x", Kind = ConflictKind.SameWorktree, Description = "2 active agents are writing in the same worktree" },
            })
            .Get("/api/worktrees", new List<Worktree>
            {
                new() { Id = "wt:x", Root = "/r/x", RepositoryId = "repo:1", Branch = "feat/x" },
                new() { Id = "wt:y", Root = "/r/y", RepositoryId = "repo:1", Branch = "fix/y" },
            })
            .Get("/api/conflicts/detail", """
                {"conflict":{},"warn":{"conflictKey":"k","path":"src/auth.ts","verdict":"overlapping","recipients":[
                  {"agentId":"codex:1","name":"Codex","tool":"Codex CLI","worktreeId":"wt:x","branch":"feat/x","canReceive":true,"transport":"pty","text":"Heads-up from Overhive: ...","baseSpans":[]}]}}
                """)
            .On("POST", "/api/conflicts/warn", _ => (HttpStatusCode.OK, """
                {"ok":true,"conflictKey":"k","agents":[{"agentId":"codex:1","name":"Codex","outcome":"delivered","transport":"pty","detail":"typed"}],"detail":"1 of 1 agent(s) warned"}
                """));
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Lists_conflicts_with_compared_line_ranges()
    {
        Assert.Equal(0, await _h.Run("conflicts"));
        var o = _h.Out.ToString();
        Assert.Contains("overlapping lines src/auth.ts", o);
        Assert.Contains("feat/x lines 40-80 overlaps fix/y lines 52-70", o);
        Assert.Contains("merge-tree: committed heads conflict", o);
        Assert.Contains("2 active agents are writing", o);
    }

    [Fact]
    public async Task Warn_needs_yes_when_not_interactive_and_posts_with_confirm()
    {
        Assert.Equal(2, await _h.Run("conflicts", "warn", "1"));
        Assert.DoesNotContain(_h.Daemon.Requests, r => r.Method == HttpMethod.Post);

        Assert.Equal(0, await _h.Run("conflicts", "warn", "src/auth.ts", "--yes"));
        var post = _h.Daemon.Single("POST", "/api/conflicts/warn");
        Assert.Contains("\"confirm\":true", post.Body);
        Assert.Contains("cross-worktree|repo:1|src/auth.ts", post.Body);
        Assert.Contains("Codex: delivered", _h.Out.ToString());
    }

    [Fact]
    public async Task Warn_asks_interactively()
    {
        Assert.Equal(1, await _h.Run("n\n", true, "conflicts", "warn", "1"));
        Assert.Contains("Heads-up from Overhive", _h.Err.ToString());
        Assert.DoesNotContain(_h.Daemon.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(0, await _h.Run("y\n", true, "conflicts", "warn", "1"));
        Assert.Single(_h.Daemon.Requests, r => r.Method == HttpMethod.Post);
    }
}
