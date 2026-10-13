using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Providers.Claude;
using Amc.Tests.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Amc.Tests.E2E;

/// <summary>
/// End-to-end: the real daemon (all collectors) observes a fake agent that behaves like Claude Code:
/// a real OS process with a registry entry, real hook payloads, a real git repository + worktree,
/// real file edits, a test run, a sub-agent, a same-file conflict, a commit, a mailbox message,
/// and finally the process dying. Then WebSocket replay and a daemon restart are verified.
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class FakeAgentEndToEndTests : IDisposable
{
    private const string Session = "e2e00000-0000-4000-8000-000000000001";
    private static readonly string Main = Ids.MainAgent("claude", Session);
    private static readonly string Sub = Ids.SubAgent("claude", Session, "s1");

    private readonly TempDir _data = new(), _claude = new(), _work = new();
    private System.Diagnostics.Process? _fakeAgent;

    private WebApplicationFactory<Program> StartDaemon()
    {
        Environment.SetEnvironmentVariable("AMC_DataDir", _data.Path);
        Environment.SetEnvironmentVariable("AMC_ClaudeHome", _claude.Path);
        Environment.SetEnvironmentVariable("AMC_WorkspaceScanDepth", "0");
        Environment.SetEnvironmentVariable("AMC_BackfillHours", "1");
        return new WebApplicationFactory<Program>();
    }

    private static HttpClient Client(WebApplicationFactory<Program> f, string token)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Amc-Token", token);
        return c;
    }

    [Fact]
    public async Task Complete_agent_lifecycle_is_observed_persisted_and_replayable()
    {
        // ---- environment: Claude home with our hooks "installed", a git repo with a linked worktree ----
        Directory.CreateDirectory(Path.Combine(_claude.Path, "sessions"));
        Directory.CreateDirectory(Path.Combine(_claude.Path, "projects"));
        File.WriteAllText(Path.Combine(_claude.Path, "settings.json"),
            ClaudeHooksConfig.Install(new JsonObject(), ClaudeHooksConfig.BuildCommand("/dev/null")).ToJsonString());
        var repo = Path.Combine(_work.Path, "app");
        var wt = Path.Combine(_work.Path, "app-feature");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "Service.cs"), "class Service {}\n");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=e2e@test", "-c", "user.name=e2e", "commit", "-qm", "init");
        Git(repo, "worktree", "add", "-q", wt, "-b", "feature");

        await using var factory = StartDaemon();
        _ = factory.Server; // build + start the host (creates the token file)
        var token = File.ReadAllText(Path.Combine(_data.Path, "token")).Trim();
        using var http = Client(factory, token);

        // ---- 1. start fake agent: real process + registry entry ----
        _fakeAgent = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", "600") { UseShellExecute = false })!;
        var procStart = File.ReadAllText($"/proc/{_fakeAgent.Id}/stat").Split(')')[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
        File.WriteAllText(Path.Combine(_claude.Path, "sessions", $"{_fakeAgent.Id}.json"), new JsonObject
        {
            ["pid"] = _fakeAgent.Id, ["sessionId"] = Session, ["cwd"] = repo, ["procStart"] = procStart,
            ["startedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["version"] = "e2e", ["kind"] = "interactive",
            ["status"] = "busy", ["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }.ToJsonString());
        await Eventually(http, s => s.Agents.TryGetValue(Main, out var a) && a.Status == AgentStatus.Running && a.Pid == _fakeAgent.Id, "agent appears running");

        // ---- 2. modify a file in the worktree (Edit tool) ----
        await Hook(http, "SessionStart", ("source", "startup"));
        await Hook(http, "UserPromptSubmit", ("prompt", "Implement the feature"), ("prompt_id", "p1"));
        await Hook(http, "PreToolUse", ("tool_name", "Edit"), ("tool_use_id", "tu-edit"), ("tool_input", Edit(wt)));
        File.WriteAllText(Path.Combine(wt, "Service.cs"), "class Service { void Run() {} }\n");
        await Hook(http, "PostToolUse", ("tool_name", "Edit"), ("tool_use_id", "tu-edit"), ("tool_input", Edit(wt)),
            ("tool_response", new JsonObject { ["filePath"] = Path.Combine(wt, "Service.cs") }));

        // ---- 3. run a command (tests) ----
        await Hook(http, "PreToolUse", ("tool_name", "Bash"), ("tool_use_id", "tu-test"),
            ("tool_input", new JsonObject { ["command"] = $"cd {wt} && dotnet test", ["description"] = "Run tests" }));
        await Hook(http, "PostToolUse", ("tool_name", "Bash"), ("tool_use_id", "tu-test"),
            ("tool_input", new JsonObject { ["command"] = $"cd {wt} && dotnet test", ["description"] = "Run tests" }),
            ("tool_response", new JsonObject { ["stdout"] = "Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5", ["stderr"] = "", ["interrupted"] = false }));

        // ---- 4. spawn a sub-agent that modifies the SAME file and creates another ----
        await Hook(http, "PreToolUse", ("tool_name", "Agent"), ("tool_use_id", "tu-agent"),
            ("tool_input", new JsonObject { ["description"] = "Write tests", ["subagent_type"] = "general-purpose", ["prompt"] = "add tests" }));
        await Hook(http, "SubagentStart", ("agent_id", "s1"), ("agent_type", "general-purpose"));
        await Hook(http, "PreToolUse", ("agent_id", "s1"), ("agent_type", "general-purpose"), ("tool_name", "Edit"), ("tool_use_id", "tu-sub-edit"), ("tool_input", Edit(wt)));
        File.AppendAllText(Path.Combine(wt, "Service.cs"), "// touched by sub-agent\n");
        await Hook(http, "PostToolUse", ("agent_id", "s1"), ("agent_type", "general-purpose"), ("tool_name", "Edit"), ("tool_use_id", "tu-sub-edit"),
            ("tool_input", Edit(wt)), ("tool_response", new JsonObject { ["filePath"] = Path.Combine(wt, "Service.cs") }));
        var testFile = Path.Combine(wt, "ServiceTests.cs");
        var write = new JsonObject { ["file_path"] = testFile, ["content"] = "class ServiceTests {}\n" };
        await Hook(http, "PreToolUse", ("agent_id", "s1"), ("agent_type", "general-purpose"), ("tool_name", "Write"), ("tool_use_id", "tu-sub-write"), ("tool_input", write));
        File.WriteAllText(testFile, "class ServiceTests {}\n");
        await Hook(http, "PostToolUse", ("agent_id", "s1"), ("agent_type", "general-purpose"), ("tool_name", "Write"), ("tool_use_id", "tu-sub-write"),
            ("tool_input", write.DeepClone()), ("tool_response", new JsonObject { ["type"] = "create", ["filePath"] = testFile }));

        var state = await Eventually(http, s =>
            s.Agents.TryGetValue(Sub, out var sub) && sub.ParentId == Main
            && s.Conflicts.Values.Any(c => c.Kind == ConflictKind.SameFile && c.Path == "Service.cs")
            && s.Files.Values.Any(f => f.Path == "ServiceTests.cs" && f.Agents.ContainsKey(Sub)),
            "sub-agent linked, same-file conflict raised, sub-agent file attributed");
        Assert.Contains(Main, state.Conflicts.Values.First(c => c.Kind == ConflictKind.SameFile).AgentIds);

        // ---- 5. commit (real git), then sub-agent stops ----
        await Hook(http, "PreToolUse", ("tool_name", "Bash"), ("tool_use_id", "tu-commit"), ("tool_input", new JsonObject { ["command"] = $"git -C {wt} commit -am feature" }));
        Git(wt, "add", ".");
        Git(wt, "-c", "user.email=e2e@test", "-c", "user.name=e2e", "commit", "-qm", "feature: run + tests");
        await Hook(http, "PostToolUse", ("tool_name", "Bash"), ("tool_use_id", "tu-commit"), ("tool_input", new JsonObject { ["command"] = $"git -C {wt} commit -am feature" }),
            ("tool_response", new JsonObject { ["stdout"] = "[feature abc] feature", ["stderr"] = "", ["interrupted"] = false }));
        await Hook(http, "SubagentStop", ("agent_id", "s1"), ("agent_type", "general-purpose"), ("stop_hook_active", false));
        state = await Eventually(http, s => s.Commits.Values.Any(c => c.Subject == "feature: run + tests")
                                            && s.Agents[Sub].Status == AgentStatus.Completed
                                            && s.TestRuns.Values.Any(t => t.State == "failed" && t.Failed == 1 && t.Passed == 4),
            "commit observed, sub-agent completed, failed test run recorded");

        // ---- 6. message the agent through the hook mailbox ----
        var send = await http.PostAsJsonAsync($"/api/agents/{Uri.EscapeDataString(Main)}/messages", new { text = "Focus only on tests" });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, send.StatusCode);
        var reply = await Hook(http, "PostToolUse", ("tool_name", "Read"), ("tool_use_id", "tu-read"),
            ("tool_input", new JsonObject { ["file_path"] = testFile }), ("tool_response", new JsonObject()));
        Assert.Contains("Focus only on tests", reply);
        await Eventually(http, s => s.Messages.Values.Any(m => m.Status == MessageStatus.Delivered), "message delivered");

        // ---- 7. the agent process dies without an end event ----
        var seqBeforeCrash = state.Seq;
        _fakeAgent.Kill();
        await _fakeAgent.WaitForExitAsync();
        state = await Eventually(http, s => s.Agents[Main].Status == AgentStatus.Disconnected, "crash detected", TimeSpan.FromSeconds(15));
        Assert.Equal(Evidence.Observed, state.Agents[Main].StatusEvidence);

        // ---- 8. the complete timeline, in order ----
        var events = await http.GetFromJsonAsync<List<AgentEvent>>($"/api/events?agent={Uri.EscapeDataString(Main)}&descendants=true&limit=1000", Json.Options);
        var types = events!.Select(e => e.Type).ToList();
        string[] expectedOrder =
        [
            EventTypes.AgentCreated, EventTypes.AgentPrompt, EventTypes.FileModified, EventTypes.CommandStarted,
            EventTypes.TestStarted, EventTypes.TestFailed, EventTypes.AgentSpawned, EventTypes.FileCreated,
            EventTypes.AgentCompleted, EventTypes.MessageSent, EventTypes.MessageDelivered, EventTypes.ProcessExited,
        ];
        var cursor = 0;
        foreach (var t in expectedOrder)
        {
            var idx = types.IndexOf(t, cursor);
            Assert.True(idx >= 0, $"timeline missing '{t}' after position {cursor}: {string.Join(", ", types)}");
            cursor = idx;
        }
        var all = await http.GetFromJsonAsync<List<AgentEvent>>("/api/events?type=git.commit_created", Json.Options);
        Assert.Contains(all!, e => e.Data["subject"]?.GetValue<string>() == "feature: run + tests");

        // ---- 9. WebSocket replay from an earlier seq delivers everything after it ----
        var replayed = await ReplayTypes(factory, token, seqBeforeCrash);
        Assert.Contains(EventTypes.ProcessExited, replayed);

        // ---- 10. restart: historical state survives ----
        var before = await http.GetStringAsync("/api/agents");
        await factory.DisposeAsync();
        await using var restarted = StartDaemon();
        _ = restarted.Server;
        using var http2 = Client(restarted, token);
        var after = await Eventually(http2, s => s.Agents.ContainsKey(Main), "state restored after restart");
        Assert.Equal(AgentStatus.Disconnected, after.Agents[Main].Status);
        Assert.Equal(AgentStatus.Completed, after.Agents[Sub].Status);
        Assert.Contains(after.Commits.Values, c => c.Subject == "feature: run + tests");
        Assert.NotEmpty(before);
    }

    // ---- helpers ------------------------------------------------------------------------

    private static JsonObject Edit(string wt) => new()
    {
        ["file_path"] = Path.Combine(wt, "Service.cs"), ["old_string"] = "{}", ["new_string"] = "{ void Run() {} }",
    };

    private static async Task<string> Hook(HttpClient http, string evt, params (string Key, object Value)[] fields)
    {
        var body = new JsonObject
        {
            ["hook_event_name"] = evt, ["session_id"] = Session, ["cwd"] = "/", ["permission_mode"] = "default",
            ["transcript_path"] = "/nonexistent/e2e.jsonl",
        };
        foreach (var (k, v) in fields)
            body[k] = v switch { JsonNode n => n, string s => JsonValue.Create(s), bool b => JsonValue.Create(b), _ => JsonValue.Create(v.ToString()) };
        var r = await http.PostAsync("/hooks/claude", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync();
    }

    private static async Task<Amc.Core.State.WorldState> Eventually(HttpClient http, Func<Amc.Core.State.WorldState, bool> cond, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        Amc.Core.State.WorldState? s = null;
        while (DateTime.UtcNow < deadline)
        {
            s = Json.Deserialize<Amc.Core.State.WorldState>(await http.GetStringAsync("/api/state"))!;
            try { if (cond(s)) return s; } catch (KeyNotFoundException) { }
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {what}. Agents: {string.Join("; ", s?.Agents.Values.Select(a => $"{a.Id}={a.Status}") ?? [])}");
    }

    private static async Task<List<string>> ReplayTypes(WebApplicationFactory<Program> f, string token, long since)
    {
        var ws = f.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => r.Headers["X-Amc-Token"] = token;
        using var socket = await ws.ConnectAsync(new Uri(f.Server.BaseAddress, $"ws?since={since}"), CancellationToken.None);
        var types = new List<string>();
        var buf = new byte[1 << 20];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var r = await socket.ReceiveAsync(buf, cts.Token);
                var msg = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count))!;
                if ((string?)msg["t"] == "event") types.Add((string)msg["e"]!["type"]!);
                if (types.Contains(EventTypes.ProcessExited)) break;
            }
        }
        catch (OperationCanceledException) { }
        return types;
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }

    public void Dispose()
    {
        try { if (_fakeAgent is { HasExited: false }) _fakeAgent.Kill(); } catch (InvalidOperationException) { }
        foreach (var k in new[] { "AMC_DataDir", "AMC_ClaudeHome", "AMC_WorkspaceScanDepth", "AMC_BackfillHours" })
            Environment.SetEnvironmentVariable(k, null);
        _data.Dispose(); _claude.Dispose(); _work.Dispose();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DaemonCollection
{
    public const string Name = "daemon";
}
