using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Infrastructure;

namespace Amc.Daemon.Pipeline;

/// <summary>
/// Enforces "never pretend an agent is working without evidence" over time: a sub-agent
/// whose last evidence is older than <see cref="StaleAfter"/> and that never reported an
/// end is moved to Unknown (evidence: inferred). New activity reopens it via the reducer.
/// Main agents are governed by live process/registry evidence instead.
/// </summary>
public sealed class StalenessMonitor(StateStore state, IEventSink sink, HealthRegistry health, TimeProvider clock) : BackgroundService
{
    public const string HealthName = "staleness";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        health.Register(HealthName, Interval * 3);
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            var n = Sweep(clock.GetUtcNow());
            health.Ok(HealthName, n == 0 ? "no stale agents" : $"{n} agent(s) marked unknown");
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public int Sweep(DateTimeOffset now)
    {
        var stale = state.Read(s => s.Agents.Values
            .Where(a => a.Kind == AgentKind.Sub && a.Status is AgentStatus.Running or AgentStatus.Starting or AgentStatus.Created
                        && now - a.LastActivityAt > StaleAfter)
            .Select(a => (a.Id, a.SessionId, a.LastActivityAt)).ToList());
        foreach (var (id, session, last) in stale)
            sink.TryPublish(new AgentEvent
            {
                Type = EventTypes.AgentStatusChanged, Source = EventSources.System, Evidence = Evidence.Inferred,
                AgentId = id, SessionId = session, Ts = now,
                Data = Json.ToObject(new AgentStatusData(AgentStatus.Unknown,
                    $"No evidence of activity for {Age(now - last)} and no completion was recorded")),
            });
        return stale.Count;
    }

    private static string Age(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalMinutes}m";
}
