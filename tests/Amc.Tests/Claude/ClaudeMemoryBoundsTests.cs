using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon;
using Amc.Daemon.Messaging;
using Amc.Daemon.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Amc.Tests.Claude;

/// <summary>In-memory state of the Claude module stays bounded while the daemon runs for weeks.</summary>
public sealed class ClaudeMemoryBoundsTests(ITestOutputHelper output) : IDisposable
{
    private readonly ClaudeHarness _h = new();
    private const string S = ClaudeHarness.SessionId;
    private static readonly string Main = Ids.MainAgent("claude", S);

    public void Dispose() => _h.Dispose();

    private static JsonObject H(string evt, params (string, JsonNode?)[] f) => ClaudeHarness.Hook(evt, S, f);
    private Origin O(string source = EventSources.Hook) => new(source, S, Main, _h.Time.Now, "/work/repo");

    private static JsonObject BigWrite(int i, int kb) => new()
    {
        ["file_path"] = $"/work/repo/src/F{i}.cs",
        ["content"] = string.Join('\n', Enumerable.Repeat(new string('x', 99), kb * 1024 / 100)) + "\n",
    };

    [Fact]
    public async Task Remembered_tool_input_keeps_only_what_the_end_reads_and_is_dropped_after_the_grace()
    {
        var input = BigWrite(1, 64);
        input["old_string"] = new string('o', 50_000);
        input["edits"] = new JsonArray(new JsonObject { ["old_string"] = "a", ["new_string"] = "b" });
        await _h.Mapper.ToolStartAsync(O(EventSources.Transcript), "Write", "toolu_w", input);

        var kept = _h.Context.ToolUses["toolu_w"].Input;
        Assert.Equal("/work/repo/src/F1.cs", kept["file_path"]!.GetValue<string>());
        Assert.Equal(ClaudePayloadTrim.LineCount(input["content"]!.GetValue<string>()), kept[ClaudePayloadTrim.ContentLinesKey]!.GetValue<int>());
        Assert.True(kept["content"]!.GetValue<string>().Length <= ClaudePayloadTrim.MaxInputChars);
        Assert.Equal(ClaudePayloadTrim.MaxInputChars, kept["old_string"]!.GetValue<string>().Length);
        Assert.False(kept.ContainsKey("edits"));

        // The end (transcript: no input of its own) still counts the created file's lines from the compacted input.
        var events = await _h.Mapper.ToolEndAsync(O(EventSources.Transcript), null, "toolu_w", null,
            new JsonObject { ["type"] = "create" }, null, false);
        var created = Assert.Single(events, e => e.Type == EventTypes.FileCreated).Payload<FileChangeData>()!;
        Assert.Equal(655, created.Added);

        // Ended: kept for the grace (a second source's end maps the same way), then forgotten.
        Assert.NotNull(_h.Context.ToolUses["toolu_w"].EndedAt);
        _h.Time.Now += ClaudeContext.ToolEndGrace + TimeSpan.FromSeconds(1);
        _h.Context.SweepToolUses(_h.Time.Now);
        Assert.Empty(_h.Context.ToolUses);
    }

    [Fact]
    public async Task An_end_seen_before_its_start_leaves_the_late_start_already_ended()
    {
        await _h.Mapper.ToolEndAsync(O(), "Bash", "toolu_late", new JsonObject { ["command"] = "ls" }, new JsonObject { ["stdout"] = "" }, null, false);
        await _h.Mapper.ToolStartAsync(O(EventSources.Transcript), "Bash", "toolu_late", new JsonObject { ["command"] = "ls" });
        Assert.NotNull(_h.Context.ToolUses["toolu_late"].EndedAt);
        _h.Time.Now += ClaudeContext.ToolEndGrace + TimeSpan.FromSeconds(1);
        _h.Context.SweepToolUses(_h.Time.Now);
        Assert.Empty(_h.Context.ToolUses);
    }

    [Fact]
    public async Task Tool_starts_without_an_end_are_capped()
    {
        for (var i = 0; i < ClaudeContext.MaxToolUses + 500; i++)
            _h.Context.RememberToolUse(new ToolUseInfo($"t{i}", "Read", new JsonObject { ["file_path"] = "/x" }, Main, S, _h.Time.Now, null, false, null));
        Assert.InRange(_h.Context.ToolUses.Count, 1, ClaudeContext.MaxToolUses);
        Assert.True(_h.Context.ToolUses.ContainsKey($"t{ClaudeContext.MaxToolUses + 499}"));
        await Task.CompletedTask;
    }

    /// <summary>Measurement: retained bytes for N Write calls of 64 KB each (the shape that grew before this change).</summary>
    [Fact]
    public async Task Measure_retained_bytes_of_remembered_tool_calls()
    {
        const int n = 500;
        long full = 0, compact = 0;
        for (var i = 0; i < n; i++)
        {
            var input = BigWrite(i, 64);
            full += ClaudePayloadTrim.EstimateBytes(input);
            await _h.Mapper.ToolStartAsync(O(EventSources.Transcript), "Write", $"toolu_{i}", input);
        }
        compact = _h.Context.ToolUses.Values.Sum(t => ClaudePayloadTrim.EstimateBytes(t.Input));
        var beforeEnds = _h.Context.ToolUses.Count;
        for (var i = 0; i < n; i++)
            await _h.Mapper.ToolEndAsync(O(EventSources.Transcript), null, $"toolu_{i}", null, new JsonObject { ["type"] = "update" }, null, false);
        _h.Time.Now += ClaudeContext.ToolEndGrace + TimeSpan.FromSeconds(1);
        _h.Context.SweepToolUses(_h.Time.Now);
        output.WriteLine($"{n} Write calls x 64 KB: full inputs {full / 1024} KB, compacted {compact / 1024} KB ({beforeEnds} entries), after ends + grace {_h.Context.ToolUses.Count} entries");
        Assert.True(compact * 20 < full, $"compacted {compact} vs full {full}");
        Assert.Empty(_h.Context.ToolUses);
    }

    [Fact]
    public async Task Queued_hook_payloads_are_compacted_and_bounded_by_bytes()
    {
        var r = _h.HookReceiver();
        var response = new JsonObject
        {
            ["type"] = "update", ["filePath"] = "/work/repo/a.cs", ["originalFile"] = new string('o', 200_000),
            ["structuredPatch"] = new JsonArray(new JsonObject { ["oldStart"] = 1, ["oldLines"] = 1, ["newStart"] = 1, ["newLines"] = 1, ["lines"] = new JsonArray("-a", "+b") }),
        };
        var input = new JsonObject { ["file_path"] = "/work/repo/a.cs", ["old_string"] = new string('a', 100_000), ["new_string"] = new string('b', 100_000) };
        var p = H("PostToolUse", ("tool_name", "Edit"), ("tool_use_id", "toolu_e"), ("tool_input", input), ("tool_response", response),
            ("last_assistant_message", new string('m', 300_000)));
        var before = ClaudePayloadTrim.EstimateBytes(p);
        await r.HandleAsync(p);
        output.WriteLine($"Edit hook payload: {before / 1024} KB as received, {r.QueuedBytes / 1024} KB queued");
        Assert.InRange(r.QueuedBytes, 1, 200 * 1024);
        Assert.False(((JsonObject)p["tool_response"]!).ContainsKey("originalFile"));
        Assert.Equal(ClaudePayloadTrim.MaxPayloadChars, p["last_assistant_message"]!.GetValue<string>().Length);

        // Mapping the compacted payload gives the same file event as the full one would.
        await r.ProcessAsync(new ClaudeHookReceiver.HookWork(p, _h.Time.Now, false), CancellationToken.None);
        var mod = Assert.Single(_h.Sink.OfType(EventTypes.FileModified)).Payload<FileChangeData>()!;
        Assert.Equal((1, 1), (mod.Added!.Value, mod.Removed!.Value));

        // Byte bound: big payloads stop being queued long before the 10k count bound.
        for (var i = 0; i < 400; i++)
        {
            var big = H("UserPromptSubmit", ("prompt", new string('p', ClaudePayloadTrim.MaxPayloadChars)));
            await r.HandleAsync(big);
        }
        Assert.True(r.QueuedBytes <= ClaudeHookReceiver.MaxQueuedBytes, $"{r.QueuedBytes} queued");
        Assert.Contains(_h.Health.Snapshot(), c => c.Name == ClaudeHookReceiver.HealthName && c.Counters.GetValueOrDefault("dropped") > 0);
    }

    [Fact]
    public async Task Sweep_forgets_ended_and_absent_sessions_but_keeps_live_ones()
    {
        const string other = "5e55a0aa-0000-4000-8000-0000000000bb";
        var r = _h.HookReceiver();
        foreach (var sid in new[] { S, other })
        {
            await r.HandleAndProcessAsync(ClaudeHarness.Hook("SessionStart", sid));
            await r.HandleAndProcessAsync(ClaudeHarness.Hook("UserPromptSubmit", sid, ("prompt", "go")));
            await r.HandleAndProcessAsync(ClaudeHarness.Hook("PreToolUse", sid, ("tool_name", "Agent"), ("tool_use_id", "toolu_sp_" + sid[^2..]),
                ("tool_input", new JsonObject { ["description"] = "d", ["subagent_type"] = "x", ["prompt"] = "p" })));
            await r.HandleAndProcessAsync(ClaudeHarness.Hook("PreToolUse", sid, ("tool_name", "Bash"), ("tool_use_id", "toolu_b_" + sid[^2..]),
                ("tool_input", new JsonObject { ["command"] = "sleep 1" })));
            _h.Context.AddPendingReply(ClaudePaths.MainId(sid), new PendingReply("m1", _h.Time.Now));
        }
        await r.HandleAndProcessAsync(H("SessionEnd", ("reason", "exit")));
        _h.State.Load(_h.Sink.Fold());
        _h.Mailbox.Enqueue(new PendingMessage("m2", Main, "x", _h.Time.Now));
        _h.Mailbox.DrainFor(Main);
        var sweeper = new ClaudeContextSweeper(_h.Context, _h.Mailbox, _h.Health, _h.Time, NullLogger<ClaudeContextSweeper>.Instance);

        Assert.True(sweeper.SweepOnce() > 0);   // empty mailbox queue, announced markers already in state
        Assert.Equal(0, _h.Mailbox.QueueCount);
        Assert.Equal(2, _h.Context.Spawns.Count); // the ended session is still recent

        _h.Time.Now += ClaudeContext.EndedAgentRetention + TimeSpan.FromMinutes(1);
        sweeper.SweepOnce();
        var mainOther = ClaudePaths.MainId(other);
        Assert.Equal(["toolu_sp_bb"], _h.Context.Spawns.Keys);
        Assert.Equal(["toolu_b_bb", "toolu_sp_bb"], _h.Context.ToolUses.Keys.Order());
        Assert.Equal([mainOther], _h.Context.PendingReplies.Keys);
        Assert.Equal(0, _h.Context.Counts()["prompt_keys"]); // older than PromptKeyMaxAge
    }

    [Fact]
    public void Agents_absent_from_state_are_forgotten_only_on_the_second_sweep()
    {
        var ghost = ClaudePaths.MainId("5e55a0aa-0000-4000-8000-0000000000cc");
        _h.Context.MarkAnnounced(ghost);
        _h.Context.Changed(ghost, "transcript", "/t.jsonl");
        _h.Context.Sweep(_h.Time.Now);
        Assert.True(_h.Context.IsKnown(ghost));
        Assert.False(_h.Context.Changed(ghost, "transcript", "/t.jsonl"));
        _h.Context.Sweep(_h.Time.Now);
        Assert.False(_h.Context.IsKnown(ghost));
        Assert.Equal(0, _h.Context.Counts()["last_values"]);
    }

    [Fact]
    public async Task Rewake_hold_timer_is_disposed_when_the_message_wins_and_empty_waiter_sets_are_removed()
    {
        var time = new TimerCountingTime();
        var r = new ClaudeRewake(_h.Mailbox, _h.Sink, _h.Context, _h.Health, time);
        var hook = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = S };
        r.Observe("Stop", S, hook);
        var wait = r.WaitAsync(hook, CancellationToken.None);
        for (var i = 0; i < 200 && r.WaitingCount(Main) == 0; i++) await Task.Delay(10);
        Assert.Equal(1, time.ActiveTimers);

        _h.Mailbox.Enqueue(new PendingMessage("m-1", Main, "hi", DateTimeOffset.UtcNow));
        Assert.Contains("hi", await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, time.ActiveTimers);       // before: the 3,400 s delay lived on after delivery
        Assert.Empty(r.WaitingAgents());
    }

    /// <summary>System time whose timers are counted until disposed.</summary>
    private sealed class TimerCountingTime : TimeProvider
    {
        private int _active;
        public int ActiveTimers => Volatile.Read(ref _active);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _active);
            return new Counted(System.CreateTimer(callback, state, dueTime, period), this);
        }

        private sealed class Counted(ITimer inner, TimerCountingTime owner) : ITimer
        {
            private int _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._active); inner.Dispose(); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
