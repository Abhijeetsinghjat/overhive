using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Guard;
using Amc.Core.Providers;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Guard;

public sealed record GuardPauseResult(string AgentId, bool Ok, string Detail);

/// <summary>
/// Runaway guard: every <see cref="GuardSettings.IntervalSeconds"/> evaluates the rules per root agent
/// (<see cref="GuardEvaluator"/>) and publishes guard.alert / guard.cleared events for level changes only.
/// With <see cref="GuardSettings.AutoPause"/> (off by default) a new critical alert pauses the tree through
/// the normal control path, audited as automatic. It never terminates or kills.
/// </summary>
public sealed class GuardService(StateStore state, IEventSink sink, AgentOperations ops, HealthRegistry health,
    IOptions<AmcOptions> options, TimeProvider clock, ILogger<GuardService> log) : BackgroundService
{
    public const string HealthName = "guard";
    private GuardSettings Settings => options.Value.Guard;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(Settings.IntervalSeconds, 2, 600));
        health.Register(HealthName, interval * 3 + TimeSpan.FromSeconds(30));
        if (!Settings.Enabled)
        {
            var n = await ClearAllAsync("The runaway guard is disabled", ct);
            health.Ok(HealthName, n == 0 ? "disabled" : $"disabled; cleared {n} alert(s)");
            return;
        }
        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            try
            {
                var t = await SweepAsync(clock.GetUtcNow(), ct);
                var open = state.Read(s => s.GuardAlerts.Count);
                health.Ok(HealthName, open == 0 ? "no runaway trees" : $"{open} active alert(s)" + (t.Count > 0 ? $"; {t.Count} change(s) this pass" : ""));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Guard sweep failed");
                health.Degraded(HealthName, "last sweep failed: " + ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One evaluation pass. Returns the transitions published.</summary>
    public async Task<IReadOnlyList<GuardTransition>> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var g = Settings;
        var (plan, sessions) = state.Read(s =>
        {
            var p = GuardEvaluator.Plan(s, now, g);
            return (p, p.Transitions.Select(t => t.RootAgentId).Distinct()
                .ToDictionary(id => id, id => s.Agents.GetValueOrDefault(id)?.SessionId));
        });
        foreach (var t in plan.Transitions)
            await sink.PublishAsync(GuardEvaluator.ToEvent(t, sessions[t.RootAgentId], now), ct);

        if (g.AutoPause && options.Value.EnableControl)
            foreach (var group in plan.Transitions.Where(t => t.IsNewCritical).GroupBy(t => t.RootAgentId))
            {
                var why = "runaway guard (Guard.AutoPause) — " + string.Join("; ", group.Select(t => t.Summary));
                var results = await PauseTreeAsync(group.Key, why, ct);
                log.LogWarning("Runaway guard paused tree {Root}: {Results}", group.Key,
                    string.Join(", ", results.Select(r => $"{r.AgentId}={(r.Ok ? "ok" : r.Detail)}")));
            }
        return plan.Transitions;
    }

    /// <summary>
    /// Pauses a root agent and every descendant that has its own controllable process (sub-agents that run
    /// inside the session's process are covered by pausing the session). Pause is reversible (resume).
    /// </summary>
    public async Task<IReadOnlyList<GuardPauseResult>> PauseTreeAsync(string rootId, string? automaticReason, CancellationToken ct = default)
    {
        var targets = state.Read(s =>
        {
            if (!s.Agents.TryGetValue(rootId, out var root)) return null;
            var list = new List<string>();
            var seen = new HashSet<string> { root.Id };
            var stack = new Stack<string>(root.ChildIds);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id) || !s.Agents.TryGetValue(id, out var a)) continue;
                if (a.Pid is not null && a.Pid != root.Pid && !a.IsTerminal) list.Add(a.Id);
                foreach (var c in a.ChildIds) stack.Push(c);
            }
            return list;
        });
        if (targets is null) return [new GuardPauseResult(rootId, false, "Unknown agent")];

        var results = new List<GuardPauseResult>();
        var r = await ops.ControlAsync(rootId, ControlAction.Pause, false, ct, automaticReason);
        results.Add(new GuardPauseResult(rootId, r.Ok, r.Detail));
        foreach (var id in targets)
        {
            if (ops.Capabilities(id)?.Supports(ControlAction.Pause) != true) continue; // e.g. already paused with its parent
            var cr = await ops.ControlAsync(id, ControlAction.Pause, false, ct, automaticReason);
            results.Add(new GuardPauseResult(id, cr.Ok, cr.Detail));
        }
        return results;
    }

    /// <summary>Marks alerts dismissed (one by key, or all of a root's). Returns how many.</summary>
    public async Task<int> DismissAsync(string? key, string? rootAgentId, CancellationToken ct = default)
    {
        var keys = state.Read(s => s.GuardAlerts.Values
            .Where(a => !a.Dismissed && (a.Key == key || (key is null && a.RootAgentId == rootAgentId)))
            .Select(a => (a.Key, a.RootAgentId, Session: s.Agents.GetValueOrDefault(a.RootAgentId)?.SessionId)).ToList());
        foreach (var (k, root, session) in keys)
            await sink.PublishAndWaitAsync(new AgentEvent
            {
                Type = EventTypes.GuardDismissed, Source = EventSources.User, Evidence = Evidence.Observed,
                AgentId = root, SessionId = session, Ts = clock.GetUtcNow(),
                Data = Json.ToObject(new GuardDismissedData(k)),
            }, ct);
        return keys.Count;
    }

    /// <summary>GET /api/guard, serialized inside the read lock (alerts are mutable entities).</summary>
    public string StatusJson(DateTimeOffset now)
    {
        var g = Settings;
        return state.Read(s => Json.Serialize(new GuardStatus(
            g.Enabled, g.AutoPause, options.Value.EnableControl, now, GuardEvaluator.Describe(g),
            g.Enabled ? GuardEvaluator.Measure(s, now, g) : [],
            s.GuardAlerts.Values.OrderByDescending(a => a.Severity).ThenBy(a => a.RaisedAt).ToList())));
    }

    private async Task<int> ClearAllAsync(string reason, CancellationToken ct)
    {
        var alerts = state.Read(s => s.GuardAlerts.Values
            .Select(a => new GuardTransition(a.RootAgentId, a.Rule, null, a.Severity, a.Value, a.Threshold, a.Evidence, reason)).ToList());
        var now = clock.GetUtcNow();
        foreach (var t in alerts)
            await sink.PublishAsync(GuardEvaluator.ToEvent(t, state.Read(s => s.Agents.GetValueOrDefault(t.RootAgentId)?.SessionId), now), ct);
        return alerts.Count;
    }
}
