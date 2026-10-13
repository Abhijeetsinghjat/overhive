using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Pipeline;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("amc-test-").FullName;
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class EventStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private EventStore NewStore() => new(System.IO.Path.Combine(_dir.Path, "amc.db"));

    private static AgentEvent Ev(string type, string? dedupe = null, string? agent = null) => new()
    {
        Type = type, Source = EventSources.Hook, DedupeKey = dedupe, AgentId = agent,
        Data = Json.ToObject(new AgentTextData("x")),
    };

    [Fact]
    public void Dedupe_key_stores_a_fact_once()
    {
        using var store = NewStore();
        var stored = store.Append([Ev("tool.started", "tool:1:start"), Ev("tool.started", "tool:1:start"), Ev("agent.output")]);
        Assert.Equal(2, stored.Count);
        Assert.Empty(store.Append([Ev("tool.started", "tool:1:start")]));
        Assert.Equal(new long[] { 1, 2 }, stored.Select(e => e.Seq));
    }

    [Fact]
    public void Query_filters_and_pages()
    {
        using var store = NewStore();
        store.Append([.. Enumerable.Range(0, 50).Select(i => Ev(i % 2 == 0 ? "file.modified" : "git.commit_created", agent: i % 3 == 0 ? "a" : "b"))]);
        Assert.Equal(25, store.Query(new EventQuery(Types: ["file.modified"], Limit: 100)).Count);
        Assert.Equal(25, store.Query(new EventQuery(TypePrefix: "git.", Limit: 100)).Count);
        Assert.Equal(17, store.Query(new EventQuery(AgentId: "a", Limit: 100)).Count);
        var page = store.Query(new EventQuery(After: 40, Limit: 5));
        Assert.Equal(new long[] { 41, 42, 43, 44, 45 }, page.Select(e => e.Seq));
        Assert.Equal(50, store.Query(new EventQuery(Descending: true, Limit: 1))[0].Seq);
    }

    [Fact]
    public void Survives_reopen_with_snapshot_and_cursors()
    {
        using (var store = NewStore())
        {
            store.Append([Ev("agent.output")]);
            store.SaveSnapshot(new WorldState { Seq = 1 });
            store.SetCursor("transcript:/x", "123");
        }
        using var reopened = NewStore();
        Assert.Equal(1, reopened.LatestSeq());
        Assert.Equal(1, reopened.LoadLatestSnapshot()!.Seq);
        Assert.Equal("123", reopened.GetCursor("transcript:/x"));
    }

    public void Dispose() => _dir.Dispose();
}

public class PipelineTests : IDisposable
{
    private readonly TempDir _dir = new();

    private (EventPipeline Pipeline, StateStore State, EventStore Store) Build(int snapshotEvery = 2000, int capacity = 20_000)
    {
        var opts = Options.Create(new AmcOptions { DataDir = _dir.Path, SnapshotEveryEvents = snapshotEvery, IngestQueueCapacity = capacity });
        var store = new EventStore(opts.Value.DbPath);
        var state = new StateStore();
        var health = new HealthRegistry();
        var p = new EventPipeline(store, state, new Broadcaster(state, health), health, opts, NullLogger<EventPipeline>.Instance);
        p.Restore();
        return (p, state, store);
    }

    [Fact]
    public async Task Restart_restores_identical_state_from_snapshot_plus_tail()
    {
        string before;
        {
            var (p, state, store) = Build(snapshotEvery: 7);
            await p.StartAsync(CancellationToken.None);
            for (var i = 0; i < 20; i++)
                await p.PublishAndWaitAsync(new AgentEvent
                {
                    Type = EventTypes.AgentCreated, Source = EventSources.Registry, AgentId = $"claude:s{i}",
                    Data = Json.ToObject(new AgentCreatedData(AgentKind.Main, "claude", Pid: 1000 + i)),
                });
            before = state.SerializeState();
            await p.StopAsync(CancellationToken.None);
            store.Dispose();
        }
        var (_, restored, store2) = Build();
        Assert.Equal(before, restored.SerializeState());
        Assert.Equal(20, restored.Read(s => s.Agents.Count));
        store2.Dispose();
    }

    [Fact]
    public async Task Secrets_are_redacted_before_persistence()
    {
        var (p, _, store) = Build();
        await p.StartAsync(CancellationToken.None);
        var stored = await p.PublishAndWaitAsync(new AgentEvent
        {
            Type = EventTypes.CommandStarted, Source = EventSources.Hook, AgentId = "a",
            Data = Json.ToObject(new CommandData("t1", "curl -H 'Authorization: Bearer abcdefghijklmnopqrstuvwxyz'")),
        });
        Assert.NotNull(stored);
        var raw = store.Query(new EventQuery())[0].Data.ToJsonString();
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", raw);
        await p.StopAsync(CancellationToken.None);
        store.Dispose();
    }

    [Fact]
    public async Task Thousands_of_events_are_ingested_in_order_without_loss()
    {
        var (p, state, store) = Build();
        await p.StartAsync(CancellationToken.None);
        await p.PublishAndWaitAsync(new AgentEvent
        {
            Type = EventTypes.AgentCreated, Source = EventSources.Hook, AgentId = "a",
            Data = Json.ToObject(new AgentCreatedData(AgentKind.Main, "claude")),
        });
        const int n = 5000;
        for (var i = 0; i < n; i++)
            Assert.True(p.TryPublish(new AgentEvent
            {
                Type = EventTypes.ToolStarted, Source = EventSources.Hook, AgentId = "a", DedupeKey = $"tool:{i}:start",
                Data = Json.ToObject(new ToolCallData($"{i}", "Read")),
            }));
        await p.PublishAndWaitAsync(new AgentEvent { Type = EventTypes.AgentOutput, Source = EventSources.Hook, AgentId = "a", Data = Json.ToObject(new AgentTextData("end")) });

        Assert.Equal(n + 2, store.LatestSeq());
        Assert.Equal(n, state.Read(s => s.Agents["a"].ToolCount));
        Assert.Equal(0, p.Dropped);
        await p.StopAsync(CancellationToken.None);
        store.Dispose();
    }

    [Fact]
    public void Full_queue_counts_drops_instead_of_blocking()
    {
        var (p, _, store) = Build(capacity: 3); // pipeline not started: nothing drains
        var accepted = Enumerable.Range(0, 10).Count(_ => p.TryPublish(new AgentEvent { Type = "x", Source = "test" }));
        Assert.Equal(3, accepted);
        Assert.Equal(7, p.Dropped);
        store.Dispose();
    }

    public void Dispose() => _dir.Dispose();
}
