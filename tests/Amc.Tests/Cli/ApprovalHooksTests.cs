using System.Net;
using System.Text.Json.Nodes;
using Amc.Cli.Hooks;
using Amc.Cli.Infrastructure;
using Amc.Core.Approvals;
using Amc.Core.Providers;
using Amc.Core.Providers.Claude;
using Amc.Core.Risk;

namespace Amc.Tests.Cli;

/// <summary>Approvals on the CLI side: the PreToolUse hook variant per OS, hook-forward, status/doctor, `approvals`.</summary>
public sealed class ApprovalHooksTests : IDisposable
{
    private readonly CliHarness _h = new();
    private string Settings => Path.Combine(_h.Dir, ".claude", "settings.json");
    private string HookSocket => Path.Combine(_h.Dir, "state", ClaudeHooksConfig.SocketFileName);
    private const string Exe = @"C:\Users\Jane O'Neil\AppData\Local\Overhive\agentctl.exe";
    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(Settings))!;
    private static JsonNode Ours(JsonObject s, string ev) =>
        s["hooks"]![ev]!.AsArray().SelectMany(g => g!["hooks"]!.AsArray()).First(h => ClaudeHooksConfig.IsOurs(h) && h?["asyncRewake"] is null)!;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Approvals_off_keeps_todays_PreToolUse_hook_unchanged()
    {
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        var pre = Ours(Read(), "PreToolUse");
        Assert.Equal(ClaudeHooksConfig.BuildCommand(HookSocket), (string?)pre["command"]);
        Assert.Equal(ClaudeHooksConfig.TimeoutSeconds, (int)pre["timeout"]!);
        Assert.Contains("-m 3 ", (string?)pre["command"]);
    }

    [Fact]
    public async Task Linux_and_macOS_get_a_longer_curl_and_hook_timeout_on_PreToolUse_only()
    {
        _h.Approvals = new ApprovalSettings { Mode = "risky", TimeoutSeconds = 120 };
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        var s = Read();
        var pre = Ours(s, "PreToolUse");
        var cmd = (string?)pre["command"];
        Assert.Equal(ClaudeHooksConfig.BuildApprovalCommand(HookSocket, 135), cmd);
        Assert.Contains("curl -sf -m 135 ", cmd);
        Assert.Contains("'http://localhost/hooks/claude?approval=1'", cmd);
        Assert.EndsWith("|| true # agent-mission-control", cmd);              // still fails open
        Assert.Equal(150, (int)pre["timeout"]!);                               // Claude Code's timeout > curl's > the daemon's hold
        Assert.True(OverhiveHooks.IsHookCommand(cmd));                         // never reported as the agent's own action
        foreach (var ev in new[] { "PostToolUse", "Stop", "UserPromptSubmit" })
        {
            Assert.Equal(ClaudeHooksConfig.BuildCommand(HookSocket), (string?)Ours(s, ev)["command"]);
            Assert.Equal(ClaudeHooksConfig.TimeoutSeconds, (int)Ours(s, ev)["timeout"]!);
        }

        _h.Out.GetStringBuilder().Clear();
        Assert.Equal(0, await _h.Run("hooks", "status", "--settings", Settings, "--json"));
        var status = JsonNode.Parse(_h.Out.ToString())!;
        Assert.True((bool)status["upToDate"]!);
        Assert.Equal("risky", (string?)status["approvals"]);

        // Turning approvals off (or changing the timeout) makes the installed hook outdated until reinstalled.
        _h.Approvals = new ApprovalSettings();
        _h.Out.GetStringBuilder().Clear();
        Assert.Equal(0, await _h.Run("hooks", "status", "--settings", Settings, "--json"));
        Assert.False((bool)JsonNode.Parse(_h.Out.ToString())!["upToDate"]!);
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        Assert.Equal(ClaudeHooksConfig.BuildCommand(HookSocket), (string?)Ours(Read(), "PreToolUse")["command"]);
    }

    [Fact]
    public async Task Windows_gets_hook_forward_with_approval_and_max_time()
    {
        _h.Windows = true;
        _h.Approvals = new ApprovalSettings { Mode = "all-edits", TimeoutSeconds = 60 };
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        var pre = Ours(Read(), "PreToolUse");
        Assert.Equal(Exe, (string?)pre["command"]);
        Assert.Equal(["hook-forward", "claude", "--approval", "--max-time", "75", "--socket", HookSocket, "--tag", ClaudeHooksConfig.Marker],
            pre["args"]!.AsArray().Select(a => (string?)a));
        Assert.Equal(90, (int)pre["timeout"]!);
        Assert.Equal(["hook-forward", "claude", "--socket", HookSocket, "--tag", ClaudeHooksConfig.Marker],
            Ours(Read(), "PostToolUse")["args"]!.AsArray().Select(a => (string?)a));
        Assert.True(OverhiveHooks.IsHookCommand(new HookForwarder(Exe).CmdLine(pre["args"]!.AsArray().Select(a => (string)a!).ToList())));
    }

    [Fact]
    public void Hook_forward_uses_the_approval_route_and_waits_only_as_long_as_asked()
    {
        var r = HookForward.Parse(["claude", "--approval", "--max-time", "135", "--socket", "/s/amcd.sock", "--tag", "x"], () => null)!;
        Assert.Equal(("/hooks/claude?approval=1", TimeSpan.FromSeconds(135)), (r.PathAndQuery, r.Timeout));
        foreach (var bad in new[] { "abc", "-5", "0", "999999" })
        {
            var p = HookForward.Parse(["claude", "--approval", "--max-time", bad, "--socket", "/s/amcd.sock"], () => null)!;
            Assert.Equal(("/hooks/claude", TimeSpan.FromSeconds(3)), (p.PathAndQuery, p.Timeout));
        }
        var plain = HookForward.Parse(["claude", "--socket", "/s/amcd.sock"], () => null)!;
        Assert.Equal(("/hooks/claude", TimeSpan.FromSeconds(3)), (plain.PathAndQuery, plain.Timeout));
    }

    [Fact]
    public void Timeouts_are_clamped_and_ordered()
    {
        var a = new ApprovalSettings { Mode = "RISKY", TimeoutSeconds = 1 };
        Assert.Equal((ApprovalModes.Risky, 10, 25, 40), (a.EffectiveMode, a.EffectiveTimeoutSeconds, a.TransportSeconds, a.HookTimeoutSeconds));
        Assert.Equal(ApprovalSettings.MaxTimeoutSeconds, new ApprovalSettings { TimeoutSeconds = 99999 }.EffectiveTimeoutSeconds);
        Assert.Equal(ApprovalModes.Off, ApprovalModes.Normalize("yes please"));
        var cfg = CliSettings.LoadApprovals(n => n == "AMC_Approvals__TimeoutSeconds" ? "200" : null,
            JsonNode.Parse("""{"approvals":{"mode":"all-edits","TimeoutSeconds":30}}""")!.AsObject());
        Assert.Equal((ApprovalModes.AllEdits, 200), (cfg.EffectiveMode, cfg.EffectiveTimeoutSeconds));
    }

    [Fact]
    public async Task Approvals_command_lists_and_decides_by_id_prefix()
    {
        const string id = "0123456789abcdef0123456789abcdef";
        _h.Daemon.Get("/api/approvals", $$"""
            {"mode":"risky","timeoutSeconds":120,"minSeverity":"warn","items":[{"id":"{{id}}","agentId":"claude:5e55a0aa-1","tool":"Bash","reason":"risk",
             "command":"rm -rf ~/","severity":"critical","findings":[{"ruleId":"recursive-delete","severity":"critical","explanation":"Recursive delete of the home directory","span":"rm -rf ~/"}],
             "requestedAt":"{{CliHarness.Now.AddSeconds(-20):O}}"}]}
            """);
        _h.Daemon.On("POST", $"/api/approvals/{id}/deny", _ => (HttpStatusCode.OK, """{"ok":true,"message":"Denied","latencyMs":20000}"""));
        _h.Daemon.On("POST", $"/api/approvals/{id}/allow", _ => (HttpStatusCode.NotFound, """{"ok":false,"message":"No such pending approval"}"""));

        Assert.Equal(0, await _h.Run("approvals"));
        var listing = _h.Out.ToString();
        Assert.Contains("01234567", listing);
        Assert.Contains("rm -rf ~/", listing);
        Assert.Contains("critical", listing);

        Assert.Equal(0, await _h.Run("approvals", "deny", "0123", "--reason", "too risky"));
        var body = JsonNode.Parse(_h.Daemon.Single("POST", "/deny").Body!)!;
        Assert.Equal(("too risky", false, "cli"), ((string?)body["reason"], (bool)body["allowSimilar"]!, (string?)body["client"]));
        Assert.Contains("Denied", _h.Out.ToString());

        Assert.Equal(ExitCodes.Error, await _h.Run("approvals", "allow", id, "--similar"));
        Assert.True((bool)JsonNode.Parse(_h.Daemon.Single("POST", "/allow").Body!)!["allowSimilar"]!);
        Assert.Equal(ExitCodes.Usage, await _h.Run("approvals", "deny", id, "--similar"));
        Assert.Equal(ExitCodes.Usage, await _h.Run("approvals", "maybe"));
    }
}
