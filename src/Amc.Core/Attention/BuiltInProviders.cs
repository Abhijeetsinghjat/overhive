using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Core.Attention;

/// <summary>
/// Agents that need the user, from their status (whose evidence label the item inherits):
/// blocked (permission / notification) → urgent; a session idle at its prompt with no active sub-agents → normal;
/// failed → urgent (main) / normal (sub-agent); disconnected session → normal; recently finished session → low.
/// </summary>
public sealed class AgentAttentionProvider : IAttentionProvider
{
    public string Name => "agents";

    public IEnumerable<AttentionItem> Collect(WorldState state, AttentionContext ctx)
    {
        foreach (var a in state.Agents.Values)
        {
            var item = For(state, a, ctx);
            if (item is not null) yield return item;
        }
    }

    private static AttentionItem? For(WorldState s, Agent a, AttentionContext ctx)
    {
        var since = a.StatusAt == default ? a.LastActivityAt : a.StatusAt;
        var main = a.Kind == AgentKind.Main;
        var name = DisplayName(a);
        var open = new AttentionAction(AttentionActions.OpenAgent, "Open", a.Id);
        var reply = new AttentionAction(AttentionActions.Reply, "Message", a.Id);
        AttentionItem Make(string kind, AttentionPriority p, string title, AttentionAction action, params AttentionAction[] more) => new()
        {
            Id = $"{kind}:{a.Id}:{since.ToUnixTimeMilliseconds()}",
            Kind = kind, Priority = p, AgentIds = [a.Id], Tool = a.Provider, Title = title, Detail = a.StatusReason,
            Since = since, Evidence = a.StatusEvidence, Action = action, MoreActions = more,
        };
        bool Recent(TimeSpan window) => window > TimeSpan.Zero && ctx.Now - since <= window;

        switch (a.Status)
        {
            case AgentStatus.Blocked:
            {
                var permission = a.StatusReason?.StartsWith("Waiting for permission", StringComparison.OrdinalIgnoreCase) == true;
                // A permission prompt is answered where the agent runs; a terminal Overhive owns can be opened right here.
                var primary = a.TerminalId is not null ? new AttentionAction(AttentionActions.OpenTerminal, "Answer in terminal", a.Id) : open;
                return Make(AttentionKinds.AgentBlocked, AttentionPriority.Urgent,
                    (permission ? "Needs your approval: " : "Needs your input: ") + name, primary,
                    primary == open ? [reply] : [open, reply]);
            }
            case AgentStatus.Waiting when main && !HasActiveChildren(s, a):
                return Make(AttentionKinds.AgentWaiting, AttentionPriority.Normal, "Waiting for your next prompt: " + name,
                    a.TerminalId is not null ? new AttentionAction(AttentionActions.OpenTerminal, "Open terminal", a.Id) : reply,
                    a.TerminalId is not null ? [reply, open] : [open]);
            case AgentStatus.Failed when Recent(ctx.Settings.FailureWindow):
                return Make(AttentionKinds.AgentFailed, main ? AttentionPriority.Urgent : AttentionPriority.Normal, "Failed: " + name, open);
            case AgentStatus.Disconnected when main && Recent(ctx.Settings.FailureWindow):
                return Make(AttentionKinds.AgentDisconnected, AttentionPriority.Normal, "Exited unexpectedly: " + name, open);
            case AgentStatus.Completed when main && Recent(ctx.Settings.FinishedWindow):
                return Make(AttentionKinds.AgentFinished, AttentionPriority.Low, "Finished: " + name, open);
            default:
                return null;
        }
    }

    /// <summary>A session waiting on its own sub-agents is not waiting on the user.</summary>
    private static bool HasActiveChildren(WorldState s, Agent a) =>
        a.ChildIds.Any(id => s.Agents.TryGetValue(id, out var c) && AgentStateMachine.IsActive(c.Status));

    /// <summary>Same naming as the dashboard (model.js agentName).</summary>
    public static string DisplayName(Agent a)
    {
        if (!string.IsNullOrWhiteSpace(a.Title)) return a.Title!;
        if (a.Kind == AgentKind.Main) return "Session " + Short(a.SessionId ?? a.Id);
        return a.AgentType ?? "sub-agent " + Short(a.Id);
    }

    private static string Short(string id)
    {
        var last = id[(id.LastIndexOf(':') + 1)..];
        return last.Length > 8 ? last[..8] : last;
    }
}

/// <summary>
/// Conflicts between agents (see ConflictDetector). Git-unmerged state needs a human → urgent; the others are
/// warnings → normal. Git-derived kinds are observed; agent-overlap kinds are inferred from attribution.
/// </summary>
public sealed class ConflictAttentionProvider : IAttentionProvider
{
    public string Name => "conflicts";

    public IEnumerable<AttentionItem> Collect(WorldState state, AttentionContext ctx)
    {
        foreach (var c in state.Conflicts.Values)
        {
            var first = c.AgentIds.Select(id => state.Agents.GetValueOrDefault(id)).FirstOrDefault(a => a is not null);
            yield return new AttentionItem
            {
                Id = $"{AttentionKinds.Conflict}:{c.Key}:{c.DetectedAt.ToUnixTimeMilliseconds()}",
                Kind = AttentionKinds.Conflict,
                Priority = c.Kind == ConflictKind.GitUnmerged ? AttentionPriority.Urgent : AttentionPriority.Normal,
                AgentIds = [.. c.AgentIds],
                Tool = first?.Provider,
                Title = Headline(c.Kind) + (c.Path is { Length: > 0 } p ? ": " + p : ""),
                Detail = c.Description,
                Since = c.DetectedAt,
                Evidence = c.Kind is ConflictKind.GitUnmerged or ConflictKind.CrossWorktree ? Evidence.Observed : Evidence.Inferred,
                Action = new AttentionAction(AttentionActions.OpenConflict, "Details", c.Key),
            };
        }
    }

    public static string Headline(ConflictKind k) => k switch
    {
        ConflictKind.SameFile => "Agents editing the same file",
        ConflictKind.SameWorktree => "Several agents writing in one worktree",
        ConflictKind.CrossWorktree => "Same path changed in several worktrees",
        ConflictKind.GitUnmerged => "Unmerged files / merge in progress",
        _ => "Conflict",
    };
}
