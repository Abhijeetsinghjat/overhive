using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon;
using Amc.Daemon.Messaging;
using Amc.Daemon.Providers.Claude;

namespace Amc.Tests.Claude;

public sealed class ClaudeHookTests : IDisposable
{
    private readonly ClaudeHarness _h = new();
    private const string S = ClaudeHarness.SessionId;
    private static readonly string Main = Ids.MainAgent("claude", S);

    public void Dispose() => _h.Dispose();

    private static JsonObject H(string evt, params (string, JsonNode?)[] f) => ClaudeHarness.Hook(evt, S, f);

    private static JsonObject BashInput(string cmd) => new() { ["command"] = cmd, ["description"] = "run" };

    [Fact]
    public async Task Session_prompt_tools_stop_and_end()
    {
        var r = _h.HookReceiver();
        Assert.Null(await r.HandleAndProcessAsync(H("SessionStart", ("source", "startup"))));
        await r.HandleAndProcessAsync(H("UserPromptSubmit", ("prompt", "fix the bug"), ("prompt_id", "p-7")));
        await r.HandleAndProcessAsync(H("PreToolUse", ("tool_name", "Bash"), ("tool_use_id", "toolu_1"), ("tool_input", BashInput("cd /work/repo && pytest"))));
        await r.HandleAndProcessAsync(H("PostToolUse", ("tool_name", "Bash"), ("tool_use_id", "toolu_1"), ("tool_input", BashInput("cd /work/repo && pytest")),
            ("tool_response", new JsonObject { ["stdout"] = "==== 1 failed, 3 passed in 0.12s ====", ["stderr"] = "", ["interrupted"] = false })));
        await r.HandleAndProcessAsync(H("Notification", ("notification_type", "permission_prompt"), ("message", "Claude needs your permission to use Bash")));
        Assert.Equal(AgentStatus.Blocked, _h.Sink.Fold().Agents[Main].Status);
        await r.HandleAndProcessAsync(H("Stop", ("stop_hook_active", false)));

        var state = _h.Sink.Fold();
        var a = state.Agents[Main];
        Assert.Equal(AgentStatus.Waiting, a.Status);
        Assert.Equal("Turn finished; waiting for input", a.StatusReason);
        Assert.Equal("fix the bug", a.LastPrompt);
        Assert.Contains(_h.Sink.Stored, e => e.DedupeKey == $"prompt:{S}:p-7");
        Assert.Contains(_h.Sink.Stored, e => e.Type == EventTypes.CommandStarted && e.Source == EventSources.Hook && e.WorktreeId == FakeWorktreeIndex.WtId("/work/repo"));
        var test = Assert.Single(_h.Sink.OfType(EventTypes.TestFailed)).Payload<TestRunData>()!;
        Assert.Equal((3, 1), (test.Passed!.Value, test.Failed!.Value));
        Assert.Equal("Waiting for permission: Claude needs your permission to use Bash",
            _h.Sink.OfType(EventTypes.AgentStatusChanged).Select(e => e.Payload<AgentStatusData>()!.Reason).First(x => x!.StartsWith("Waiting for")));

        // Live hook activity notifies the git collector: shell start before the command runs, then end.
        Assert.Equal([(ActivityKinds.Shell, ActivityKinds.Start), (ActivityKinds.Shell, ActivityKinds.End)],
            _h.Worktrees.Activity.Select(x => (x.Kind, x.Phase)));

        await r.HandleAndProcessAsync(H("SessionEnd", ("reason", "prompt_input_exit")));
        state = _h.Sink.Fold();
        Assert.Equal(AgentStatus.Completed, state.Agents[Main].Status);
        Assert.Equal("Session ended (prompt_input_exit)", state.Agents[Main].StatusReason);
    }

    [Fact]
    public async Task Sub_agent_hooks_are_attributed_to_the_sub_agent_and_linked_to_the_spawning_call()
    {
        var r = _h.HookReceiver();
        await r.HandleAndProcessAsync(H("PreToolUse", ("tool_name", "Agent"), ("tool_use_id", "toolu_sp"),
            ("tool_input", new JsonObject { ["description"] = "Write docs", ["subagent_type"] = "writer", ["prompt"] = "go" })));
        await r.HandleAndProcessAsync(H("SubagentStart", ("agent_id", "agent-b7"), ("agent_type", "writer")));
        await r.HandleAndProcessAsync(H("PreToolUse", ("agent_id", "b7"), ("agent_type", "writer"), ("tool_name", "Write"), ("tool_use_id", "toolu_w"),
            ("tool_input", new JsonObject { ["file_path"] = "/work/wt-feature/docs/a.md", ["content"] = "x\n" })));
        await r.HandleAndProcessAsync(H("PostToolUse", ("agent_id", "b7"), ("tool_name", "Write"), ("tool_use_id", "toolu_w"),
            ("tool_input", new JsonObject { ["file_path"] = "/work/wt-feature/docs/a.md", ["content"] = "x\n" }),
            ("tool_output", new JsonObject { ["type"] = "create", ["filePath"] = "/work/wt-feature/docs/a.md" })));
        await r.HandleAndProcessAsync(H("SubagentStop", ("agent_id", "b7"), ("agent_type", "writer"), ("stop_hook_active", false)));

        var sub = Ids.SubAgent("claude", S, "b7");
        var spawn = Assert.Single(_h.Sink.OfType(EventTypes.AgentSpawned));
        Assert.Equal(sub, spawn.AgentId);
        Assert.Equal($"spawn:{sub}", spawn.DedupeKey);
        Assert.Equal((Main, "toolu_sp", "Write docs"), (spawn.Payload<AgentCreatedData>()!.ParentId!, spawn.Payload<AgentCreatedData>()!.SpawnToolUseId!, spawn.Payload<AgentCreatedData>()!.Title!));
        var file = Assert.Single(_h.Sink.OfType(EventTypes.FileCreated));
        Assert.Equal((sub, "docs/a.md", "file:toolu_w"), (file.AgentId!, file.FilePath!, file.DedupeKey!));
        Assert.Equal([(ActivityKinds.Write, ActivityKinds.Start), (ActivityKinds.Write, ActivityKinds.End)],
            _h.Worktrees.Activity.Where(a => a.AgentId == sub).Select(x => (x.Kind, x.Phase)));
        var state = _h.Sink.Fold();
        Assert.Equal([sub], state.Agents[Main].ChildIds);
        Assert.Equal(AgentStatus.Completed, state.Agents[sub].Status);
        Assert.Contains(_h.Sink.Stored, e => e.DedupeKey?.StartsWith($"end:{sub}") == true);
    }

    [Fact]
    public async Task Tool_failure_hook()
    {
        var r = _h.HookReceiver();
        await r.HandleAndProcessAsync(H("PreToolUse", ("tool_name", "Edit"), ("tool_use_id", "toolu_e"), ("tool_input", new JsonObject { ["file_path"] = "/work/repo/a.cs" })));
        await r.HandleAndProcessAsync(H("PostToolUseFailure", ("tool_name", "Edit"), ("tool_use_id", "toolu_e"), ("tool_input", new JsonObject { ["file_path"] = "/work/repo/a.cs" }),
            ("error", "String to replace not found")));
        var f = Assert.Single(_h.Sink.OfType(EventTypes.ToolFailed));
        Assert.Equal("String to replace not found", f.Payload<ToolCallData>()!.Error);
        Assert.Empty(_h.Sink.OfType(EventTypes.FileModified));
    }

    [Fact]
    public async Task Hooks_and_transcript_produce_identical_dedupe_keys()
    {
        // Hook path first …
        var r = _h.HookReceiver();
        await r.HandleAndProcessAsync(H("UserPromptSubmit", ("prompt", "Add a greeting feature and run the tests"))); // no prompt_id: text correlation
        foreach (var (tool, id, input, response) in new (string, string, JsonObject, JsonObject)[]
        {
            ("Write", "toolu_write", new() { ["file_path"] = "/work/repo/src/Greeting.cs", ["content"] = "class Greeting\n{\n}\n" }, new() { ["type"] = "create" }),
            ("Bash", "toolu_test", BashInput("cd /work/repo && dotnet test"), new() { ["stdout"] = "Passed!  - Failed:     0, Passed:    12, Skipped:     1, Total:    13", ["stderr"] = "" }),
        })
        {
            await r.HandleAndProcessAsync(H("PreToolUse", ("tool_name", tool), ("tool_use_id", id), ("tool_input", input)));
            await r.HandleAndProcessAsync(H("PostToolUse", ("tool_name", tool), ("tool_use_id", id), ("tool_input", input.DeepClone()), ("tool_response", response)));
        }
        var hookKeys = _h.Sink.Stored.Where(e => e.DedupeKey is not null && !e.DedupeKey.StartsWith("created:")).Select(e => e.DedupeKey!).ToHashSet();

        // … then the transcript of the same session: every fact the hook reported is a duplicate.
        _h.CopyFixtures();
        await _h.Ingestor().ScanAllAsync(CancellationToken.None);
        var transcriptKeys = _h.Sink.All.Where(e => e.Source == EventSources.Transcript && e.DedupeKey is not null).Select(e => e.DedupeKey!).ToHashSet();
        Assert.Subset(transcriptKeys, hookKeys);
        foreach (var k in hookKeys) Assert.Single(_h.Sink.Stored, e => e.DedupeKey == k);
        Assert.Equal(EventSources.Hook, _h.Sink.Stored.Single(e => e.DedupeKey == "file:toolu_write").Source);
        Assert.Single(_h.Sink.OfType(EventTypes.AgentPrompt), e => e.AgentId == Main && e.Payload<AgentTextData>()!.Text.StartsWith("Add a greeting"));
    }

    [Fact]
    public async Task Invalid_payload_is_ignored_and_raw_payloads_are_logged_redacted()
    {
        var r = _h.HookReceiver();
        Assert.Null(await r.HandleAndProcessAsync(new JsonObject { ["foo"] = 1 }));
        await r.HandleAndProcessAsync(H("UserPromptSubmit", ("prompt", "use token=supersecretvalue123 please")));
        var log = await File.ReadAllTextAsync(_h.Options.HookLogPath);
        Assert.Contains("hook_event_name", log);
        Assert.DoesNotContain("supersecretvalue123", log);
    }

    // ---- mailbox --------------------------------------------------------------------------

    private void Queue(string agentId, string id, string text) =>
        _h.Mailbox.Enqueue(new PendingMessage(id, agentId, text, new DateTimeOffset(2026, 10, 7, 9, 15, 30, TimeSpan.Zero)));

    [Fact]
    public async Task Mailbox_delivers_via_PostToolUse_additional_context_and_links_the_reply()
    {
        var r = _h.HookReceiver();
        Queue(Main, "m1", "please also update the README");
        var resp = await r.HandleAndProcessAsync(H("PostToolUse", ("tool_name", "Read"), ("tool_use_id", "t1"),
            ("tool_input", new JsonObject { ["file_path"] = "/work/repo/x" }), ("tool_response", new JsonObject())));
        var hso = resp!["hookSpecificOutput"]!.AsObject();
        Assert.Equal("PostToolUse", (string?)hso["hookEventName"]);
        var ctx = (string)hso["additionalContext"]!;
        Assert.StartsWith("Message from the user via Overhive (sent ", ctx);
        Assert.EndsWith("): please also update the README", ctx);
        Assert.Matches(@"\(sent \d\d:\d\d:\d\d\)", ctx);
        var delivered = Assert.Single(_h.Sink.OfType(EventTypes.MessageDelivered));
        Assert.Equal(("msg:m1:delivered", Transports.HookMailbox), (delivered.DedupeKey!, delivered.Payload<MessageStatusData>()!.Transport));
        Assert.False(_h.Mailbox.HasPending(Main));

        // Next assistant output from that agent is linked as the (inferred) reply.
        var later = new Origin(EventSources.Transcript, S, Main, _h.Time.Now.AddSeconds(3));
        var events = _h.Mapper.Output(later, "Sure, README updated.", "out:x:0");
        var reply = Assert.Single(events, e => e.Type == EventTypes.MessageReceived);
        Assert.Equal(("m1", Evidence.Inferred), (reply.Payload<MessageReplyData>()!.MessageId, reply.Evidence));
        Assert.DoesNotContain(_h.Mapper.Output(later, "again", "out:x:1"), e => e.Type == EventTypes.MessageReceived);
    }

    [Fact]
    public async Task Mailbox_delivers_via_Stop_block_unless_stop_hook_active()
    {
        var r = _h.HookReceiver();
        Queue(Main, "m2", "one more thing");
        Assert.Null(await r.HandleAndProcessAsync(H("Stop", ("stop_hook_active", true))));
        Assert.True(_h.Mailbox.HasPending(Main));
        Assert.Equal(AgentStatus.Waiting, _h.Sink.Fold().Agents[Main].Status);

        var resp = await r.HandleAndProcessAsync(H("Stop", ("stop_hook_active", false)));
        Assert.Equal("block", (string?)resp!["decision"]);
        Assert.Contains("one more thing", (string)resp["reason"]!);
        Assert.Single(_h.Sink.OfType(EventTypes.MessageDelivered));
        Assert.Equal(AgentStatus.Running, _h.Sink.Fold().Agents[Main].Status);
    }

    [Fact]
    public async Task Mailbox_targets_the_sub_agent_on_its_hooks_only()
    {
        var r = _h.HookReceiver();
        var sub = Ids.SubAgent("claude", S, "c9");
        Queue(sub, "m3", "focus on tests");
        Assert.Null(await r.HandleAndProcessAsync(H("PostToolUse", ("tool_name", "Read"), ("tool_use_id", "t2"), ("tool_input", new JsonObject()))));
        var resp = await r.HandleAndProcessAsync(H("SubagentStop", ("agent_id", "agent-c9"), ("agent_type", "x"), ("stop_hook_active", false)));
        Assert.Equal("block", (string?)resp!["decision"]);
        var state = _h.Sink.Fold();
        Assert.Equal(AgentStatus.Running, state.Agents[sub].Status); // not completed: it continues
        Assert.Equal(sub, Assert.Single(_h.Sink.OfType(EventTypes.MessageDelivered)).AgentId);
    }

    [Fact]
    public async Task Prompt_hook_delivers_additional_context()
    {
        var r = _h.HookReceiver();
        Queue(Main, "m4", "hi");
        var resp = await r.HandleAndProcessAsync(H("UserPromptSubmit", ("prompt", "go")));
        Assert.Equal("UserPromptSubmit", (string?)resp!["hookSpecificOutput"]!["hookEventName"]);
    }

    [Fact]
    public async Task SubagentStop_for_internal_helper_without_any_evidence_creates_no_agent()
    {
        var r = _h.HookReceiver();
        await r.HandleAndProcessAsync(H("SubagentStop", ("agent_id", "a586"), ("agent_type", ""), ("stop_hook_active", false)));
        Assert.DoesNotContain(_h.Sink.Fold().Agents.Keys, k => k.EndsWith("a586"));
    }

    [Fact]
    public async Task Resumed_sub_agent_completes_again_instead_of_staying_running()
    {
        var r = _h.HookReceiver();
        var sub = Ids.SubAgent("claude", S, "r1");
        await r.HandleAndProcessAsync(H("SubagentStart", ("agent_id", "r1"), ("agent_type", "general-purpose")));
        await r.HandleAndProcessAsync(H("SubagentStop", ("agent_id", "r1"), ("agent_type", "general-purpose"), ("stop_hook_active", false)));
        Assert.Equal(AgentStatus.Completed, _h.Sink.Fold().Agents[sub].Status);

        // The parent continues the same agent id later: a new run.
        _h.State.Load(_h.Sink.Fold());
        await r.HandleAndProcessAsync(H("SubagentStart", ("agent_id", "r1"), ("agent_type", "general-purpose")));
        await r.HandleAndProcessAsync(H("PreToolUse", ("agent_id", "r1"), ("agent_type", "general-purpose"), ("tool_name", "Read"),
            ("tool_use_id", "toolu_r2"), ("tool_input", new JsonObject { ["file_path"] = "/work/repo/a.cs" })));
        Assert.Equal(AgentStatus.Running, _h.Sink.Fold().Agents[sub].Status);
        await r.HandleAndProcessAsync(H("SubagentStop", ("agent_id", "r1"), ("agent_type", "general-purpose"), ("stop_hook_active", false)));
        Assert.Equal(AgentStatus.Completed, _h.Sink.Fold().Agents[sub].Status);
    }
}
