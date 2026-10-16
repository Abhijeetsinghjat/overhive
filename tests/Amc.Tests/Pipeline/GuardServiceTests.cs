using System.Collections.Concurrent;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Guard;
using Amc.Core.Providers;
using Amc.Daemon.Guard;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Amc.Tests.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Pipeline;

/// <summary>Sink that folds every event into the StateStore immediately (seq assigned), like the pipeline does.</summary>
internal sealed class ApplyingSink(StateStore state) : IEventSink
{
    private long _seq = 1_000_000;
    public ConcurrentQueue<AgentEvent> Events { get; } = new();
    public bool TryPublish(AgentEvent e) { Add(e); return true; }
    public ValueTask PublishAsync(AgentEvent e, CancellationToken ct = default) { Add(e); return ValueTask.CompletedTask; }
    public Task<AgentEvent?> PublishAndWaitAsync(AgentEvent e, CancellationToken ct = default) => Task.FromResult<AgentEvent?>(Add(e));

    private AgentEvent Add(AgentEvent e)
    {
        e = e with { Seq = Interlocked.Increment(ref _seq) };
        Events.Enqueue(e);
        state.Apply([e]);
        return e;
    }
}

/// <summary>Records control calls; main agents can be paused, sub-agents cannot (like Claude Code).</summary>
internal sealed class RecordingProvider : IAgentProvider
{
    public ConcurrentQueue<(string AgentId, ControlAction Action)> Calls { get; } = new();
    public string Id => "claude";

    public AgentCapabilities Capabilities(Agent agent) => new([], Enum.GetValues<ControlAction>().ToDictionary(a => a,
        a => agent.Kind == AgentKind.Sub ? "no per-sub-agent control" : a == ControlAction.Pause && agent.Status == AgentStatus.Paused ? "Already paused" : (string?)null));

    public Task<ControlResult> ControlAsync(Agent agent, ControlAction action, CancellationToken ct)
    {
        Calls.Enqueue((agent.Id, action));
        return Task.FromResult(new ControlResult(true, $"{action} sent to 1 process(es)"));
    }

    public Task<DeliveryResult> SendAsync(Agent agent, OutgoingMessage message, CancellationToken ct) =>
        Task.FromResult(new DeliveryResult(false, false, "none", "n/a"));
}

public class GuardServiceTests
{
    private readonly EventFactory _f = new();
    private readonly StateStore _state = new();
    private readonly ApplyingSink _sink;
    private readonly RecordingProvider _provider = new();
    private readonly AmcOptions _options = new();
    private readonly GuardService _guard;

    public GuardServiceTests()
    {
        _sink = new ApplyingSink(_state);
        var ops = new AgentOperations([_provider], _state, _sink);
        _guard = new GuardService(_state, _sink, ops, new HealthRegistry(), Options.Create(_options), TimeProvider.System,
            NullLogger<GuardService>.Instance);
        // A session that fans out to 31 active sub-agents: critical for "sub-agents at once" (default 30).
        var events = new List<AgentEvent> { _f.Main("main") };
        for (var i = 0; i < 31; i++) events.Add(_f.Sub("s" + i, "main"));
        _state.Apply(events);
    }

    private List<AgentEvent> Audits => _sink.Events.Where(e => e.Type == EventTypes.AuditControl).ToList();

    [Fact]
    public async Task Critical_alert_is_recorded_once_and_nothing_is_paused_by_default()
    {
        var t = await _guard.SweepAsync(_f.Now);
        Assert.Contains(t, x => x.Rule == GuardRules.ActiveSubAgents && x.Severity == GuardSeverity.Critical);
        var alert = _sink.Events.Single(e => e.Type == EventTypes.GuardAlert && e.Payload<GuardAlertData>()!.Rule == GuardRules.ActiveSubAgents);
        Assert.Equal("main", alert.AgentId);
        Assert.Equal(EventSources.System, alert.Source);
        Assert.Equal(Evidence.Reported, alert.Evidence);
        Assert.True(_state.Read(s => s.GuardAlerts.ContainsKey(GuardAlert.KeyOf("main", GuardRules.ActiveSubAgents))));

        var n = _sink.Events.Count;
        Assert.Empty(await _guard.SweepAsync(_f.Now)); // deduped by state
        Assert.Equal(n, _sink.Events.Count);
        Assert.Empty(_provider.Calls);
        Assert.Empty(Audits);
    }

    [Fact]
    public async Task Auto_pause_pauses_the_root_once_and_audits_it_as_automatic()
    {
        _options.Guard.AutoPause = true;
        await _guard.SweepAsync(_f.Now);

        Assert.Equal([("main", ControlAction.Pause)], _provider.Calls.ToArray()); // sub-agents share the session's process
        var audit = Assert.Single(Audits);
        Assert.Equal(EventSources.System, audit.Source);
        var d = audit.Payload<AuditData>()!;
        Assert.Equal("pause", d.Action);
        Assert.Equal("ok", d.Outcome);
        Assert.Contains("Automatic", d.Detail);
        Assert.Contains("runaway guard", d.Detail);
        Assert.Contains("31 sub-agents active", d.Detail);

        // Still critical on the next pass (e.g. after the user resumed): no second pause.
        await _guard.SweepAsync(_f.Now);
        Assert.Single(_provider.Calls);
    }

    [Fact]
    public async Task Auto_pause_needs_control_enabled_and_a_critical_level()
    {
        _options.Guard.AutoPause = true;
        _options.EnableControl = false;
        await _guard.SweepAsync(_f.Now);
        Assert.Empty(_provider.Calls);

        var fresh = new GuardServiceTests();
        fresh._options.Guard.AutoPause = true;
        fresh._options.Guard.ActiveSubAgentsCritical = 0; // only a warning
        await fresh._guard.SweepAsync(fresh._f.Now);
        Assert.Contains(fresh._sink.Events, e => e.Type == EventTypes.GuardAlert);
        Assert.Empty(fresh._provider.Calls);
    }

    [Fact]
    public async Task Skipped_automatic_pause_is_audited_and_automation_can_never_terminate()
    {
        _state.Apply([_f.Status("main", AgentStatus.Paused)]);
        _options.Guard.AutoPause = true;
        await _guard.SweepAsync(_f.Now);
        Assert.Empty(_provider.Calls);
        var audit = Assert.Single(Audits);
        Assert.Equal("skipped", audit.Payload<AuditData>()!.Outcome);

        var ops = new AgentOperations([_provider], _state, _sink);
        var r = await ops.ControlAsync("main", ControlAction.Kill, confirmed: true, default, automaticReason: "test");
        Assert.False(r.Ok);
        Assert.Equal(428, r.StatusCode);
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public async Task Dismiss_hides_alerts_of_a_root_and_status_reports_live_values()
    {
        await _guard.SweepAsync(_f.Now);
        Assert.Equal(2, await _guard.DismissAsync(null, "main")); // sub-agents at once (critical) + spawn rate (warn)
        Assert.All(_state.Read(s => s.GuardAlerts.Values.ToList()), a => Assert.True(a.Dismissed));
        Assert.Equal(0, await _guard.DismissAsync(null, "main"));

        var status = Json.Deserialize<GuardStatus>(_guard.StatusJson(_f.Now))!;
        Assert.True(status.Enabled);
        Assert.False(status.AutoPause);
        Assert.Equal(GuardRules.All.Count, status.Rules.Count);
        var root = Assert.Single(status.Roots);
        Assert.Equal(31, root.ActiveSubAgents);
        Assert.Equal(31, root.SpawnedInWindow);
        Assert.Equal(GuardSeverity.Critical, status.Alerts[0].Severity); // most severe first
        Assert.Equal(2, status.Alerts.Count);
    }
}
