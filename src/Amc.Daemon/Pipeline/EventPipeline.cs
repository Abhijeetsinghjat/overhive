using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Amc.Core.Events;
using Amc.Core.Security;
using Amc.Core.State;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Storage;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Pipeline;

/// <summary>Where collectors publish normalized events.</summary>
public interface IEventSink
{
    /// <summary>Non-blocking publish. Returns false (and counts a drop) if the queue is full.</summary>
    bool TryPublish(AgentEvent e);

    /// <summary>Back-pressured publish for low-rate, must-not-drop producers (hooks, user actions).</summary>
    ValueTask PublishAsync(AgentEvent e, CancellationToken ct = default);

    /// <summary>Publishes and waits until the event is persisted and applied (returns null if it was a duplicate).</summary>
    Task<AgentEvent?> PublishAndWaitAsync(AgentEvent e, CancellationToken ct = default);
}

/// <summary>
/// Collectors → bounded channel → redact/cap → batch persist (SQLite) → reduce → broadcast.
/// Single consumer, so persistence order == fold order == seq order.
/// </summary>
public sealed class EventPipeline : BackgroundService, IEventSink
{
    public const string HealthName = "pipeline";
    private const int MaxBatch = 200;
    private static readonly TimeSpan MaxBatchDelay = TimeSpan.FromMilliseconds(50);

    private readonly Channel<(AgentEvent Event, TaskCompletionSource<AgentEvent?>? Done)> _channel;
    private readonly EventStore _store;
    private readonly StateStore _state;
    private readonly Broadcaster _broadcaster;
    private readonly HealthRegistry _health;
    private readonly AmcOptions _options;
    private readonly ILogger<EventPipeline> _log;
    private long _sinceSnapshot;
    private long _dropped;

    public EventPipeline(EventStore store, StateStore state, Broadcaster broadcaster, HealthRegistry health,
        IOptions<AmcOptions> options, ILogger<EventPipeline> log)
    {
        _store = store; _state = state; _broadcaster = broadcaster; _health = health; _options = options.Value; _log = log;
        _channel = Channel.CreateBounded<(AgentEvent, TaskCompletionSource<AgentEvent?>?)>(
            new BoundedChannelOptions(_options.IngestQueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _health.Register(HealthName);
    }

    public long Dropped => Interlocked.Read(ref _dropped);
    public int QueueDepth => _channel.Reader.Count;

    public bool TryPublish(AgentEvent e)
    {
        if (_channel.Writer.TryWrite((e, null))) return true;
        Interlocked.Increment(ref _dropped);
        _health.Increment(HealthName, "dropped");
        _health.Degraded(HealthName, $"ingest queue full; {Dropped} event(s) dropped");
        return false;
    }

    public ValueTask PublishAsync(AgentEvent e, CancellationToken ct = default) => _channel.Writer.WriteAsync((e, null), ct);

    public async Task<AgentEvent?> PublishAndWaitAsync(AgentEvent e, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<AgentEvent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync((e, tcs), ct);
        return await tcs.Task.WaitAsync(ct);
    }

    /// <summary>Loads the latest snapshot and replays the tail of the log. Called once at startup before collectors run.</summary>
    public void Restore()
    {
        var sw = Stopwatch.StartNew();
        var state = _store.LoadLatestSnapshot();
        if (state is not null && state.ReducerVersion != Reducer.Version)
        {
            _log.LogInformation("Snapshot from reducer v{Old} ignored (current v{New}); replaying the full event log", state.ReducerVersion, Reducer.Version);
            state = null;
        }
        state ??= new WorldState();
        var changed = new HashSet<EntityRef>();
        long replayed = 0;
        foreach (var e in _store.ReadAll(state.Seq))
        {
            Reducer.Apply(state, e, changed);
            replayed++;
        }
        ConflictDetector.Recompute(state, changed);
        Amc.Core.Verification.VerificationFold.Recompute(state, changed);
        state.ReducerVersion = Reducer.Version;
        _state.Load(state);
        _sinceSnapshot = replayed;
        _log.LogInformation("Restored state at seq {Seq} ({Replayed} events replayed) in {Ms} ms", state.Seq, replayed, sw.ElapsedMilliseconds);
        _health.Ok(HealthName, $"restored at seq {state.Seq}");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var batch = new List<(AgentEvent Event, TaskCompletionSource<AgentEvent?>? Done)>(MaxBatch);
        var reader = _channel.Reader;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(ct)) break;
                var deadline = DateTime.UtcNow + MaxBatchDelay;
                while (batch.Count < MaxBatch)
                {
                    if (reader.TryRead(out var item)) { batch.Add(item); continue; }
                    var wait = deadline - DateTime.UtcNow;
                    if (wait <= TimeSpan.Zero) break;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(wait);
                    try { if (!await reader.WaitToReadAsync(cts.Token)) break; }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                }
                Process(batch);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Pipeline batch failed ({Count} events)", batch.Count);
                _health.Failed(HealthName, "batch failed: " + ex.Message);
                foreach (var (_, done) in batch) done?.TrySetException(ex);
                await Task.Delay(500, ct);
            }
            finally { batch.Clear(); }
        }
        if (_sinceSnapshot > 0) SaveSnapshot();
    }

    private void Process(List<(AgentEvent Event, TaskCompletionSource<AgentEvent?>? Done)> batch)
    {
        var normalized = batch.Select(b => Normalize(b.Event)).ToList();
        var stored = _store.Append(normalized);
        var storedById = stored.ToDictionary(e => e.Id);

        if (stored.Count > 0)
        {
            var changed = _state.Apply(stored);
            _broadcaster.Publish(stored, changed);
            _sinceSnapshot += stored.Count;
            if (_sinceSnapshot >= _options.SnapshotEveryEvents)
            {
                SaveSnapshot();
                _sinceSnapshot = 0;
            }
        }
        foreach (var (e, done) in batch) done?.TrySetResult(storedById.GetValueOrDefault(e.Id));

        _health.Increment(HealthName, "ingested", stored.Count);
        _health.Increment(HealthName, "duplicates", normalized.Count - stored.Count);
        _health.SetCounter(HealthName, "queue", _channel.Reader.Count);
        _health.SetCounter(HealthName, "dropped", Dropped);
        if (Dropped == 0) _health.Ok(HealthName, $"seq {_state.Read(s => s.Seq)}");
    }

    /// <summary>One serialization, inside the read lock (the live state already carries the reducer version).</summary>
    private void SaveSnapshot()
    {
        var (seq, json) = _state.Read(s => (s.Seq, Json.Serialize(s)));
        _store.SaveSnapshot(seq, json);
    }

    /// <summary>Redact secrets and cap every string in the payload. Applied centrally so no collector can forget.</summary>
    public static AgentEvent Normalize(AgentEvent e)
    {
        var data = (JsonObject)e.Data.DeepClone();
        RedactNode(data);
        return e with { Data = data, Ts = e.Ts.ToUniversalTime() };
    }

    private static void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s))
                        o[key] = Redactor.RedactAndCap(s);
                    else RedactNode(o[key]);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s)) a[i] = Redactor.RedactAndCap(s);
                    else RedactNode(a[i]);
                }
                break;
        }
    }
}
