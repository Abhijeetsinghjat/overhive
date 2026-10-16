using Amc.Core.Events;
using Amc.Core.Guard;
using Amc.Core.Risk;
using Amc.Core.State;
using Amc.Core.Verification;

namespace Amc.Core.Attention;

/// <summary>
/// Runaway-guard alerts (see Amc.Core.Guard): critical → urgent, warn → normal. Alerts dismissed in the guard
/// stay hidden here too. An escalation from warn to critical is a new item, so it notifies again.
/// </summary>
public sealed class GuardAttentionProvider : IAttentionProvider
{
    public string Name => "guard";

    public IEnumerable<AttentionItem> Collect(WorldState state, AttentionContext ctx)
    {
        foreach (var g in state.GuardAlerts.Values)
        {
            if (g.Dismissed) continue;
            var root = state.Agents.GetValueOrDefault(g.RootAgentId);
            var critical = g.Severity == GuardSeverity.Critical;
            yield return new AttentionItem
            {
                Id = $"{AttentionKinds.GuardAlert}:{g.Key}:{g.Severity.ToString().ToLowerInvariant()}:{g.RaisedAt.ToUnixTimeMilliseconds()}",
                Kind = AttentionKinds.GuardAlert,
                Priority = critical ? AttentionPriority.Urgent : AttentionPriority.Normal,
                AgentIds = [g.RootAgentId],
                Tool = root?.Provider,
                Title = (IsTokenRule(g.Rule) ? "Token burn" : "Sub-agent fan-out") + (critical ? " (critical): " : ": ")
                    + (root is null ? g.RootAgentId : AgentAttentionProvider.DisplayName(root)),
                Detail = g.Summary,
                Since = g.RaisedAt,
                Evidence = g.Evidence,
                Action = new AttentionAction(AttentionActions.OpenAgent, "Open session", g.RootAgentId),
            };
        }
    }

    private static bool IsTokenRule(string rule) => rule is GuardRules.TokenRate or GuardRules.TokensLastHour or GuardRules.TokenBudget;
}

/// <summary>
/// Risky actions (see Amc.Core.Risk) of the last <see cref="AttentionSettings.FailureWindow"/>: each critical
/// finding is an urgent item of its own; warnings are combined per agent and rule into one normal item
/// (so a run of writes outside the worktree is one line, not fifty). Info findings stay in the Risks tab.
/// </summary>
public sealed class RiskAttentionProvider : IAttentionProvider
{
    public string Name => "risks";

    public IEnumerable<AttentionItem> Collect(WorldState state, AttentionContext ctx)
    {
        var recent = state.Risks.Values
            .Where(r => r.Severity is RiskSeverity.Critical or RiskSeverity.Warn && ctx.Now - r.At <= ctx.Settings.FailureWindow)
            .ToList();

        foreach (var r in recent.Where(r => r.Severity == RiskSeverity.Critical))
            yield return Item(state, r, AttentionPriority.Urgent, $"{AttentionKinds.Risk}:{r.Id}", r.At, 1);

        foreach (var group in recent.Where(r => r.Severity == RiskSeverity.Warn).GroupBy(r => (r.AgentId, r.RuleId)))
        {
            var ordered = group.OrderBy(r => r.At).ThenBy(r => r.Seq).ToList();
            var first = ordered[0];
            yield return Item(state, ordered[^1], AttentionPriority.Normal,
                $"{AttentionKinds.Risk}:{first.AgentId}:{first.RuleId}:{first.At.ToUnixTimeMilliseconds()}", first.At, ordered.Count);
        }
    }

    private static AttentionItem Item(WorldState s, Amc.Core.Risk.Risk r, AttentionPriority p, string id, DateTimeOffset since, int count)
    {
        var agent = r.AgentId is null ? null : s.Agents.GetValueOrDefault(r.AgentId);
        var what = r.Command ?? r.Target ?? r.Span;
        return new AttentionItem
        {
            Id = id,
            Kind = AttentionKinds.Risk,
            Priority = p,
            AgentIds = r.AgentId is null ? [] : [r.AgentId],
            Tool = agent?.Provider,
            Title = "Risky action" + (count > 1 ? $" ×{count}" : "") + ": " + (agent is null ? "unknown agent" : AgentAttentionProvider.DisplayName(agent)),
            Detail = r.Explanation + (string.IsNullOrWhiteSpace(what) ? "" : " · " + what),
            Since = since,
            Evidence = r.Evidence,
            Action = r.AgentId is null
                ? new AttentionAction(AttentionActions.OpenRisks, "Risks", null)
                : new AttentionAction(AttentionActions.OpenAgent, "Open", r.AgentId),
            MoreActions = r.AgentId is null ? [] : [new AttentionAction(AttentionActions.OpenRisks, "Risks", null)],
        };
    }
}

/// <summary>
/// Claims an agent made that the evidence contradicts ("all tests pass" after a failing run, "committed" with no
/// commit, "created x" when x does not exist), from the last <see cref="AttentionSettings.FailureWindow"/>.
/// Normal priority: worth a look before trusting the work, not an interruption. Only agents whose summary
/// already counts a contradiction are evaluated, so this stays cheap.
/// </summary>
public sealed class VerificationAttentionProvider : IAttentionProvider
{
    public string Name => "verification";

    public IEnumerable<AttentionItem> Collect(WorldState state, AttentionContext ctx)
    {
        foreach (var a in state.Agents.Values)
        {
            if (a.Verification is not { Contradicted: > 0 }) continue;
            foreach (var c in Verifier.Evaluate(state, a.Id).Checks)
            {
                if (c.Status != CheckStatus.Contradicted) continue;
                var since = c.ObservedAt ?? c.ClaimedAt;
                if (ctx.Now - since > ctx.Settings.FailureWindow) continue;
                yield return new AttentionItem
                {
                    Id = $"{AttentionKinds.ClaimContradicted}:{c.Id}",
                    Kind = AttentionKinds.ClaimContradicted,
                    Priority = AttentionPriority.Normal,
                    AgentIds = [a.Id],
                    Tool = a.Provider,
                    Title = "Claim not backed by evidence: " + AgentAttentionProvider.DisplayName(a),
                    Detail = $"Said: “{c.Claim}” · Observed: {c.Observation}",
                    Since = since,
                    Evidence = c.Evidence ?? Evidence.Observed,
                    Action = new AttentionAction(AttentionActions.OpenAgent, "Open", a.Id),
                };
            }
        }
    }
}
