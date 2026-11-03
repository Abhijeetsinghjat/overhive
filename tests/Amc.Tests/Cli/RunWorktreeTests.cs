using System.Net;
using System.Text.Json.Nodes;
using Amc.Cli;
using Amc.Cli.Infrastructure;
using Amc.Cli.Run;

namespace Amc.Tests.Cli;

/// <summary>`agentctl run --worktree`: the worktree is created through the daemon, then the PTY run uses its path.</summary>
[Collection("WorktreeRunSetupHook")]
public sealed class RunWorktreeTests : IDisposable
{
    private readonly CliHarness _h = new();
    public void Dispose() { WorktreeRun.SetupRunner = null; _h.Dispose(); }

    private Task<string> Prepare(params string[] argv) =>
        WorktreeRun.PrepareAsync(_h.Context(), ArgParser.Parse(argv, Router.Find), "T1", CancellationToken.None);

    [Fact]
    public async Task Creates_the_worktree_through_the_daemon_and_returns_its_path()
    {
        _h.Daemon.On("POST", "/api/new-worktree", _ => (HttpStatusCode.Created,
            """{"ok":true,"path":"/w/app.worktrees/feat-x","branch":"feat/x","base":"origin/main","newBranch":true,"warnings":["long"]}"""));

        var path = await Prepare("run", "--worktree", "feat/x", "--base", "origin/main", "--fetch", "--", "claude", "--model", "opus");

        Assert.Equal(OperatingSystem.IsWindows() ? @"\w\app.worktrees\feat-x" : "/w/app.worktrees/feat-x", path);
        var body = JsonNode.Parse(_h.Daemon.Single("POST", "new-worktree").Body!)!;
        Assert.Equal(("feat/x", "origin/main", true, false, "T1", "claude", _h.Dir),
            ((string?)body["branch"], (string?)body["base"], (bool?)body["fetch"], (bool?)body["existingBranch"], (string?)body["terminalId"],
             (string?)body["tool"], (string?)body["cwd"]));
        Assert.Contains("Created worktree /w/app.worktrees/feat-x on branch feat/x (new, from origin/main)", _h.Err.ToString());
        Assert.Contains("! long", _h.Err.ToString());
    }

    [Fact]
    public async Task Daemon_refusals_are_reported_and_setup_runs_only_when_asked()
    {
        _h.Daemon.On("POST", "/api/new-worktree", _ => (HttpStatusCode.BadRequest, """{"error":"Branch 'main' already exists"}"""));
        var ex = await Assert.ThrowsAsync<CliException>(() => Prepare("run", "--worktree", "main", "--", "claude"));
        Assert.Contains("already exists", ex.Message);

        _h.Daemon.On("POST", "/api/new-worktree", _ => (HttpStatusCode.Created,
            """{"path":"/w/t","newBranch":false,"setupCommand":"npm ci","setupArgs":["npm","ci"]}"""));
        var ran = new List<string>();
        WorktreeRun.SetupRunner = (args, cwd) => { ran.Add(string.Join(' ', args)); return 3; };
        await Prepare("run", "--worktree", "t", "--existing", "--", "claude");
        Assert.Empty(ran);
        Assert.Contains("add --setup", _h.Err.ToString());
        var failed = await Assert.ThrowsAsync<CliException>(() => Prepare("run", "--worktree", "t", "--existing", "--setup", "--", "claude"));
        Assert.Equal(["npm ci"], ran);
        Assert.Contains("left in place: /w/t", failed.Message);
    }

    [Fact]
    public async Task Worktree_options_without_worktree_are_usage_errors()
    {
        Assert.Equal(ExitCodes.Usage, await _h.Run("run", "--base", "main", "--", "claude"));
        Assert.Equal(ExitCodes.Usage, await _h.Run("run", "--worktree", "x", "--existing", "--base", "main", "--", "claude"));
        Assert.Empty(_h.Daemon.Requests);
    }
}
