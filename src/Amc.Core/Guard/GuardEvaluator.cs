using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Core.Guard;

/// <summary>
/// Runaway guard rules, evaluated per root agent over <see cref="WorldState"/> and a wall-clock time.
/// Pure: the daemon's GuardService turns the resulting transitions into events. Dedupe is
/// state-based (an alert already in <see cref="WorldState.GuardAlerts"/> at the same level produces
/// nothing), so a restart never re-raises or re-acts on an alert.
/// </summary>
public static class GuardEvaluator
{
    public static readonly TimeSpan HourWindow = TimeSpan.FromHours(1);

    public static GuardPlan Plan(WorldState s, DateTimeOffset now, GuardSettings g)
    {
        var metrics = Measure(s, now, g);
        var transitions = new List<GuardTransition>();
        var seen = new HashSet<string>();
        foreach (var m in metrics)
        foreach (var rule in GuardRules.All)
        {
            var key = GuardAlert.KeyOf(m.RootAgentId, rule);
            seen.Add(key);
            var current = s.GuardAlerts.GetValueOrDefault(key)?.Severity;
            var (warn, critical) = g.Thresholds(rule);
            var value = m.Value(rule);
            var level = Level(value, warn, critical, current);
            if (level == current) continue;
            transitions.Add(level is { } l
                ? new GuardTransition(m.RootAgentId, rule, l, current, value, l == GuardSeverity.Critical ? critical : warn,
                    EvidenceOf(rule, m), Summarize(rule, value, l == GuardSeverity.Critical ? critical : warn, g))
                : new GuardTransition(m.RootAgentId, rule, null, current, value, warn, EvidenceOf(rule, m),
                    $"Back below the threshold ({Fmt(rule, value)})"));
        }
        // Alerts whose tree is no longer known.
        foreach (var a in s.GuardAlerts.Values)
            if (!seen.Contains(a.Key))
                transitions.Add(new GuardTransition(a.RootAgentId, a.Rule, null, a.Severity, 0, a.Threshold, a.Evidence, "The agent tree is no longer known"));
        return new GuardPlan(metrics, transitions);
    }

    /// <summary>The level a value is at, given the current level (hysteresis: leaving a level needs a drop below <see cref="GuardSettings.ClearRatio"/>).</summary>
    public static GuardSeverity? Level(long value, long warn, long critical, GuardSeverity? current)
    {
        if (critical > 0 && value >= critical) return GuardSeverity.Critical;
        if (current == GuardSeverity.Critical && critical > 0 && value >= critical * GuardSettings.ClearRatio) return GuardSeverity.Critical;
        if (warn > 0 && value >= warn) return GuardSeverity.Warn;
        if (current is not null && warn > 0 && value >= warn * GuardSettings.ClearRatio) return GuardSeverity.Warn;
        return null;
    }

    /// <summary>
    /// Metrics for every root whose tree is relevant now: an active agent, activity or usage in the
    /// last hour, or an existing alert (so it can clear).
    /// </summary>
    public static List<GuardMetrics> Measure(WorldState s, DateTimeOffset now, GuardSettings g)
    {
        var spawnSince = now - TimeSpan.FromMinutes(Math.Max(0.1, g.SpawnWindowMinutes));
        var rateWindow = TimeSpan.FromMinutes(Math.Max(0.1, g.TokenRateWindowMinutes));
        var hourSince = now - HourWindow;
        var alerted = s.GuardAlerts.Values.Select(a => a.RootAgentId).ToHashSet();

        var rootOf = new Dictionary<string, string>();
        var trees = new List<(Agent Root, int Active, int Spawned, int Depth, long Session, Evidence Ev, bool Relevant)>();
        foreach (var root in s.Agents.Values.Where(a => a.ParentId is null || !s.Agents.ContainsKey(a.ParentId)))
        {
            int active = 0, spawned = 0, depth = 0;
            long session = 0;
            var ev = Evidence.Observed;
            var relevant = alerted.Contains(root.Id);
            var stack = new Stack<(Agent A, int D)>([(root, 0)]);
            var seen = new HashSet<string>();
            while (stack.Count > 0)
            {
                var (a, d) = stack.Pop();
                if (!seen.Add(a.Id)) continue;
                rootOf[a.Id] = root.Id;
                session += Counted(a.Usage.Input, a.Usage.Output, a.Usage.CacheWrite);
                var isActive = a.Status is AgentStatus.Starting or AgentStatus.Running or AgentStatus.Waiting or AgentStatus.Blocked;
                if (isActive || a.Status == AgentStatus.Paused || a.LastActivityAt >= hourSince) relevant = true;
                if (d > 0)
                {
                    var recent = a.CreatedAt >= spawnSince;
                    if (recent) spawned++;
                    if (isActive)
                    {
                        active++;
                        if (a.StatusEvidence > ev) ev = a.StatusEvidence;
                    }
                    if (isActive || recent) depth = Math.Max(depth, d);
                }
                foreach (var id in a.ChildIds)
                    if (s.Agents.TryGetValue(id, out var c)) stack.Push((c, d + 1));
            }
            trees.Add((root, active, spawned, depth, session, active > 0 ? ev : Evidence.Reported, relevant));
        }

        // Token windows from per-response usage marks (each response counted at its latest record).
        var inWindow = new Dictionary<string, long>();
        var inHour = new Dictionary<string, long>();
        foreach (var mark in s.UsageMarks.Values)
        {
            if (mark.AgentId is null || mark.At < hourSince || mark.At > now + TimeSpan.FromMinutes(5)) continue;
            if (!rootOf.TryGetValue(mark.AgentId, out var r)) continue;
            var n = Counted(mark.Input, mark.Output, mark.CacheWrite);
            inHour[r] = inHour.GetValueOrDefault(r) + n;
            if (mark.At >= now - rateWindow) inWindow[r] = inWindow.GetValueOrDefault(r) + n;
        }

        var result = new List<GuardMetrics>();
        foreach (var t in trees)
        {
            var hour = inHour.GetValueOrDefault(t.Root.Id);
            if (!t.Relevant && hour == 0) continue;
            var perMinute = (long)Math.Round(inWindow.GetValueOrDefault(t.Root.Id) / rateWindow.TotalMinutes);
            result.Add(new GuardMetrics(t.Root.Id, t.Active, t.Spawned, t.Depth, perMinute, hour, t.Session, t.Ev));
        }
        return result;
    }

    /// <summary>Tokens the guard counts: everything except cache reads.</summary>
    public static long Counted(long input, long output, long cacheWrite) => input + output + cacheWrite;

    /// <summary>Token counts are reported by the tool; the sub-agent tree is reported; active status carries its own evidence.</summary>
    private static Evidence EvidenceOf(string rule, GuardMetrics m) =>
        rule == GuardRules.ActiveSubAgents ? m.AgentEvidence : Evidence.Reported;

    public static string Summarize(string rule, long value, long threshold, GuardSettings g) => rule switch
    {
        GuardRules.ActiveSubAgents => $"{value} sub-agents active at once (threshold {threshold})",
        GuardRules.SpawnRate => $"{value} sub-agents spawned in the last {g.SpawnWindowMinutes:0.#} min (threshold {threshold})",
        GuardRules.Depth => $"Sub-agents nested {value} levels deep (threshold {threshold})",
        GuardRules.TokenRate => $"{Tok(value)} tokens/min over the last {g.TokenRateWindowMinutes:0.#} min (threshold {Tok(threshold)}/min)",
        GuardRules.TokensLastHour => $"{Tok(value)} tokens in the last hour (threshold {Tok(threshold)})",
        GuardRules.TokenBudget => $"Session used {Tok(value)} of its {Tok(g.SessionTokenBudget)}-token budget",
        _ => $"{rule}: {value} (threshold {threshold})",
    };

    private static string Fmt(string rule, long value) => rule switch
    {
        GuardRules.TokenRate => Tok(value) + " tokens/min",
        GuardRules.TokensLastHour or GuardRules.TokenBudget => Tok(value) + " tokens",
        GuardRules.Depth => value + " levels",
        _ => value + " sub-agents",
    };

    public static string Tok(long n) => n switch
    {
        >= 1_000_000 => (n / 1e6).ToString(n >= 10_000_000 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1e3).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "k",
        _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static IReadOnlyList<GuardRuleInfo> Describe(GuardSettings g)
    {
        GuardRuleInfo R(string rule, string label, string unit, string desc)
        {
            var (w, c) = g.Thresholds(rule);
            return new GuardRuleInfo(rule, label, unit, w, c, desc);
        }
        return
        [
            R(GuardRules.ActiveSubAgents, "Sub-agents at once", "agents", "Sub-agents of one session running, waiting or blocked at the same time"),
            R(GuardRules.SpawnRate, "Spawn rate", "agents", $"Sub-agents created in the last {g.SpawnWindowMinutes:0.#} min"),
            R(GuardRules.Depth, "Tree depth", "levels", "Deepest level of active or recently spawned sub-agents (a direct sub-agent is 1)"),
            R(GuardRules.TokenRate, "Token burn rate", "tokens/min", $"Average over the last {g.TokenRateWindowMinutes:0.#} min; input + output + cache writes (cache reads excluded)"),
            R(GuardRules.TokensLastHour, "Tokens in the last hour", "tokens", "Input + output + cache writes, whole tree"),
            R(GuardRules.TokenBudget, "Session token budget", "tokens", g.SessionTokenBudget > 0
                ? $"Whole tree, all time; warns at {g.BudgetWarnPercent}%" : "Off (set Guard:SessionTokenBudget)"),
        ];
    }

    /// <summary>The event that records a transition. Agent-tree facts are attached to the root agent.</summary>
    public static AgentEvent ToEvent(GuardTransition t, string? sessionId, DateTimeOffset now) => t.Severity is { } sev
        ? new AgentEvent
        {
            Type = EventTypes.GuardAlert, Source = EventSources.System, Evidence = t.Evidence, Ts = now,
            AgentId = t.RootAgentId, SessionId = sessionId,
            Data = Json.ToObject(new GuardAlertData(t.RootAgentId, t.Rule, sev, t.Value, t.Threshold, t.Summary, t.Previous)),
        }
        : new AgentEvent
        {
            Type = EventTypes.GuardCleared, Source = EventSources.System, Evidence = t.Evidence, Ts = now,
            AgentId = t.RootAgentId, SessionId = sessionId,
            Data = Json.ToObject(new GuardClearedData(t.RootAgentId, t.Rule, t.Value, t.Summary)),
        };
}

/// <summary>Folds guard.* events into <see cref="WorldState.GuardAlerts"/> (called by <see cref="Reducer"/>).</summary>
public static class GuardReducer
{
    public static void Apply(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        switch (e.Type)
        {
            case EventTypes.GuardAlert when e.Payload<GuardAlertData>() is { } d:
            {
                var key = GuardAlert.KeyOf(d.RootAgentId, d.Rule);
                if (!s.GuardAlerts.TryGetValue(key, out var a))
                    s.GuardAlerts[key] = a = new GuardAlert { Key = key, RootAgentId = d.RootAgentId, Rule = d.Rule, Summary = d.Summary, RaisedAt = e.Ts };
                if (a.Severity < d.Severity) { a.Dismissed = false; a.DismissedAt = null; } // escalation shows a dismissed alert again
                a.Severity = d.Severity;
                a.Value = d.Value;
                a.Threshold = d.Threshold;
                a.Summary = d.Summary;
                a.Evidence = e.Evidence;
                a.UpdatedAt = e.Ts;
                changed.Add(new(EntityKinds.Guard, key));
                break;
            }
            case EventTypes.GuardCleared when e.Payload<GuardClearedData>() is { } c:
            {
                var key = GuardAlert.KeyOf(c.RootAgentId, c.Rule);
                if (s.GuardAlerts.Remove(key)) changed.Add(new(EntityKinds.Guard, key));
                break;
            }
            case EventTypes.GuardDismissed when e.Payload<GuardDismissedData>() is { } x && s.GuardAlerts.TryGetValue(x.Key, out var da):
                da.Dismissed = true;
                da.DismissedAt = e.Ts;
                changed.Add(new(EntityKinds.Guard, x.Key));
                break;
        }
    }
}
