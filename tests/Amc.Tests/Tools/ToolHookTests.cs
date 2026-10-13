using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Providers;
using Amc.Daemon;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Tools;
using Amc.Tests.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Tools;

/// <summary>Payload shapes follow each tool's official hook documentation.</summary>
public class ToolHookTests
{
    private readonly CollectingSink _sink = new();
    private readonly FakeWorktreeIndex _wt = new();
    private readonly Mailbox _mailbox = new();
    private readonly ToolHookReceiver _r;
    private readonly ProtocolInbox _inbox;

    public ToolHookTests()
    {
        var opts = Options.Create(new AmcOptions());
        var tools = new ToolRegistry(opts);
        _inbox = new ProtocolInbox(_mailbox, _sink, TimeProvider.System);
        var collector = new GenericToolCollector(tools, _sink, _wt, new HealthRegistry(), TimeProvider.System, NullLogger<GenericToolCollector>.Instance);
        _r = new ToolHookReceiver(new ProtocolReceiver(tools, _sink, _wt, TimeProvider.System), _inbox, collector, TimeProvider.System,
            NullLogger<ToolHookReceiver>.Instance);
    }

    private Task<JsonObject?> Hook(string tool, string evt, string json) => _r.HandleAsync(tool, evt, JsonNode.Parse(json)!.AsObject(), default);
    private void Queue(string agentId, string text) => _inbox.Enqueue(agentId, new OutgoingMessage("m-" + text, text));

    [Fact]
    public async Task Codex_session_shell_command_patch_and_stop_delivery()
    {
        await Hook("codex", "SessionStart", """{"session_id":"c1","cwd":"/work/repo","hook_event_name":"SessionStart","model":"gpt-5","source":"startup"}""");
        await Hook("codex", "PreToolUse", """{"session_id":"c1","cwd":"/work/repo","tool_name":"Bash","tool_use_id":"t1","tool_input":{"command":"npm test"}}""");
        await Hook("codex", "PostToolUse", """{"session_id":"c1","cwd":"/work/repo","tool_name":"Bash","tool_use_id":"t1","tool_input":{"command":"npm test"},"tool_response":"ok"}""");
        await Hook("codex", "PostToolUse", """{"session_id":"c1","cwd":"/work/repo","tool_name":"apply_patch","tool_use_id":"t2","tool_input":{"input":"*** Begin Patch\n*** Add File: src/new.ts\n+x\n*** Update File: src/old.ts\n*** End Patch"}}""");
        Queue("codex:c1", "focus on tests");
        var reply = await Hook("codex", "Stop", """{"session_id":"c1","cwd":"/work/repo","stop_hook_active":false}""");

        Assert.Equal("block", (string?)reply!["decision"]);
        Assert.Contains("focus on tests", (string?)reply["reason"]);
        var s = _sink.Fold();
        Assert.Equal("codex", s.Agents["codex:c1"].Provider);
        Assert.Equal(1, s.Agents["codex:c1"].ToolCount);
        Assert.Equal(["src/new.ts", "src/old.ts"], s.Files.Values.Select(f => f.Path).Order());
        Assert.Single(_sink.OfType(EventTypes.MessageDelivered));
    }

    [Fact]
    public async Task Gemini_tools_without_ids_usage_and_afteragent_retry()
    {
        await Hook("gemini", "BeforeTool", """{"session_id":"g1","cwd":"/work/repo","tool_name":"run_shell_command","tool_input":{"command":"go test ./..."}}""");
        await Hook("gemini", "AfterTool", """{"session_id":"g1","cwd":"/work/repo","tool_name":"run_shell_command","tool_input":{"command":"go test ./..."},"tool_response":{"llmContent":"PASS"}}""");
        await Hook("gemini", "AfterModel", """{"session_id":"g1","cwd":"/work/repo","llm_request":{"model":"gemini-2.5-pro"},"llm_response":{"usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":40}}}""");
        Queue("gemini:g1", "also update the docs");
        var reply = await Hook("gemini", "AfterAgent", """{"session_id":"g1","cwd":"/work/repo","prompt_response":"done","stop_hook_active":false}""");

        Assert.Equal("deny", (string?)reply!["decision"]);
        var a = _sink.Fold().Agents["gemini:g1"];
        Assert.Equal((1, 0), (a.ToolCount, a.ToolFailures));
        Assert.Equal(40, a.Usage.Output);
        Assert.Equal(AgentStatus.Waiting, a.Status);
    }

    [Fact]
    public async Task Cursor_conversation_file_edit_and_additional_context()
    {
        await Hook("cursor", "afterFileEdit", """{"conversation_id":"k1","workspace_roots":["/work/repo"],"file_path":"/work/repo/app.py"}""");
        Queue("cursor:k1", "use pytest");
        var reply = await Hook("cursor", "postToolUse", """{"conversation_id":"k1","workspace_roots":["/work/repo"],"tool_name":"Shell","tool_input":{"command":"ls"},"tool_output":"x","tool_use_id":"u1"}""");
        Assert.Contains("use pytest", (string?)reply!["additional_context"]);
        Assert.Contains(_sink.Fold().Files.Values, f => f.Path == "app.py");
    }

    [Fact]
    public async Task Copilot_camelCase_and_agentStop_continue()
    {
        await Hook("copilot", "sessionStart", """{"sessionId":"p1","cwd":"/work/repo","source":"new","initialPrompt":"fix lint"}""");
        await Hook("copilot", "preToolUse", """{"sessionId":"p1","cwd":"/work/repo","toolName":"bash","toolArgs":{"command":"npm run lint"}}""");
        await Hook("copilot", "postToolUse", """{"sessionId":"p1","cwd":"/work/repo","toolName":"bash","toolArgs":{"command":"npm run lint"},"toolResult":{"resultType":"success","textResultForLlm":"0 problems"}}""");
        Queue("copilot:p1", "commit when done");
        var reply = await Hook("copilot", "agentStop", """{"sessionId":"p1","cwd":"/work/repo","stopReason":"end_turn"}""");
        Assert.Equal("block", (string?)reply!["decision"]);
        Assert.Equal(1, _sink.Fold().Agents["copilot:p1"].ToolCount);
    }

    [Fact]
    public async Task Grok_is_monitored_but_never_answers_hooks()
    {
        await Hook("grok", "SessionStart", """{"hookEventName":"SessionStart","sessionId":"x1","cwd":"/work/repo"}""");
        Queue("grok:x1", "hello");
        Assert.Null(await Hook("grok", "Stop", """{"hookEventName":"Stop","sessionId":"x1","cwd":"/work/repo"}"""));
        Assert.True(_mailbox.HasPending("grok:x1"));
        Assert.Equal(AgentStatus.Waiting, _sink.Fold().Agents["grok:x1"].Status);
    }

    [Fact]
    public void Install_writes_each_tools_documented_shape_and_uninstall_removes_only_ours()
    {
        var foreign = JsonNode.Parse("""{"hooks":{"PreToolUse":[{"hooks":[{"type":"command","command":"guard.sh"}]}]}}""")!.AsObject();
        var codex = ToolHooksConfig.Install(ToolHooksConfig.Profiles["codex"], foreign, "/s/amcd.sock");
        Assert.True(ToolHooksConfig.IsInstalled(ToolHooksConfig.Profiles["codex"], codex));
        Assert.Contains("hooks/codex?e=Stop", codex.ToJsonString());
        Assert.Equal("guard.sh", (string?)ToolHooksConfig.Uninstall(ToolHooksConfig.Profiles["codex"], codex)["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]);

        var gemini = ToolHooksConfig.Install(ToolHooksConfig.Profiles["gemini"], new JsonObject(), "/s/amcd.sock");
        Assert.Equal(5000, (int)gemini["hooks"]!["AfterTool"]![0]!["hooks"]![0]!["timeout"]!); // milliseconds
        var cursor = ToolHooksConfig.Install(ToolHooksConfig.Profiles["cursor"], new JsonObject(), "/s/amcd.sock");
        Assert.Equal(1, (int)cursor["version"]!);
        Assert.NotNull(cursor["hooks"]!["afterFileEdit"]![0]!["command"]);
        var copilot = ToolHooksConfig.Install(ToolHooksConfig.Profiles["copilot"], new JsonObject(), "/s/amcd.sock");
        Assert.NotNull(copilot["hooks"]!["agentStop"]![0]!["bash"]);
        Assert.Null(ToolHooksConfig.Uninstall(ToolHooksConfig.Profiles["copilot"], copilot)["hooks"]);
    }
}
