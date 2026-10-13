using System.Collections.Concurrent;

namespace Amc.Daemon.Infrastructure;

public enum HealthState { Starting, Ok, Degraded, Failed, Disabled }

public sealed record ComponentHealth(
    string Name, HealthState State, string? Detail, DateTimeOffset? LastSuccessAt, DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, long> Counters);

/// <summary>
/// The observer observes itself: every collector reports here. A collector that has
/// not reported success within its expected interval is shown as degraded, so a
/// silently stalled monitor is visible.
/// </summary>
public sealed class HealthRegistry
{
    private sealed class Entry
    {
        public HealthState State = HealthState.Starting;
        public string? Detail;
        public DateTimeOffset? LastSuccess;
        public DateTimeOffset Updated = DateTimeOffset.UtcNow;
        public TimeSpan? StaleAfter;
        public readonly ConcurrentDictionary<string, long> Counters = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Register a component. <paramref name="staleAfter"/>: max expected gap between successes.</summary>
    public void Register(string name, TimeSpan? staleAfter = null) => _entries.GetOrAdd(name, _ => new Entry()).StaleAfter = staleAfter;

    public void Ok(string name, string? detail = null)
    {
        var e = _entries.GetOrAdd(name, _ => new Entry());
        e.State = HealthState.Ok; e.Detail = detail; e.LastSuccess = e.Updated = DateTimeOffset.UtcNow;
    }

    public void Degraded(string name, string detail) => Set(name, HealthState.Degraded, detail);
    public void Failed(string name, string detail) => Set(name, HealthState.Failed, detail);
    public void Disabled(string name, string detail) => Set(name, HealthState.Disabled, detail);

    public void Increment(string name, string counter, long by = 1) =>
        _entries.GetOrAdd(name, _ => new Entry()).Counters.AddOrUpdate(counter, by, (_, v) => v + by);

    public void SetCounter(string name, string counter, long value) =>
        _entries.GetOrAdd(name, _ => new Entry()).Counters[counter] = value;

    private void Set(string name, HealthState s, string detail)
    {
        var e = _entries.GetOrAdd(name, _ => new Entry());
        e.State = s; e.Detail = detail; e.Updated = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<ComponentHealth> Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return _entries.OrderBy(kv => kv.Key).Select(kv =>
        {
            var e = kv.Value;
            var state = e.State;
            var detail = e.Detail;
            if (state == HealthState.Ok && e.StaleAfter is { } stale && e.LastSuccess is { } last && now - last > stale)
            {
                state = HealthState.Degraded;
                detail = $"no successful run for {(int)(now - last).TotalSeconds}s (expected every {(int)stale.TotalSeconds}s)";
            }
            return new ComponentHealth(kv.Key, state, detail, e.LastSuccess, e.Updated,
                new Dictionary<string, long>(e.Counters));
        }).ToList();
    }

    public HealthState Overall() =>
        Snapshot().Where(c => c.State != HealthState.Disabled).Select(c => c.State)
            .DefaultIfEmpty(HealthState.Ok)
            .Max(s => s switch { HealthState.Failed => 3, HealthState.Degraded => 2, HealthState.Starting => 1, _ => 0 }) switch
        {
            3 => HealthState.Failed, 2 => HealthState.Degraded, 1 => HealthState.Starting, _ => HealthState.Ok,
        };
}
