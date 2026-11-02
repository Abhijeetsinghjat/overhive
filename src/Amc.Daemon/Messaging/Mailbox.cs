using System.Collections.Concurrent;

namespace Amc.Daemon.Messaging;

public sealed record PendingMessage(string MessageId, string AgentId, string Text, DateTimeOffset QueuedAt);

/// <summary>
/// In-memory queue of messages waiting for a hook to carry them to the agent.
/// Survives daemon restarts through the event log: on startup, messages in state
/// "queued" with transport hook-mailbox are re-enqueued (see MessageService.RestoreQueue).
/// </summary>
public sealed class Mailbox
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PendingMessage>> _queues = new();
    /// <summary>Serializes Enqueue against <see cref="PruneEmpty"/>, so a message never lands in a queue that was just removed.</summary>
    private readonly Lock _gate = new();

    /// <summary>Raised after a message is queued (agent id). Lets transports that can wake an idle agent react at once.</summary>
    public event Action<string>? Enqueued;

    public void Enqueue(PendingMessage m)
    {
        lock (_gate) _queues.GetOrAdd(m.AgentId, _ => new()).Enqueue(m);
        Enqueued?.Invoke(m.AgentId);
    }

    /// <summary>Number of agents with a queue (empty or not).</summary>
    public int QueueCount => _queues.Count;

    /// <summary>Removes the empty per-agent queues (every agent ever messaged otherwise keeps one). Returns how many.</summary>
    public int PruneEmpty()
    {
        var n = 0;
        lock (_gate)
            foreach (var (id, q) in _queues)
                if (q.IsEmpty && _queues.TryRemove(new KeyValuePair<string, ConcurrentQueue<PendingMessage>>(id, q))) n++;
        return n;
    }

    public bool HasPending(string agentId) => _queues.TryGetValue(agentId, out var q) && !q.IsEmpty;

    public int Count(string agentId) => _queues.TryGetValue(agentId, out var q) ? q.Count : 0;

    /// <summary>Takes all pending messages for the agent (atomic per message).</summary>
    public List<PendingMessage> DrainFor(string agentId)
    {
        var list = new List<PendingMessage>();
        if (!_queues.TryGetValue(agentId, out var q)) return list;
        while (q.TryDequeue(out var m)) list.Add(m);
        return list;
    }

    /// <summary>Puts messages back (e.g. the hook response could not be produced).</summary>
    public void Requeue(IEnumerable<PendingMessage> messages)
    {
        foreach (var m in messages) Enqueue(m);
    }
}
