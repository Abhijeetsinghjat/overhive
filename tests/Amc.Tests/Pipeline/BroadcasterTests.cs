using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Terminals;

namespace Amc.Tests.Pipeline;

/// <summary>Per-client WebSocket queues stay bounded however slow (or asleep) the client is.</summary>
public sealed class BroadcasterTests
{
    private static List<string> Drain(ClientConnection c)
    {
        var list = new List<string>();
        while (c.TryRead(out var m)) list.Add(m);
        return list;
    }

    private static AgentEvent Ev(int i, int payloadChars) =>
        ClaudeLike(i, new string('x', payloadChars));

    private static AgentEvent ClaudeLike(int i, string text) => AgentEvent.Create(EventTypes.AgentOutput, EventSources.Hook,
        new AgentTextData(text)) with { Seq = i, AgentId = "claude:s" };

    [Fact]
    public void Live_messages_are_latest_wins_per_topic()
    {
        var b = new Broadcaster(new StateStore(), new HealthRegistry(), patchDelay: TimeSpan.Zero);
        var c = b.Connect();
        for (var i = 0; i < 10_000; i++) b.PublishLive("live", $$"""{"t":"live","n":{{i}}}""");
        b.PublishLive("attention", """{"t":"attention","n":1}""");
        b.PublishLive("attention", """{"t":"attention","n":2}""");
        Assert.Equal(1, c.QueuedCount);   // one marker, not 10,002 messages
        var sent = Drain(c);
        Assert.Equal(new[] { """{"t":"attention","n":2}""", """{"t":"live","n":9999}""" }.Order(), sent.Order());
        Assert.False(c.NeedsResync);

        b.PublishLive("live", "x");       // a new value after the drain queues a new marker
        Assert.Equal(["x"], Drain(c));
    }

    [Fact]
    public void Overflowing_client_is_switched_to_resync_and_its_queue_is_freed()
    {
        var health = new HealthRegistry();
        var b = new Broadcaster(new StateStore(), health, patchDelay: TimeSpan.Zero);
        var slow = b.Connect();
        var fast = b.Connect();
        var maxQueued = 0L;
        for (var i = 1; i <= 2_000; i++)
        {
            b.Publish([Ev(i, 10_000)], []);          // ~20 KB per message in memory: 2,000 x 20 KB = 40 MB if unbounded
            maxQueued = Math.Max(maxQueued, slow.QueuedBytes);
            if (i % 10 == 0) Drain(fast);
        }
        Assert.True(maxQueued <= ClientConnection.MaxQueuedBytes, $"{maxQueued} bytes queued");
        Assert.True(slow.NeedsResync);
        Assert.Equal(0, slow.QueuedBytes);
        Assert.Equal([ClientConnection.ResyncMessage], Drain(slow));   // nothing else is kept or added
        b.Publish([Ev(5_000, 10)], []);
        b.PublishLive("live", "x");
        Assert.Empty(Drain(slow));
        Assert.False(fast.NeedsResync);
        Assert.Contains(health.Snapshot(), h => h.Name == Broadcaster.HealthName && h.Counters["resyncs"] == 1);
    }

    [Fact]
    public async Task Patches_are_coalesced_into_one_message_per_burst()
    {
        var b = new Broadcaster(new StateStore(), new HealthRegistry(), patchDelay: TimeSpan.FromMilliseconds(50));
        var c = b.Connect();
        var a = new EntityRef(EntityKinds.Agent, "claude:a");
        for (var i = 0; i < 20; i++) b.Publish([], [a, new EntityRef(EntityKinds.Agent, "claude:" + (i % 3))]);
        Assert.Equal(0, c.QueuedCount);
        for (var i = 0; i < 200 && c.QueuedCount == 0; i++) await Task.Delay(10);
        var msgs = Drain(c);
        var patch = System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(msgs))!;
        Assert.Equal("patch", patch["t"]!.GetValue<string>());
        Assert.Equal(4, patch["items"]!.AsArray().Count);
        Assert.Equal(0, patch["seq"]!.GetValue<long>());
    }

    [Fact]
    public void Terminal_viewer_queue_is_bounded_by_bytes()
    {
        var v = new TerminalHub.Viewer();
        var frame = new byte[TerminalHub.MaxFrameBytes];
        var accepted = 0;
        while (v.TryEnqueue(frame, false)) accepted++;
        Assert.Equal((int)(TerminalHub.MaxViewerQueuedBytes / TerminalHub.MaxFrameBytes), accepted); // was up to 1,024 frames (1 GB)
        Assert.True(v.TryDequeue(out _));
        Assert.True(v.TryEnqueue(frame, false));
    }
}
