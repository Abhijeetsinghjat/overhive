using Amc.Core.Attention;
using Amc.Core.Security;
using Amc.Daemon.Infrastructure;

namespace Amc.Daemon.Attention;

/// <summary>A short, already redacted notification. <see cref="ItemIds"/> are the attention items it covers.</summary>
public sealed record NotificationMessage(string Title, string Body, AttentionPriority Priority, IReadOnlyList<string> ItemIds);

/// <summary>One notification channel (desktop, ntfy, a fake in tests).</summary>
public interface INotifier
{
    string Name { get; }
    /// <summary>False when the channel is not configured or not supported on this OS; it then receives nothing.</summary>
    bool Enabled { get; }
    /// <summary>True: only notify while no dashboard is connected (the dashboard notifies by itself).</summary>
    bool OnlyWithoutDashboard { get; }
    /// <summary>Delivers once (no retries). Returns false on failure; must not throw.</summary>
    Task<bool> SendAsync(NotificationMessage message, CancellationToken ct);
}

/// <summary>
/// Per-channel rate limit and coalescing. Pure (time is passed in), so it is unit-tested directly.
/// Items wait <c>coalesce</c> for company, then go out together, never closer than <c>minInterval</c>
/// to the previous send and never more than <c>maxPerHour</c> sends per hour. Items that stop needing
/// attention while they wait are dropped (<see cref="Retain"/>), so a prompt answered quickly never notifies.
/// </summary>
public sealed class NotificationThrottle(TimeSpan coalesce, TimeSpan minInterval, int maxPerHour)
{
    public const int MaxPending = 100;
    private readonly List<AttentionItem> _pending = [];
    private readonly Queue<DateTimeOffset> _sent = new();
    private DateTimeOffset? _firstPendingAt;
    private DateTimeOffset? _lastSent;

    public int Pending => _pending.Count;

    public void Offer(IEnumerable<AttentionItem> items, DateTimeOffset now)
    {
        foreach (var i in items)
        {
            if (_pending.Count >= MaxPending || _pending.Any(p => p.Id == i.Id)) continue;
            _pending.Add(i);
            _firstPendingAt ??= now;
        }
    }

    /// <summary>Drops pending items that are no longer in the queue (answered, dismissed, snoozed).</summary>
    public void Retain(IReadOnlySet<string> visibleIds)
    {
        _pending.RemoveAll(i => !visibleIds.Contains(i.Id));
        if (_pending.Count == 0) _firstPendingAt = null;
    }

    /// <summary>When the pending batch may go out (null: nothing pending).</summary>
    public DateTimeOffset? NextDue(DateTimeOffset now)
    {
        if (_firstPendingAt is not { } first) return null;
        var due = first + coalesce;
        if (_lastSent is { } last && last + minInterval > due) due = last + minInterval;
        Expire(now);
        if (_sent.Count >= maxPerHour && _sent.Peek() + TimeSpan.FromHours(1) > due) due = _sent.Peek() + TimeSpan.FromHours(1);
        return due;
    }

    /// <summary>Returns the batch to send now (and records the send), or null if nothing is due.</summary>
    public IReadOnlyList<AttentionItem>? TakeDue(DateTimeOffset now)
    {
        if (NextDue(now) is not { } due || due > now) return null;
        var batch = _pending.ToList();
        _pending.Clear();
        _firstPendingAt = null;
        _lastSent = now;
        _sent.Enqueue(now);
        return batch;
    }

    private void Expire(DateTimeOffset now)
    {
        while (_sent.Count > 0 && now - _sent.Peek() >= TimeSpan.FromHours(1)) _sent.Dequeue();
    }
}

/// <summary>Turns a batch into one short notification text. Titles only (no details), already redacted.</summary>
public static class NotificationFormatter
{
    public const int MaxLines = 5;

    public static NotificationMessage Format(IReadOnlyList<AttentionItem> batch, Func<string?, string> toolName)
    {
        var priority = batch.Min(i => i.Priority);
        string Line(AttentionItem i) => (i.Tool is null ? "" : toolName(i.Tool) + " · ") + i.Title;
        var title = batch.Count == 1
            ? "Overhive: " + (batch[0].Priority == AttentionPriority.Urgent ? "an agent needs you" : "an agent is waiting")
            : $"Overhive: {batch.Count} items need you";
        var lines = batch.Take(MaxLines).Select(Line).ToList();
        if (batch.Count > MaxLines) lines.Add($"+{batch.Count - MaxLines} more");
        // Titles were redacted when derived; redact again because this text may leave the machine (ntfy).
        var body = Redactor.Redact(string.Join("\n", lines.Select(l => AttentionDeriver.Cap(l, 160))));
        return new NotificationMessage(title, body, priority, [.. batch.Select(i => i.Id)]);
    }
}

/// <summary>
/// Decides what to notify: new items (by stable id) at or above the configured priority, per channel through a
/// <see cref="NotificationThrottle"/>. Items present when the daemon starts are treated as already known, so a
/// restart never replays old notifications. Snoozed items notify again when the snooze ends (they reappear).
/// </summary>
public sealed class NotificationDispatcher
{
    public const string HealthName = "notifications";
    private readonly List<(INotifier Notifier, NotificationThrottle Throttle)> _channels;
    private readonly AttentionPriority _min;
    private readonly Func<string?, string> _toolName;
    private readonly HealthRegistry? _health;
    private readonly ILogger? _log;
    private readonly HashSet<string> _known = [];
    private bool _primed;
    private readonly object _gate = new();

    public NotificationDispatcher(IEnumerable<INotifier> notifiers, NotificationOptions options, Func<string?, string> toolName,
        HealthRegistry? health = null, ILogger? log = null)
    {
        _min = options.EffectiveMinPriority;
        _toolName = toolName;
        _health = health;
        _log = log;
        _channels = [.. notifiers.Where(n => n.Enabled).Select(n => (n, new NotificationThrottle(
            TimeSpan.FromSeconds(Math.Max(0, options.CoalesceSeconds)), TimeSpan.FromSeconds(Math.Max(0, options.MinIntervalSeconds)),
            Math.Max(1, options.MaxPerHour))))];
        if (_health is null) return;
        _health.Register(HealthName);
        if (_channels.Count == 0) _health.Disabled(HealthName, "no notification channel configured (browser notifications are set in the dashboard)");
        else _health.Ok(HealthName, "channels: " + string.Join(", ", _channels.Select(c => c.Notifier.Name)));
    }

    public IReadOnlyList<string> Channels => [.. _channels.Select(c => c.Notifier.Name)];

    /// <summary>
    /// Called with the visible queue after every recompute. Returns the sends that are due now; the caller
    /// runs them (so this stays synchronous and testable).
    /// </summary>
    public List<(INotifier Notifier, NotificationMessage Message)> Observe(IReadOnlyList<AttentionItem> visible, DateTimeOffset now, int dashboardClients)
    {
        lock (_gate)
        {
            var ids = visible.Select(i => i.Id).ToHashSet();
            var fresh = visible.Where(i => !_known.Contains(i.Id) && i.Priority <= _min).ToList();
            _known.Clear();
            _known.UnionWith(ids);
            if (!_primed) { _primed = true; fresh.Clear(); }

            var due = new List<(INotifier, NotificationMessage)>();
            foreach (var (notifier, throttle) in _channels)
            {
                throttle.Retain(ids);
                if (fresh.Count > 0 && !(notifier.OnlyWithoutDashboard && dashboardClients > 0)) throttle.Offer(fresh, now);
                if (throttle.TakeDue(now) is { Count: > 0 } batch) due.Add((notifier, NotificationFormatter.Format(batch, _toolName)));
            }
            return due;
        }
    }

    /// <summary>Earliest time a pending batch becomes due, so the caller can wake up for it.</summary>
    public DateTimeOffset? NextDue(DateTimeOffset now)
    {
        lock (_gate) return _channels.Select(c => c.Throttle.NextDue(now)).Where(d => d is not null).Min();
    }

    public async Task SendAsync(INotifier notifier, NotificationMessage message, CancellationToken ct)
    {
        bool ok;
        try { ok = await notifier.SendAsync(message, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ok = false; _log?.LogWarning(ex, "{Channel} notification failed", notifier.Name); }
        if (_health is null) return;
        _health.Increment(HealthName, ok ? notifier.Name + ".sent" : notifier.Name + ".failed");
        if (ok) _health.Ok(HealthName, $"last sent via {notifier.Name} at {DateTimeOffset.UtcNow:HH:mm:ss}Z");
        else _health.Degraded(HealthName, $"{notifier.Name} notification failed (no retry; see the daemon log)");
    }
}
