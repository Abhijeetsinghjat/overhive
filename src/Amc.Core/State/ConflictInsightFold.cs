using Amc.Core.Domain;
using Amc.Core.Events;

namespace Amc.Core.State;

/// <summary>
/// Folds conflict analysis / warning events into <see cref="WorldState.ConflictInsights"/> and attaches the
/// insight to the live conflict. Insights are keyed by conflict key, not stored only on the conflict, so an
/// event folded before the conflict is (re-)derived at the end of the batch still lands on it.
/// </summary>
public static class ConflictInsightFold
{
    /// <summary>Insights of conflicts that no longer exist are dropped after this much event time.</summary>
    public static readonly TimeSpan KeepOrphansFor = TimeSpan.FromHours(1);

    public static void Apply(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        string key;
        ConflictInsight insight;
        switch (e.Type)
        {
            case EventTypes.ConflictAnalyzed when e.Payload<ConflictAnalysisData>() is { ConflictKey: { } k, Overlap: { } o }:
                insight = Get(s, key = k);
                insight.Overlap = o;
                break;
            case EventTypes.ConflictWarned when e.Payload<ConflictWarnedData>() is { ConflictKey: { } k } w:
                insight = Get(s, key = k);
                insight.Warnings.Add(new ConflictWarning(e.Ts, w.Auto, w.Recipients ?? []));
                if (insight.Warnings.Count > ConflictInsight.MaxWarnings) insight.Warnings.RemoveAt(0);
                break;
            default:
                return;
        }
        insight.UpdatedAt = e.Ts;
        if (s.Conflicts.TryGetValue(key, out var c))
        {
            c.Insight = insight;
            changed.Add(new(EntityKinds.Conflict, key));
        }
    }

    /// <summary>Drops insights whose conflict is gone and that were not updated for <see cref="KeepOrphansFor"/>.</summary>
    public static void Prune(WorldState s)
    {
        if (s.ConflictInsights.Count == 0) return;
        foreach (var (k, _) in s.ConflictInsights.Where(kv => !s.Conflicts.ContainsKey(kv.Key) && s.Clock - kv.Value.UpdatedAt > KeepOrphansFor).ToList())
            s.ConflictInsights.Remove(k);
    }

    private static ConflictInsight Get(WorldState s, string key)
    {
        if (!s.ConflictInsights.TryGetValue(key, out var i)) s.ConflictInsights[key] = i = new ConflictInsight();
        return i;
    }
}
