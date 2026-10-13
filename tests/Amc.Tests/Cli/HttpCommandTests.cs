using System.Net;
using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Domain;
using Amc.Core.Events;
using static Amc.Tests.Cli.CliHarness;

namespace Amc.Tests.Cli;

public class HttpCommandTests : IDisposable
{
    private const string MainId = "claude:c441aaaa-1111";
    private const string SubId = "claude:c441aaaa-1111/a319bbbb";
    private readonly CliHarness _h = new();

    public HttpCommandTests()
    {
        _h.Daemon
            .Get("/api/agents", new List<Agent>
            {
                Agent(MainId, "Refactor billing", pid: 4242),
                Agent(SubId, "Write billing tests", parent: MainId),
            })
            .Get("/api/worktrees", new List<Worktree>())
            .Get("/api/repositories", new List<Repository>())
            .Get("/api/conflicts", new List<Conflict>
            {
                new() { Key = "k", Kind = ConflictKind.SameFile, Description = "src/A.cs edited by 2 agents", AgentIds = [MainId, SubId] },
            })
            .Get("/api/files", "[]")
            .Get("/api/health", """
                {"overall":"degraded","components":[{"name":"git","state":"degraded","detail":"slow","counters":{}}],
                 "database":{"ok":true,"events":123},"queue":{"depth":0,"dropped":2},"websocket":{"clients":1},"seq":123,
                 "serverTime":"2026-10-07T12:00:00Z","version":"1.0.0.0"}
                """);
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Requests_carry_the_token_and_no_origin()
    {
        Assert.Equal(0, await _h.Run("agents"));
        Assert.All(_h.Daemon.Requests, r => Assert.Equal("test-token", r.Token));
        Assert.All(_h.Daemon.Requests, r => Assert.Equal("127.0.0.1", r.Uri.Host));
    }

    [Fact]
    public async Task Status_summarises_health_agents_and_conflicts()
    {
        Assert.Equal(0, await _h.Run("status"));
        var o = _h.Out.ToString();
        Assert.Contains("degraded", o);
        Assert.Contains("123 events", o);
        Assert.Contains("dropped 2", o);
        Assert.Contains("1 client(s)", o);
        Assert.Contains("2 total, 2 active", o);
        Assert.Contains("2 running", o);
        Assert.Contains("same_file", o);
        Assert.Contains("[c441aaaa, a319bbbb]", o);
    }

    [Fact]
    public async Task Json_flag_prints_raw_api_json()
    {
        Assert.Equal(0, await _h.Run("agents", "--json"));
        var arr = JsonNode.Parse(_h.Out.ToString())!.AsArray();
        Assert.Equal(2, arr.Count);
        Assert.Equal(MainId, arr[0]!["id"]!.ToString());
        Assert.Equal("running", arr[0]!["status"]!.ToString());
    }

    [Fact]
    public async Task Inspect_escapes_sub_agent_ids_in_the_path()
    {
        var detail = new JsonObject
        {
            ["agent"] = Json.ToObject(Agent(SubId, "Write billing tests", parent: MainId)),
            ["capabilities"] = JsonNode.Parse("""{"messageTransports":["hook-mailbox"],"controls":{"pause":"Sub-agents share the parent's process","terminate":null}}"""),
        };
        _h.Daemon.Get("/api/agents/" + Uri.EscapeDataString(SubId), detail.ToJsonString());
        Assert.Equal(0, await _h.Run("inspect", "a319"));
        var req = _h.Daemon.Requests.Single(r => r.Uri.AbsolutePath.StartsWith("/api/agents/claude"));
        Assert.Equal("/api/agents/claude%3Ac441aaaa-1111%2Fa319bbbb", req.Uri.AbsolutePath);
        var o = _h.Out.ToString();
        Assert.Contains("hook-mailbox", o);
        Assert.Contains("✗ pause", o);
        Assert.Contains("Sub-agents share the parent's process", o);
        Assert.Contains("✓ terminate", o);
        Assert.Contains("Refactor billing", o); // parent label
        Assert.Contains("agent=claude%3Ac441aaaa-1111%2Fa319bbbb", _h.Daemon.Single("GET", "/api/files").Uri.Query);
    }

    [Fact]
    public async Task Daemon_unreachable_exits_3()
    {
        _h.Daemon.Unreachable = true;
        Assert.Equal(ExitCodes.DaemonUnreachable, await _h.Run("status"));
        Assert.Contains("connection refused", _h.Err.ToString());
    }

    [Fact]
    public async Task Missing_token_file_says_daemon_never_started()
    {
        var ctx = _h.Context() is var c ? new CliContext
        {
            Out = c.Out, Err = c.Err, HttpHandler = c.HttpHandler, SettingsOverride = c.SettingsOverride, Now = c.Now, Env = c.Env, Home = c.Home,
        } : null!;
        Assert.Equal(ExitCodes.DaemonUnreachable, await Amc.Cli.Router.RunAsync(["agents"], ctx, CancellationToken.None));
        Assert.Contains("daemon never started?", _h.Err.ToString());
    }

    [Fact]
    public async Task Unauthorized_is_explained()
    {
        _h.Daemon.On("GET", "/api/agents", _ => (HttpStatusCode.Unauthorized, """{"error":"Missing or invalid token"}"""));
        Assert.Equal(ExitCodes.Error, await _h.Run("agents"));
        Assert.Contains("rejected the token", _h.Err.ToString());
    }

    [Fact]
    public async Task Ambiguous_agent_is_a_usage_error()
    {
        Assert.Equal(ExitCodes.Usage, await _h.Run("inspect", "billing"));
        Assert.Contains("ambiguous", _h.Err.ToString());
    }

    [Fact]
    public async Task Message_reports_transport_and_queued_status_honestly()
    {
        _h.Daemon.On("POST", "/api/agents/" + Uri.EscapeDataString(MainId) + "/messages",
            _ => (HttpStatusCode.Accepted, """{"ok":true,"messageId":"01JMSG","transport":"hook-mailbox","detail":"Queued until the next hook","statusCode":202}"""));
        _h.Daemon.Get("/api/messages", new List<Message> { new() { Id = "01JMSG", AgentId = MainId, Text = "hi", Transport = "hook-mailbox", Status = MessageStatus.Queued } });
        Assert.Equal(0, await _h.Run("message", "c441aaaa-1111", "please", "rebase"));
        var post = _h.Daemon.Single("POST", "/messages");
        Assert.Equal("please rebase", JsonNode.Parse(post.Body!)!["text"]!.ToString());
        var o = _h.Out.ToString();
        Assert.StartsWith("queued via hook-mailbox to c441aaaa", o);
        Assert.Contains("an idle agent is woken immediately", o);
    }

    [Fact]
    public async Task Message_reports_delivered_when_the_store_says_so()
    {
        _h.Daemon.On("POST", "/api/agents/" + Uri.EscapeDataString(MainId) + "/messages",
            _ => (HttpStatusCode.Accepted, """{"ok":true,"messageId":"01JMSG","transport":"pty","detail":"written","statusCode":202}"""));
        _h.Daemon.Get("/api/messages", new List<Message> { new() { Id = "01JMSG", AgentId = MainId, Text = "hi", Transport = "pty", Status = MessageStatus.Delivered } });
        Assert.Equal(0, await _h.Run("message", "c441aaaa-1111", "hi"));
        Assert.StartsWith("delivered via pty", _h.Out.ToString());
    }

    [Fact]
    public async Task Message_rejection_is_an_error()
    {
        _h.Daemon.On("POST", "/api/agents/" + Uri.EscapeDataString(MainId) + "/messages",
            _ => (HttpStatusCode.Conflict, """{"ok":false,"messageId":"","detail":"This agent cannot receive messages: no supported transport","statusCode":409}"""));
        Assert.Equal(ExitCodes.Error, await _h.Run("message", "c441aaaa-1111", "hi"));
        Assert.Contains("no supported transport", _h.Err.ToString());
    }

    [Fact]
    public async Task Pause_posts_without_confirmation()
    {
        _h.Daemon.On("POST", "/api/agents/" + Uri.EscapeDataString(MainId) + "/control", _ => (HttpStatusCode.OK, """{"ok":true,"detail":"SIGSTOP sent to 3 processes"}"""));
        Assert.Equal(0, await _h.Run("pause", "c441aaaa-1111"));
        var body = JsonNode.Parse(_h.Daemon.Single("POST", "/control").Body!)!;
        Assert.Equal("Pause", body["action"]!.ToString());
        Assert.False(body["confirm"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Stop_maps_to_terminate_and_refuses_non_interactively_without_yes()
    {
        Assert.Equal(ExitCodes.Usage, await _h.Run("", false, "stop", "c441aaaa-1111"));
        Assert.DoesNotContain(_h.Daemon.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Stop_requires_typing_the_short_id()
    {
        _h.Daemon.On("POST", "/api/agents/" + Uri.EscapeDataString(MainId) + "/control", _ => (HttpStatusCode.OK, """{"ok":true,"detail":"SIGTERM sent"}"""));
        Assert.Equal(ExitCodes.Error, await _h.Run("y\n", true, "stop", "c441aaaa-1111"));
        Assert.DoesNotContain(_h.Daemon.Requests, r => r.Method == HttpMethod.Post);

        Assert.Equal(0, await _h.Run("c441aaaa\n", true, "stop", "c441aaaa-1111"));
        var body = JsonNode.Parse(_h.Daemon.Single("POST", "/control").Body!)!;
        Assert.Equal("Terminate", body["action"]!.ToString());
        Assert.True(body["confirm"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Kill_with_yes_skips_the_prompt_and_reports_server_refusal()
    {
        _h.Daemon.On("POST", "/api/agents/" + Uri.EscapeDataString(MainId) + "/control",
            _ => (HttpStatusCode.Conflict, """{"ok":false,"detail":"Kill is not supported for this agent","statusCode":409}"""));
        Assert.Equal(ExitCodes.Error, await _h.Run("kill", "c441aaaa-1111", "--yes"));
        var body = JsonNode.Parse(_h.Daemon.Single("POST", "/control").Body!)!;
        Assert.Equal("Kill", body["action"]!.ToString());
        Assert.True(body["confirm"]!.GetValue<bool>());
        Assert.Contains("Kill is not supported", _h.Err.ToString());
    }

    [Fact]
    public async Task Events_builds_the_query_and_prints_oldest_first()
    {
        var events = new List<AgentEvent>
        {
            new() { Seq = 7, Ts = Now, Type = "tool.completed", Source = "hook", AgentId = MainId, Data = Json.ToObject(new ToolCallData("t2", "Edit", "src/A.cs")) },
            new() { Seq = 6, Ts = Now.AddSeconds(-1), Type = "tool.started", Source = "hook", AgentId = MainId, Data = Json.ToObject(new ToolCallData("t2", "Edit", "src/A.cs")) },
        };
        _h.Daemon.Get("/api/events", events);
        Assert.Equal(0, await _h.Run("events", "--agent", "Refactor", "--type", "tool.*", "--since", "10m", "--limit", "5"));
        var q = Uri.UnescapeDataString(_h.Daemon.Single("GET", "/api/events").Uri.Query);
        Assert.Contains($"agent={MainId}", q);
        Assert.Contains("type=tool.*", q);
        Assert.Contains("from=2026-10-07T11:50:00", q);
        Assert.Contains("limit=5", q);
        Assert.Contains("order=desc", q);
        var lines = _h.Out.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Contains("tool.started", lines[0]);
        Assert.Contains("tool.completed", lines[1]);
    }

    [Fact]
    public async Task Logs_requests_command_and_tool_types_for_the_agent()
    {
        _h.Daemon.Get("/api/events", new List<AgentEvent>
        {
            new() { Seq = 3, Ts = Now, Type = EventTypes.CommandCompleted, Source = "hook", AgentId = MainId,
                Data = Json.ToObject(new CommandData("t1", "git status", ExitCode: 0, Stdout: "On branch main")) },
        });
        Assert.Equal(0, await _h.Run("logs", "c441aaaa-1111"));
        var q = Uri.UnescapeDataString(_h.Daemon.Single("GET", "/api/events").Uri.Query);
        Assert.Contains("type=command.completed", q);
        Assert.Contains("type=tool.failed", q);
        Assert.Contains("$ git status", _h.Out.ToString());
        Assert.Contains("│ On branch main", _h.Out.ToString());
    }

    [Fact]
    public async Task Diff_calls_agent_and_worktree_endpoints()
    {
        _h.Daemon.Get("/api/worktrees", new List<Worktree> { new() { Id = "wt:abc123", Root = "/r/app", RepositoryId = "repo:1", Branch = "main" } });
        _h.Daemon.On("GET", "/api/agents/" + Uri.EscapeDataString(MainId) + "/diff", _ => (HttpStatusCode.OK, Json.Serialize(new { diff = "+x\n" })));
        _h.Daemon.On("GET", "/api/worktrees/wt%3Aabc123/diff", _ => (HttpStatusCode.OK, Json.Serialize(new { diff = "-y\n" })));
        Assert.Equal(0, await _h.Run("diff", "c441aaaa-1111"));
        Assert.Equal(0, await _h.Run("diff", "--worktree", "main", "--path", "src/A.cs"));
        Assert.Contains("path=src%2FA.cs", _h.Daemon.Single("GET", "/api/worktrees/wt").Uri.Query);
        Assert.Equal("+x\n-y\n", _h.Out.ToString().ReplaceLineEndings("\n"));
        Assert.Equal(ExitCodes.Usage, await _h.Run("diff", "c441aaaa-1111", "--worktree", "main"));
    }

    [Fact]
    public async Task Doctor_reports_failures_with_remedies()
    {
        Assert.Equal(ExitCodes.Error, await _h.Run("doctor"));
        var o = _h.Out.ToString();
        Assert.Contains("✓ daemon reachable", o);
        Assert.Contains("✓ database", o);
        Assert.Contains("✗ collectors", o);           // git component degraded
        Assert.Contains("✗ claude hooks", o);
        Assert.Contains("agentctl hooks install", o);
        Assert.Contains("✗ curl", o);                 // PATH is empty in the harness
        Assert.Contains("! ingest drops", o);
    }
}
