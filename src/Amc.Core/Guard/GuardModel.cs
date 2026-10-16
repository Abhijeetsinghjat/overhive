using Amc.Core.Events;

namespace Amc.Core.Guard;

/// <summary>Rule ids of the runaway guard. Each is evaluated per root agent (a session and all its descendants).</summary>
public static class GuardRules
{
    public const string ActiveSubAgents = "active-sub-agents";
    public const string SpawnRate = "spawn-rate";
    public const string Depth = "depth";
    public const string TokenRate = "token-rate";
    public const string TokensLastHour = "tokens-hour";
    public const string TokenBudget = "token-budget";

    public static readonly IReadOnlyList<string> All = [ActiveSubAgents, SpawnRate, Depth, TokenRate, TokensLastHour, TokenBudget];
}

public enum GuardSeverity { Warn, Critical }

/// <summary>
/// Runaway-guard configuration (config.json section "Guard", env AMC_Guard__AutoPause=true …).
/// A threshold of 0 or less turns that level off. Tokens counted by the guard are input + output +
/// cache writes as reported by the tool; cache reads are excluded (they are cheap re-reads of context).
/// </summary>
public sealed class GuardSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Opt-in: on a new critical alert, pause the root agent's tree (never kills).</summary>
    public bool AutoPause { get; set; }
    public double IntervalSeconds { get; set; } = 15;

    public int ActiveSubAgentsWarn { get; set; } = 12;
    public int ActiveSubAgentsCritical { get; set; } = 30;

    public int SpawnedWarn { get; set; } = 30;
    public int SpawnedCritical { get; set; } = 80;
    public double SpawnWindowMinutes { get; set; } = 10;

    /// <summary>Levels of sub-agents below the session (a direct sub-agent is depth 1).</summary>
    public int DepthWarn { get; set; } = 4;
    public int DepthCritical { get; set; } = 6;

    public long TokensPerMinuteWarn { get; set; } = 400_000;
    public long TokensPerMinuteCritical { get; set; } = 1_000_000;
    public double TokenRateWindowMinutes { get; set; } = 5;

    public long TokensPerHourWarn { get; set; } = 12_000_000;
    public long TokensPerHourCritical { get; set; } = 30_000_000;

    /// <summary>Per-session token budget (whole tree, all time). 0 = off.</summary>
    public long SessionTokenBudget { get; set; }
    public int BudgetWarnPercent { get; set; } = 80;

    /// <summary>Hysteresis: an alert clears (or drops a level) only below this fraction of its threshold.</summary>
    public const double ClearRatio = 0.9;

    public (long Warn, long Critical) Thresholds(string rule) => rule switch
    {
        GuardRules.ActiveSubAgents => (ActiveSubAgentsWarn, ActiveSubAgentsCritical),
        GuardRules.SpawnRate => (SpawnedWarn, SpawnedCritical),
        GuardRules.Depth => (DepthWarn, DepthCritical),
        GuardRules.TokenRate => (TokensPerMinuteWarn, TokensPerMinuteCritical),
        GuardRules.TokensLastHour => (TokensPerHourWarn, TokensPerHourCritical),
        GuardRules.TokenBudget => SessionTokenBudget > 0 ? (SessionTokenBudget * Math.Clamp(BudgetWarnPercent, 1, 100) / 100, SessionTokenBudget) : (0, 0),
        _ => (0, 0),
    };
}

/// <summary>One active guard alert (state entity, keyed root|rule). Cleared alerts are removed; the log keeps history.</summary>
public sealed class GuardAlert
{
    public required string Key { get; init; }
    public required string RootAgentId { get; init; }
    public required string Rule { get; init; }
    public GuardSeverity Severity { get; set; }
    /// <summary>Value when the alert was raised or last changed level (live values: GET /api/guard).</summary>
    public long Value { get; set; }
    public long Threshold { get; set; }
    public required string Summary { get; set; }
    public Evidence Evidence { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Dismissed { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }

    public static string KeyOf(string rootAgentId, string rule) => rootAgentId + "|" + rule;
}

/// <summary>Current values of every rule for one root agent's tree.</summary>
public sealed record GuardMetrics(
    string RootAgentId,
    int ActiveSubAgents,
    int SpawnedInWindow,
    int MaxDepth,
    long TokensPerMinute,
    long TokensLastHour,
    long SessionTokens,
    /// <summary>Weakest evidence behind the active sub-agents' statuses.</summary>
    Evidence AgentEvidence)
{
    public long Value(string rule) => rule switch
    {
        GuardRules.ActiveSubAgents => ActiveSubAgents,
        GuardRules.SpawnRate => SpawnedInWindow,
        GuardRules.Depth => MaxDepth,
        GuardRules.TokenRate => TokensPerMinute,
        GuardRules.TokensLastHour => TokensLastHour,
        GuardRules.TokenBudget => SessionTokens,
        _ => 0,
    };
}

/// <summary>A change the guard wants recorded. <see cref="Severity"/> null = the alert clears.</summary>
public sealed record GuardTransition(
    string RootAgentId, string Rule, GuardSeverity? Severity, GuardSeverity? Previous,
    long Value, long Threshold, Evidence Evidence, string Summary)
{
    public string Key => GuardAlert.KeyOf(RootAgentId, Rule);
    public bool IsNewCritical => Severity == GuardSeverity.Critical && Previous != GuardSeverity.Critical;
}

public sealed record GuardPlan(IReadOnlyList<GuardMetrics> Metrics, IReadOnlyList<GuardTransition> Transitions);

public sealed record GuardRuleInfo(string Rule, string Label, string Unit, long Warn, long Critical, string Description);

/// <summary>Response of GET /api/guard.</summary>
public sealed record GuardStatus(
    bool Enabled, bool AutoPause, bool ControlEnabled, DateTimeOffset At,
    IReadOnlyList<GuardRuleInfo> Rules, IReadOnlyList<GuardMetrics> Roots, IReadOnlyList<GuardAlert> Alerts);

// ---- event payloads ------------------------------------------------------------

/// <summary>guard.alert: a rule crossed a threshold (or changed level) for a root agent's tree.</summary>
public sealed record GuardAlertData(
    string RootAgentId, string Rule, GuardSeverity Severity, long Value, long Threshold, string Summary, GuardSeverity? Previous = null);

/// <summary>guard.cleared: the value dropped back below the threshold (with hysteresis), or the tree is gone.</summary>
public sealed record GuardClearedData(string RootAgentId, string Rule, long Value, string Reason);

/// <summary>guard.dismissed: the user hid an alert. A later escalation to critical shows it again.</summary>
public sealed record GuardDismissedData(string Key);
