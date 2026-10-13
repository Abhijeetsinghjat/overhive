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

    /// <summary>Serializes the current state (under read lock) for snapshots and /api/state.</summary>
    public string SerializeState() => Read(s => Json.Serialize(s));
}
