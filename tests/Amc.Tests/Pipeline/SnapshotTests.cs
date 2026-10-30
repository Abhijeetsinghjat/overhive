using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Storage;
using Amc.Tests.Claude;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Pipeline;

public class SnapshotTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string DbPath => System.IO.Path.Combine(_dir.Path, "amc.db");

    private static WorldState StateWith(long seq, int agents)
    {
        var s = new WorldState { Seq = seq, ReducerVersion = Reducer.Version };
        for (var i = 0; i < agents; i++)
            s.Agents[$"claude:s{i}"] = new Agent { Id = $"claude:s{i}", Provider = "claude", Kind = AgentKind.Main, SessionId = $"s{i}", Title = new string('x', 100) + i };
        return s;
    }

    private void Sql(string sql, params (string Name, object Value)[] ps)
    {
        using var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Compressed_snapshot_round_trips()
    {
        var state = StateWith(42, 500);
        using (var store = new EventStore(DbPath)) store.SaveSnapshot(state);
        using var reopened = new EventStore(DbPath);
        var loaded = reopened.LoadLatestSnapshot()!;
        Assert.Equal(Json.Serialize(state), Json.Serialize(loaded));
        var (count, bytes) = reopened.SnapshotStats();
        Assert.Equal(1, count);
        Assert.True(bytes < Json.Serialize(state).Length / 5, $"{bytes} bytes is not compressed");
    }

    [Fact]
    public void Reads_an_uncompressed_snapshot_from_an_earlier_version_then_replaces_it()
    {
        var old = StateWith(7, 3);
        using (new EventStore(DbPath)) { } // creates the schema
        Sql("INSERT INTO snapshots(seq,created_at,state) VALUES(7,'2026-10-01T00:00:00Z',$s)", ("$s", Json.Serialize(old)));

        using var store = new EventStore(DbPath);
        var loaded = store.LoadLatestSnapshot()!;
        Assert.Equal(Json.Serialize(old), Json.Serialize(loaded));

        store.SaveSnapshot(StateWith(9, 3));
        Assert.Equal(9, store.LoadLatestSnapshot()!.Seq);
        Assert.Equal(1, store.SnapshotStats().Count); // the legacy row is gone: an older daemon would replay instead
    }

    [Fact]
    public void Keeps_two_snapshots_and_falls_back_when_the_newest_is_corrupt()
    {
        using var store = new EventStore(DbPath);
        for (var seq = 1; seq <= 4; seq++) store.SaveSnapshot(StateWith(seq, 2));
        Assert.Equal(EventStore.SnapshotsKept, store.SnapshotStats().Count);

        Sql("INSERT INTO state_snapshots(seq,created_at,state) VALUES(5,'x',$b)", ("$b", new byte[] { (byte)'A', (byte)'M', (byte)'S', 1, 0xFF, 0xFF, 0xFF }));
        Assert.Equal(4, store.LoadLatestSnapshot()!.Seq);
    }

    [Fact]
    public void Chunked_buffer_spans_chunks_and_reads_back()
    {
        var data = Enumerable.Range(0, ChunkedBuffer.ChunkSize * 2 + 123).Select(i => (byte)(i * 7)).ToArray();
        using var b = new ChunkedBuffer();
        b.Write(data.AsSpan(0, 10));
        b.Write(data, 10, data.Length - 10);
        Assert.Equal(data.Length, b.Length);
        Assert.Equal(data, b.ToArray());
        using var ms = new MemoryStream();
        b.OpenRead().CopyTo(ms);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public void Snapshot_is_due_only_after_enough_events_and_enough_time()
    {
        var o = new AmcOptions { SnapshotEveryEvents = 2000, SnapshotMinIntervalMinutes = 5 };
        Assert.False(EventPipeline.SnapshotDue(1999, TimeSpan.FromHours(1), o));
        Assert.False(EventPipeline.SnapshotDue(50_000, TimeSpan.FromSeconds(30), o)); // a burst: no snapshot every 0.5 s
        Assert.True(EventPipeline.SnapshotDue(2000, TimeSpan.FromMinutes(5), o));
    }

    public void Dispose() => _dir.Dispose();
}

public class SnapshotTriggerTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public async Task Pipeline_waits_for_the_interval_and_writes_a_final_snapshot_on_shutdown()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var opts = Options.Create(new AmcOptions { DataDir = _dir.Path, SnapshotEveryEvents = 5, SnapshotMinIntervalMinutes = 5 });
        using var store = new EventStore(opts.Value.DbPath);
        var state = new StateStore();
        var health = new HealthRegistry();
        var p = new EventPipeline(store, state, new Broadcaster(state, health), health, opts, NullLogger<EventPipeline>.Instance, time);
        p.Restore();
        await p.StartAsync(CancellationToken.None);
        async Task Publish(int n)
        {
            for (var i = 0; i < n; i++)
                await p.PublishAndWaitAsync(new AgentEvent { Type = EventTypes.AgentOutput, Source = EventSources.Hook, AgentId = "a", Data = Json.ToObject(new AgentTextData("x")) });
        }

        await Publish(20);
        Assert.Equal(0, store.SnapshotStats().Count); // enough events, not enough time

        time.Now += TimeSpan.FromMinutes(6);
        await Publish(1);
        Assert.Equal(21, store.LoadLatestSnapshot()!.Seq);

        await Publish(3);
        await p.StopAsync(CancellationToken.None);
        Assert.Equal(24, store.LoadLatestSnapshot()!.Seq);
    }

    public void Dispose() => _dir.Dispose();
}

public class RetentionScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SixHours = TimeSpan.FromHours(6);

    [Fact]
    public void First_check_runs()
    {
        Assert.True(RetentionService.Due(T0, null, SixHours));
    }

    [Fact]
    public void Runs_on_wall_clock_time_so_sleep_does_not_postpone_it()
    {
        Assert.False(RetentionService.Due(T0.AddHours(5).AddMinutes(59), T0, SixHours));
        Assert.True(RetentionService.Due(T0.AddHours(6), T0, SixHours));
        Assert.True(RetentionService.Due(T0.AddHours(14), T0, SixHours)); // woke up from a night's sleep: run at the next check
        Assert.True(RetentionService.Due(T0.AddHours(-1), T0, SixHours)); // clock set back
    }
}
