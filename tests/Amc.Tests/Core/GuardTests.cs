using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Guard;
using Amc.Core.State;

namespace Amc.Tests.Core;

public class GuardEvaluatorTests
{
    private readonly EventFactory _f = new();
    private readonly WorldState _s = new();
    private readonly GuardSettings _g = new();

    private void Apply(params AgentEvent[] events) => Reducer.ApplyBatch(_s, events);
    private void Apply(IEnumerable<AgentEvent> events) => Reducer.ApplyBatch(_s, events);

    private GuardMetrics Metrics(DateTimeOffset? now = null) =>
        Assert.Single(GuardEvaluator.Measure(_s, now ?? _f.Now, _g), m => m.RootAgentId == "main");

    private List<GuardTransition> Plan(DateTimeOffset? now = null) => GuardEvaluator.Plan(_s, now ?? _f.Now, _g).Transitions.ToList();

    /// <summary>Plans and folds the resulting events, like the daemon's GuardService does.</summary>
    private List<GuardTransition> Sweep(DateTimeOffset? now = null)
    {
        var t = Plan(now);
        Apply(t.Select(x => GuardEvaluator.ToEvent(x, null, now ?? _f.Now))); // seq 0: folded without a cursor check
        return t;
    }

    private void Subs(int n, string parent = "main", string prefix = "s")
    {
        for (var i = 0; i < n; i++) Apply(_f.Sub($"{prefix}{i}", parent));
    }

    private AgentEvent Usage(string agent, string msg, long input, long output = 0, long cacheWrite = 0, long cacheRead = 0) =>
        _f.Make(EventTypes.AgentUsage, new UsageData(msg, "claude-opus", input, output, cacheWrite, cacheRead), agent);

    [Fact]
    public void Active_sub_agents_warn_at_threshold_and_go_critical()
    {
        Apply(_f.Main("main"));
        Subs(11);
        Assert.Equal(11, Metrics().ActiveSubAgents);
        Assert.Empty(Plan());

        Subs(1, prefix: "x");
        var t = Assert.Single(Plan(), x => x.Rule == GuardRules.ActiveSubAgents);
        Assert.Equal(GuardSeverity.Warn, t.Severity);
        Assert.Equal(12, t.Value);
        Assert.Equal(12, t.Threshold);
        Assert.Equal(Evidence.Reported, t.Evidence); // statuses reported by the tool

        Subs(18, prefix: "y");
        Assert.Equal(GuardSeverity.Critical, Assert.Single(Plan(), x => x.Rule == GuardRules.ActiveSubAgents).Severity);
    }

    [Fact]
    public void Finished_sub_agents_are_not_active_but_count_in_the_spawn_window()
    {
        Apply(_f.Main("main"));
        for (var i = 0; i < 30; i++)
            Apply(_f.Sub($"s{i}", "main"), _f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "done"), $"s{i}"));

        var m = Metrics();
        Assert.Equal(0, m.ActiveSubAgents);
        Assert.Equal(30, m.SpawnedInWindow);
        Assert.Equal(GuardSeverity.Warn, Assert.Single(Plan(), x => x.Rule == GuardRules.SpawnRate).Severity);

        // Ten minutes after the last spawn the window is empty again.
        var later = _f.Now + TimeSpan.FromMinutes(11);
        Assert.Equal(0, Metrics(later).SpawnedInWindow);
        Assert.Empty(Plan(later));
    }

    [Fact]
    public void Depth_counts_levels_of_active_or_recent_sub_agents()
    {
        Apply(_f.Main("main"), _f.Sub("d1", "main"), _f.Sub("d2", "d1"), _f.Sub("d3", "d2"));
        Assert.Equal(3, Metrics().MaxDepth);
        Assert.Empty(Plan());

        Apply(_f.Sub("d4", "d3"));
        Assert.Equal(GuardSeverity.Warn, Assert.Single(Plan(), x => x.Rule == GuardRules.Depth).Severity);
        Apply(_f.Sub("d5", "d4"), _f.Sub("d6", "d5"));
        Assert.Equal(GuardSeverity.Critical, Assert.Single(Plan(), x => x.Rule == GuardRules.Depth).Severity);

        // Long finished deep trees no longer count.
        foreach (var id in new[] { "d1", "d2", "d3", "d4", "d5", "d6" })
            Apply(_f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "done"), id));
        Assert.Equal(0, Metrics(_f.Now + TimeSpan.FromMinutes(11)).MaxDepth);
    }

    [Fact]
    public void Token_rate_counts_the_whole_tree_and_excludes_cache_reads()
    {
        Apply(_f.Main("main"), _f.Sub("s", "main"), _f.Sub("t", "s"));
        // 2.1M counted tokens within the 5-minute window = 420k/min; the cache reads are ignored.
        Apply(Usage("main", "m1", 500_000, 100_000, cacheRead: 50_000_000),
            Usage("s", "m2", 400_000, 100_000, cacheWrite: 300_000),
            Usage("t", "m3", 600_000, 100_000));
        var m = Metrics();
        Assert.Equal(420_000, m.TokensPerMinute);
        Assert.Equal(2_100_000, m.TokensLastHour);
        Assert.Equal(2_100_000, m.SessionTokens);
        var t = Assert.Single(Plan(), x => x.Rule == GuardRules.TokenRate);
        Assert.Equal(GuardSeverity.Warn, t.Severity);
        Assert.Equal(Evidence.Reported, t.Evidence);

        // A streamed response that grows is counted once, by its latest totals.
        Apply(Usage("t", "m3", 600_000, 200_000));
        Assert.Equal(2_200_000, Metrics().TokensLastHour);

        // Outside the rate window, still within the hour.
        var later = _f.Now + TimeSpan.FromMinutes(6);
        Assert.Equal(0, Metrics(later).TokensPerMinute);
        Assert.Equal(2_200_000, Metrics(later).TokensLastHour);
    }

    [Fact]
    public void Tokens_in_the_last_hour_alert_and_expire()
    {
        Apply(_f.Main("main"));
        for (var i = 0; i < 13; i++) Apply(Usage("main", "m" + i, 1_000_000));
        var t = Plan(_f.Now + TimeSpan.FromMinutes(30));
        Assert.Equal(GuardSeverity.Warn, Assert.Single(t, x => x.Rule == GuardRules.TokensLastHour).Severity);
        Assert.DoesNotContain(t, x => x.Rule == GuardRules.TokenRate); // spread out: no burst
        Sweep(_f.Now + TimeSpan.FromMinutes(30));
        // An hour later the usage has left the window: the alert clears (the alert keeps the idle tree measured).
        var cleared = Assert.Single(Sweep(_f.Now + TimeSpan.FromMinutes(61)), x => x.Rule == GuardRules.TokensLastHour);
        Assert.Null(cleared.Severity);
        Assert.Equal(0, cleared.Value);
    }

    [Fact]
    public void Session_budget_is_off_by_default_and_warns_before_it_is_used_up()
    {
        Apply(_f.Main("main"), Usage("main", "m1", 850_000));
        Assert.DoesNotContain(Plan(), x => x.Rule == GuardRules.TokenBudget);

        _g.SessionTokenBudget = 1_000_000;
        var warn = Assert.Single(Sweep(), x => x.Rule == GuardRules.TokenBudget);
        Assert.Equal(GuardSeverity.Warn, warn.Severity);
        Assert.Equal(800_000, warn.Threshold);

        Apply(Usage("main", "m2", 200_000));
        var crit = Assert.Single(Plan(), x => x.Rule == GuardRules.TokenBudget);
        Assert.Equal(GuardSeverity.Critical, crit.Severity);
        Assert.True(crit.IsNewCritical);
        // The budget covers all time: the alert stays long after the burst.
        Sweep();
        Assert.DoesNotContain(Plan(_f.Now + TimeSpan.FromHours(3)), x => x.Rule == GuardRules.TokenBudget);
    }

    [Fact]
    public void Alerts_are_raised_once_and_cleared_with_hysteresis()
    {
        Apply(_f.Main("main"));
        Subs(12);
        Assert.Single(Sweep());
        var alert = Assert.Single(_s.GuardAlerts.Values);
        Assert.Equal(GuardAlert.KeyOf("main", GuardRules.ActiveSubAgents), alert.Key);
        Assert.Equal(GuardSeverity.Warn, alert.Severity);
        Assert.Empty(Sweep()); // same level: nothing new

        Complete("s0"); // 11 active: still within the hysteresis band (≥ 10.8)
        Assert.Empty(Sweep());
        Assert.Single(_s.GuardAlerts);

        Complete("s1"); // 10 active: clears
        var cleared = Assert.Single(Sweep());
        Assert.Null(cleared.Severity);
        Assert.Empty(_s.GuardAlerts);
        Assert.Empty(Sweep());
    }

    [Fact]
    public void Critical_drops_to_warn_before_it_clears()
    {
        _g.ActiveSubAgentsWarn = 2; _g.ActiveSubAgentsCritical = 4;
        Apply(_f.Main("main"));
        Subs(4);
        Assert.Equal(GuardSeverity.Critical, Assert.Single(Sweep()).Severity);
        Complete("s0"); Complete("s1"); // 2 active: below 0.9 × critical, at warn
        var t = Assert.Single(Sweep());
        Assert.Equal(GuardSeverity.Warn, t.Severity);
        Assert.Equal(GuardSeverity.Critical, t.Previous);
        Assert.False(t.IsNewCritical);
        Assert.Equal(GuardSeverity.Warn, _s.GuardAlerts.Values.Single().Severity);
    }

    [Fact]
    public void Dismissed_alert_stays_hidden_until_it_escalates()
    {
        _g.ActiveSubAgentsWarn = 2; _g.ActiveSubAgentsCritical = 4;
        Apply(_f.Main("main"));
        Subs(2);
        Sweep();
        var key = GuardAlert.KeyOf("main", GuardRules.ActiveSubAgents);
        Apply(_f.Make(EventTypes.GuardDismissed, new GuardDismissedData(key), "main", evidence: Evidence.Observed));
        Assert.True(_s.GuardAlerts[key].Dismissed);

        Subs(2, prefix: "x");
        Sweep();
        Assert.Equal(GuardSeverity.Critical, _s.GuardAlerts[key].Severity);
        Assert.False(_s.GuardAlerts[key].Dismissed);
    }

    [Fact]
    public void Alert_for_a_tree_that_disappeared_is_cleared()
    {
        Apply(_f.Make(EventTypes.GuardAlert, new GuardAlertData("ghost", GuardRules.Depth, GuardSeverity.Warn, 5, 4, "deep"), "ghost", evidence: Evidence.Reported));
        var t = Assert.Single(Plan());
        Assert.Equal("ghost", t.RootAgentId);
        Assert.Null(t.Severity);
    }

    [Fact]
    public void Guard_events_are_not_agent_activity()
    {
        Apply(_f.Main("main"));
        Subs(12);
        var before = _s.Agents["main"].LastActivityAt;
        _f.Advance(TimeSpan.FromMinutes(5));
        Sweep(_f.Now);
        Assert.Equal(before, _s.Agents["main"].LastActivityAt);
    }

    [Fact]
    public void Disabled_levels_and_idle_trees_produce_nothing()
    {
        _g.ActiveSubAgentsWarn = 0; _g.ActiveSubAgentsCritical = 0;
        Apply(_f.Main("main"));
        Subs(40);
        Assert.DoesNotContain(Plan(), x => x.Rule == GuardRules.ActiveSubAgents);

        // A finished session with no activity in the last hour is not measured at all.
        Apply(_f.Main("old"), _f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "done"), "old"));
        Assert.DoesNotContain(GuardEvaluator.Measure(_s, _f.Now + TimeSpan.FromHours(2), _g), m => m.RootAgentId == "old");
    }

    [Theory]
    [InlineData(9, null, null)]
    [InlineData(10, null, GuardSeverity.Warn)]
    [InlineData(9, GuardSeverity.Warn, GuardSeverity.Warn)]     // hysteresis band (≥ 9)
    [InlineData(8, GuardSeverity.Warn, null)]
    [InlineData(20, null, GuardSeverity.Critical)]
    [InlineData(18, GuardSeverity.Critical, GuardSeverity.Critical)]
    [InlineData(17, GuardSeverity.Critical, GuardSeverity.Warn)]
    [InlineData(8, GuardSeverity.Critical, null)]
    public void Level_applies_thresholds_with_hysteresis(long value, GuardSeverity? current, GuardSeverity? expected) =>
        Assert.Equal(expected, GuardEvaluator.Level(value, 10, 20, current));

    private void Complete(string id) =>
        Apply(_f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "done"), id));
}
