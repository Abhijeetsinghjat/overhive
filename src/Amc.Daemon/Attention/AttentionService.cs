using System.Text.Json.Nodes;
using Amc.Core.Attention;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Attention;

/// <summary>The queue as served by GET /api/attention and pushed as {"t":"attention", …} over the WebSocket.</summary>
public sealed record AttentionSnapshot(IReadOnlyList<AttentionItem> Items, int Urgent, int Hidden, DateTimeOffset ServerTime)
{
    public IReadOnlyList<AttentionItem>? HiddenItems { get; init; }
}

/// <summary>
/// Keeps the attention queue current: re-derives it (debounced) after every applied batch and on a timer (for
/// time windows and snooze expiry), pushes it to dashboards when it changes, and feeds notifications.
/// Derivation itself is the pure <see cref="AttentionDeriver"/>; this class only schedules and fans out.
/// </summary>
public sealed class AttentionService : BackgroundService
{
    public const string HealthName = "attention";
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly StateStore _state;
    private readonly Broadcaster _broadcaster;
    private readonly AttentionInbox _inbox;
    private readonly NotificationDispatcher _notifications;
    private readonly IReadOnlyList<IAttentionProvider> _providers;
    private readonly HealthRegistry _health;
    private readonly TimeProvider _clock;
    private readonly ILogger<AttentionService> _log;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private AttentionSnapshot _current;
    private List<AttentionItem> _hidden = [];
    private string _sig = "";

    public AttentionService(StateStore state, Broadcaster broadcaster, AttentionInbox inbox, NotificationDispatcher notifications,
        IEnumerable<IAttentionProvider> providers, HealthRegistry health, TimeProvider clock, ILogger<AttentionService> log)
    {
        _state = state; _broadcaster = broadcaster; _inbox = inbox; _notifications = notifications;
        _providers = [.. providers.Select(p => (IAttentionProvider)new Guarded(p, log))];
        _health = health; _clock = clock; _log = log;
        _current = new AttentionSnapshot([], 0, 0, clock.GetUtcNow());
        _state.Applied += (_, _) => Poke();
    }

    public AttentionSnapshot Current(bool includeHidden = false)
    {
        lock (_gate) return includeHidden ? _current with { HiddenItems = _hidden } : _current;
    }

    /// <summary>Asks for a recompute soon (coalesces bursts of batches).</summary>
    public void Poke()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    /// <summary>Recomputes now; returns the new snapshot. Used by the loop and after dismiss/snooze.</summary>
    public AttentionSnapshot Recompute()
    {
        var now = _clock.GetUtcNow();
        List<(INotifier, NotificationMessage)> sends;
        AttentionSnapshot snap;
        string? push = null;
        lock (_gate)
        {
            var items = _state.Read(s => AttentionDeriver.Derive(s, now, _providers));
            var (visible, hidden) = _inbox.Apply(items, now);
            snap = new AttentionSnapshot(visible, visible.Count(i => i.Priority == AttentionPriority.Urgent), hidden.Count, now);
            _current = snap;
            _hidden = hidden;
            var sig = Json.Serialize(visible) + "|" + hidden.Count;
            if (sig != _sig)
            {
                _sig = sig;
                push = Message(snap);
            }
            sends = _notifications.Observe(visible, now, _broadcaster.ClientCount);
        }
        if (push is not null) _broadcaster.PublishLive("attention", push);
        foreach (var (n, m) in sends) _ = _notifications.SendAsync(n, m, CancellationToken.None);
        _health.Ok(HealthName, $"{snap.Items.Count} item(s), {snap.Urgent} urgent, {snap.Hidden} dismissed/snoozed");
        return snap;
    }

    public static string Message(AttentionSnapshot s)
    {
        var o = Json.ToObject(s);
        o["t"] = "attention";
        return o.ToJsonString(Json.Options);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _health.Register(HealthName, Interval * 4);
        while (!ct.IsCancellationRequested)
        {
            try { Recompute(); }
            catch (Exception ex) { _log.LogError(ex, "Attention recompute failed"); _health.Failed(HealthName, ex.Message); }

            var wait = Interval;
            if (_notifications.NextDue(_clock.GetUtcNow()) is { } due)
            {
                var untilDue = due - _clock.GetUtcNow();
                if (untilDue < wait) wait = untilDue < TimeSpan.Zero ? TimeSpan.Zero : untilDue + TimeSpan.FromMilliseconds(50);
            }
            try
            {
                if (await _signal.WaitAsync(wait, ct)) await Task.Delay(Debounce, _clock, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>A failing plug-in provider must not take the whole queue down: its items are skipped and it is logged.</summary>
    private sealed class Guarded(IAttentionProvider inner, ILogger log) : IAttentionProvider
    {
        private bool _logged;
        public string Name => inner.Name;

        public IEnumerable<AttentionItem> Collect(Amc.Core.State.WorldState state, AttentionContext ctx)
        {
            try { return inner.Collect(state, ctx).ToList(); }
            catch (Exception ex)
            {
                if (!_logged) log.LogError(ex, "Attention provider {Name} failed; its items are skipped", inner.Name);
                _logged = true;
                return [];
            }
        }
    }
}
