using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Daemon.Pipeline;

/// <summary>
/// Owns the in-memory <see cref="WorldState"/>. Only the ingest pipeline writes; any
/// component may read through <see cref="Read{T}"/>. Readers must not retain references
/// to mutable entities outside the callback; copy what they need.
/// </summary>
public sealed class StateStore
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private WorldState _state = new();
    private WorktreeResolver _resolver = new([]);

    /// <summary>Raised (outside the lock) after a batch was applied.</summary>
    public event Action<IReadOnlyCollection<EntityRef>, IReadOnlyList<AgentEvent>>? Applied;

    public T Read<T>(Func<WorldState, T> f)
    {
        _lock.EnterReadLock();
        try { return f(_state); }
        finally { _lock.ExitReadLock(); }
    }

    public WorktreeResolver Resolver => Volatile.Read(ref _resolver);

    internal void Load(WorldState state)
    {
        _lock.EnterWriteLock();
        try
        {
            _state = state;
            _resolver = new WorktreeResolver(state.Worktrees.Values);
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal HashSet<EntityRef> Apply(IReadOnlyList<AgentEvent> events)
    {
        HashSet<EntityRef> changed;
        _lock.EnterWriteLock();
        try
        {
            changed = Reducer.ApplyBatch(_state, events);
            if (changed.Any(c => c.Kind == EntityKinds.Worktree))
                Volatile.Write(ref _resolver, new WorktreeResolver(_state.Worktrees.Values));
        }
        finally { _lock.ExitWriteLock(); }
        Applied?.Invoke(changed, events);
        return changed;
    }

    /// <summary>Serializes the current state (under read lock). Tests and small states; see <see cref="SerializeStateUtf8"/>.</summary>
    public string SerializeState() => Read(s => Json.Serialize(s));

    /// <summary>
    /// The state as UTF-8 JSON in pooled chunks, serialized under the read lock (no 2-byte-per-char string, no
    /// contiguous array). The caller writes it out after the lock is released, then disposes it.
    /// </summary>
    public Amc.Daemon.Storage.ChunkedBuffer SerializeStateUtf8() => Read(s =>
    {
        var buffer = new Amc.Daemon.Storage.ChunkedBuffer();
        try { System.Text.Json.JsonSerializer.Serialize(buffer, s, Json.Options); return buffer; }
        catch { buffer.Dispose(); throw; }
    });

    /// <summary>Encodes a snapshot (<see cref="Amc.Daemon.Storage.SnapshotCodec"/>) under the read lock. The caller disposes it.</summary>
    public (long Seq, Amc.Daemon.Storage.ChunkedBuffer Encoded) EncodeSnapshot() =>
        Read(s => (s.Seq, Amc.Daemon.Storage.SnapshotCodec.Encode(s)));
}
