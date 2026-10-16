using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Core.Risk;

/// <summary>Folds <c>risk.detected</c> into <see cref="WorldState.Risks"/> and the agent's counters (called by <see cref="Reducer"/>).</summary>
internal static class RiskReducer
{
    public static void Apply(WorldState s, Agent? agent, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.Payload<RiskData>() is not { } d) return;
        var id = e.DedupeKey ?? e.Id;
        if (s.Risks.ContainsKey(id)) return;
        var severity = RiskSeverity.Normalize(d.Severity);
        s.Risks[id] = new Risk
        {
            Id = id, Seq = e.Seq, AgentId = e.AgentId, SessionId = e.SessionId, WorktreeId = e.WorktreeId,
            RuleId = d.RuleId, Severity = severity, Explanation = d.Explanation, Span = d.Span, Target = d.Target,
            Command = d.Command, Evidence = e.Evidence, Origin = d.Origin, SourceType = d.SourceType, SourceSeq = d.SourceSeq,
            Pid = d.Pid, At = e.Ts,
        };
        changed.Add(new(EntityKinds.Risk, id));
        if (agent is not null)
        {
            agent.Risks ??= new RiskCounts();
            agent.Risks.Add(severity, e.Ts);
            changed.Add(new(EntityKinds.Agent, agent.Id));
        }

        // Per-agent cap: drop that agent's oldest findings (counters stay cumulative).
        if (e.AgentId is not null)
        {
            var mine = s.Risks.Values.Where(r => r.AgentId == e.AgentId).ToList();
            if (mine.Count > WorldState.MaxRisksPerAgent)
                foreach (var old in mine.OrderBy(r => r.At).ThenBy(r => r.Seq).Take(mine.Count - WorldState.MaxRisksPerAgent).ToList())
                {
                    s.Risks.Remove(old.Id);
                    changed.Add(new(EntityKinds.Risk, old.Id));
                }
        }
        if (s.Risks.Count > WorldState.MaxRisks + WorldState.MaxRisks / 10)
            foreach (var old in s.Risks.Values.OrderBy(r => r.At).ThenBy(r => r.Seq).Take(s.Risks.Count - WorldState.MaxRisks).ToList())
            {
                s.Risks.Remove(old.Id);
                changed.Add(new(EntityKinds.Risk, old.Id));
            }
    }
}
