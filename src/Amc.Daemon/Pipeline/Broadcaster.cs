using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Amc.Core.Events;
using Amc.Core.State;
using Amc.Daemon.Infrastructure;

namespace Amc.Daemon.Pipeline;

/// <summary>
/// A connected UI client. Each has an outbound queue bounded by count and by bytes, so one slow or stalled
/// browser cannot stall ingest or hold unbounded memory. Volatile overlays ("live" process data, "attention")
/// do not queue: each topic is a single latest-wins slot. A client that falls behind is told to resync
/// (its queue is dropped at once; it refetches /api/state and reconnects from its last seq).
/// </summary>
public sealed class ClientConnection
{
    public const int QueueCapacity = 4096;
    /// <summary>Most (estimated) bytes queued for one client before it is switched to resync.</summary>
    public const long MaxQueuedBytes = 8L * 1024 * 1024;
    /// <summary>Sent (once) to a client that fell behind; the socket loop closes after it.</summary>
    public const string ResyncMessage = """{"t":"resync","reason":"client too slow"}""";
    /// <summary>Queue marker meaning "a live slot changed" (compared by reference, never sent).</summary>
    private static readonly string LiveWake = new(['\0', 'l', 'i', 'v', 'e']);

    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = false, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentDictionary<string, string> _live = new();
    private readonly Queue<string> _pendingLive = new(); // reader side only
    private long _queuedBytes;
    private int _liveWakeQueued;
    private int _resync;

    public string Id { get; } = Ulid.NewId();
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Set when the client fell behind; the socket loop sends "resync" and the client refetches state.</summary>
    public bool NeedsResync => Volatile.Read(ref _resync) != 0;

    /// <summary>Estimated bytes currently queued (strings are UTF-16 in memory).</summary>
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    public int QueuedCount => _outbox.Reader.Count;
    public int LiveSlots => _live.Count;

    private static long Size(string s) => 24 + 2L * s.Length;

    /// <summary>Queues an ordered message (event / patch). On overflow the client is switched to resync; returns false.</summary>
    public bool TryEnqueue(string message)
    {
        if (NeedsResync) return false;
        var size = Size(message);
        if (Interlocked.Add(ref _queuedBytes, size) <= MaxQueuedBytes && _outbox.Writer.TryWrite(message)) return true;
        Interlocked.Add(ref _queuedBytes, -size);
        MarkResync();
        return false;
    }

    /// <summary>Replaces the pending value of a volatile topic (latest wins; never more than one per topic).</summary>
    public void SetLive(string topic, string message)
    {
        if (NeedsResync) return;
        _live[topic] = message;
        if (Interlocked.Exchange(ref _liveWakeQueued, 1) == 0 && !TryEnqueue(LiveWake))
            Volatile.Write(ref _liveWakeQueued, 0);
    }

    /// <summary>Switches the client to resync: frees everything queued and leaves only the resync message.</summary>
    public void MarkResync()
    {
        if (Interlocked.Exchange(ref _resync, 1) != 0) return;
        while (_outbox.Reader.TryRead(out var m)) Interlocked.Add(ref _queuedBytes, -Size(m));
        _live.Clear();
        _outbox.Writer.TryWrite(ResyncMessage); // wakes a reader waiting on an empty queue
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken ct) =>
        _pendingLive.Count > 0 ? ValueTask.FromResult(true) : _outbox.Reader.WaitToReadAsync(ct);

    /// <summary>Next message to send (single reader). Live slots are expanded when their marker comes up.</summary>
    public bool TryRead(out string message)
    {
        while (true)
        {
            if (_pendingLive.TryDequeue(out message!)) return true;
            if (!_outbox.Reader.TryRead(out message!)) return false;
            if (!ReferenceEquals(message, ResyncMessage)) Interlocked.Add(ref _queuedBytes, -Size(message));
            if (!ReferenceEquals(message, LiveWake)) return true;
            Volatile.Write(ref _liveWakeQueued, 0); // before taking the slots: a later SetLive queues a new marker
            foreach (var topic in _live.Keys)
                if (_live.TryRemove(topic, out var m)) _pendingLive.Enqueue(m);
        }
    }

    public void Complete() => _outbox.Writer.TryComplete();
}

/// <summary>
/// Fans out persisted events and entity patches to WebSocket clients. Messages are
/// pre-serialized once per batch, not once per client. Patches are coalesced over
/// <see cref="DefaultPatchDelay"/>: entities changed by several batches in a burst are sent
/// once, with their value and the state seq at flush time (the client's seq handling is unchanged).
/// </summary>
public sealed class Broadcaster(StateStore state, HealthRegistry health, TimeProvider? time = null, TimeSpan? patchDelay = null)
{
    public const string HealthName = "websocket";
    public static readonly TimeSpan DefaultPatchDelay = TimeSpan.FromMilliseconds(250);
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _patchDelay = patchDelay ?? DefaultPatchDelay;
    private readonly Lock _patchGate = new();
    private HashSet<EntityRef> _pendingPatch = [];
    private bool _flushScheduled;

    public int ClientCount => _clients.Count;

    public ClientConnection Connect()
    {
        var c = new ClientConnection();
        _clients[c.Id] = c;
        health.SetCounter(HealthName, "clients", _clients.Count);
        health.Ok(HealthName, $"{_clients.Count} client(s)");
        return c;
    }

    public void Disconnect(ClientConnection c)
    {
        _clients.TryRemove(c.Id, out _);
        c.Complete();
        health.SetCounter(HealthName, "clients", _clients.Count);
        health.Ok(HealthName, $"{_clients.Count} client(s)");
    }

    public void Publish(IReadOnlyList<AgentEvent> events, IReadOnlyCollection<EntityRef> changed)
    {
        if (_clients.IsEmpty) return;
        if (events.Count > 0)
            SendAll(events.Select(e => new JsonObject { ["t"] = "event", ["e"] = Json.ToObject(e) }.ToJsonString(Json.Options)).ToList());
        if (changed.Count == 0) return;
        if (_patchDelay <= TimeSpan.Zero) { SendAll([PatchMessage(changed)]); return; }
        lock (_patchGate)
        {
            _pendingPatch.UnionWith(changed);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _ = Task.Delay(_patchDelay, _time).ContinueWith(_ => FlushPatches(), TaskScheduler.Default);
    }

    /// <summary>Sends the coalesced patch now (also used by tests).</summary>
    public void FlushPatches()
    {
        HashSet<EntityRef> refs;
        lock (_patchGate)
        {
            refs = _pendingPatch;
            _pendingPatch = [];
            _flushScheduled = false;
        }
        if (refs.Count > 0 && !_clients.IsEmpty) SendAll([PatchMessage(refs)]);
    }

    private string PatchMessage(IReadOnlyCollection<EntityRef> changed) => state.Read(s =>
    {
        var items = new JsonArray();
        foreach (var r in changed)
            items.Add(new JsonObject
            {
                ["kind"] = r.Kind,
                ["id"] = r.Id,
                ["value"] = s.Get(r) is { } v ? System.Text.Json.JsonSerializer.SerializeToNode(v, v.GetType(), Json.Options) : null,
            });
        return new JsonObject { ["t"] = "patch", ["seq"] = s.Seq, ["items"] = items }.ToJsonString(Json.Options);
    });

    /// <summary>
    /// Volatile overlay (process metrics, attention, …): not persisted, not queued. Each <paramref name="topic"/> keeps only
    /// its latest message per client, so a slow client gets the newest value once instead of every intermediate one.
    /// </summary>
    public void PublishLive(string topic, string json)
    {
        foreach (var c in _clients.Values)
        {
            var wasResync = c.NeedsResync;
            c.SetLive(topic, json);
            if (!wasResync && c.NeedsResync) health.Increment(HealthName, "resyncs");
        }
    }

    private void SendAll(List<string> messages)
    {
        foreach (var c in _clients.Values)
        {
            if (c.NeedsResync) continue;
            foreach (var m in messages)
            {
                if (c.TryEnqueue(m)) continue;
                health.Increment(HealthName, "resyncs");
                break;
            }
        }
    }
}
