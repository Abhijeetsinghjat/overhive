using System.Text.Json.Serialization;
using Amc.Core.Events;

namespace Amc.Core.Attention;

/// <summary>How soon the user should look. Only <see cref="Urgent"/> items count in the header badge and notify by default.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttentionPriority>))]
public enum AttentionPriority { Urgent, Normal, Low }

/// <summary>Item kinds produced by the built-in providers. Plug-in providers add their own.</summary>
public static class AttentionKinds
{
    public const string AgentBlocked = "agent.blocked";
    public const string AgentWaiting = "agent.waiting";
    public const string AgentFailed = "agent.failed";
    public const string AgentDisconnected = "agent.disconnected";
    public const string AgentFinished = "agent.finished";
    public const string Conflict = "conflict";
    public const string GuardAlert = "guard.alert";
    public const string Risk = "risk";
    public const string ClaimContradicted = "claim.contradicted";
}

/// <summary>What the primary button of an item does. The UI maps these to existing panels and endpoints.</summary>
public static class AttentionActions
{
    /// <summary>Select the agent (Target = agent id).</summary>
    public const string OpenAgent = "open-agent";
    /// <summary>Open the agent's live terminal (Target = agent id); used when the answer has to be typed there.</summary>
    public const string OpenTerminal = "open-terminal";
    /// <summary>Send a message through the agent's existing message transports (Target = agent id).</summary>
    public const string Reply = "reply";
    /// <summary>Select the conflict (Target = conflict key).</summary>
    public const string OpenConflict = "open-conflict";
    /// <summary>Open the Risks tab (Target unused).</summary>
    public const string OpenRisks = "open-risks";
}

public sealed record AttentionAction(string Kind, string Label, string? Target);

/// <summary>
/// One thing that needs the user, derived from state (never stored as an event). <see cref="Id"/> is stable
/// for one occurrence: the same blocked episode keeps its id across recomputes and restarts, and a new
/// episode of the same agent gets a new id, so dismissing or snoozing applies to exactly one occurrence.
/// </summary>
public sealed record AttentionItem
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public AttentionPriority Priority { get; init; }
    public IReadOnlyList<string> AgentIds { get; init; } = [];
    /// <summary>Tool (provider id, e.g. "claude", "codex") of the first agent; null for items without an agent.</summary>
    public string? Tool { get; init; }
    public required string Title { get; init; }
    /// <summary>Redacted and capped; may come from agent text (status reasons).</summary>
    public string? Detail { get; init; }
    /// <summary>When the condition began (the evidence time, not when it was derived).</summary>
    public DateTimeOffset Since { get; init; }
    public Evidence Evidence { get; init; }
    public required AttentionAction Action { get; init; }
    /// <summary>Secondary actions (e.g. reply next to open-terminal).</summary>
    public IReadOnlyList<AttentionAction> MoreActions { get; init; } = [];
    /// <summary>Name of the provider that produced the item.</summary>
    public string Source { get; init; } = "";
    /// <summary>Set by the daemon's inbox when the user snoozed the item (only present in "all" listings).</summary>
    public DateTimeOffset? SnoozedUntil { get; init; }
    /// <summary>Set by the daemon's inbox when the user dismissed the item (only present in "all" listings).</summary>
    public bool? Dismissed { get; init; }
}
