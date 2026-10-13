namespace Amc.Core.Domain;

/// <summary>
/// Lifecycle rules. A status is only ever set from evidence; this class decides whether
/// a proposed transition is legal so that late or duplicated events cannot resurrect a
/// finished agent or hide a crash.
/// </summary>
public static class AgentStateMachine
{
    public static bool IsTerminal(AgentStatus s) =>
        s is AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Cancelled or AgentStatus.Disconnected;

    public static bool IsActive(AgentStatus s) =>
        s is AgentStatus.Starting or AgentStatus.Running or AgentStatus.Waiting or AgentStatus.Blocked or AgentStatus.Paused;

    /// <summary>
    /// Returns true if <paramref name="from"/> → <paramref name="to"/> may be applied.
    /// <paramref name="reopen"/> marks evidence of genuinely new activity (e.g. a resumed
    /// session producing new tool calls), which may reopen a terminal agent.
    /// </summary>
    public static bool CanTransition(AgentStatus from, AgentStatus to, bool reopen = false)
    {
        if (from == to) return false;
        if (to == AgentStatus.Unknown) return !IsTerminal(from);
        if (IsTerminal(from))
        {
            // Disconnected may be corrected by a later explicit end event from the provider.
            if (from == AgentStatus.Disconnected && to is AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Cancelled)
                return true;
            return reopen && IsActive(to);
        }
        // A paused agent only leaves pause by resume (running) or by ending.
        if (from == AgentStatus.Paused)
            return to is AgentStatus.Running || IsTerminal(to);
        return true;
    }
}
