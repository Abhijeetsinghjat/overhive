using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Amc.Core.Events;
using Amc.Core.State;
using Amc.Daemon.Infrastructure;

namespace Amc.Daemon.Pipeline;

/// <summary>A connected UI client. Each has a bounded outbound queue so one slow browser cannot stall ingest.</summary>
public sealed class ClientConnection
{
    public const int QueueCapacity = 4096;
    public string Id { get; } = Ulid.NewId();
    public Channel<string> Outbox { get; } = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    /// <summary>Set when the client fell behind; the socket loop sends "resync" and the client refetches state.</summary>
    public volatile bool NeedsResync;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Fans out persisted events and entity patches to WebSocket clients. Messages are
/// pre-serialized once per batch, not once per client.
/// </summary>
public sealed class Broadcaster(StateStore state, HealthRegistry health)
{
    public const string HealthName = "websocket";
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();

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
        c.Outbox.Writer.TryComplete();
        health.SetCounter(HealthName, "clients", _clients.Count);
        health.Ok(HealthName, $"{_clients.Count} client(s)");
    }

    public void Publish(IReadOnlyList<AgentEvent> events, IReadOnlyCollection<EntityRef> changed)
    {
        if (_clients.IsEmpty) return;
        var messages = new List<string>(events.Count + changed.Count);
        foreach (var e in events)
            messages.Add(new JsonObject { ["t"] = "event", ["e"] = Json.ToObject(e) }.ToJsonString(Json.Options));
        if (changed.Count > 0)
        {
            var patches = state.Read(s => changed.Select(r => new JsonObject
            {
                ["kind"] = r.Kind,
                ["id"] = r.Id,
                ["value"] = s.Get(r) is { } v ? System.Text.Json.JsonSerializer.SerializeToNode(v, v.GetType(), Json.Options) : null,
            }).ToList());
            messages.Add(new JsonObject { ["t"] = "patch", ["seq"] = state.Read(s => s.Seq), ["items"] = new JsonArray([.. patches]) }
                .ToJsonString(Json.Options));
        }
        SendAll(messages);
    }

    /// <summary>Volatile overlay (process metrics, terminal output, …): not persisted, dropped for slow clients.</summary>
    public void PublishLive(string json)
    {
        foreach (var c in _clients.Values) c.Outbox.Writer.TryWrite(json);
    }

    private void SendAll(List<string> messages)
    {
        foreach (var c in _clients.Values)
        {
            if (c.NeedsResync) continue;
            foreach (var m in messages)
            {
                if (c.Outbox.Writer.TryWrite(m)) continue;
                c.NeedsResync = true;
                health.Increment(HealthName, "resyncs");
                break;
            }
        }
    }
}
