using Amc.Core.Domain;

namespace Amc.Core.Providers;

public enum ControlAction { Pause, Resume, Interrupt, Terminate, Kill }

/// <summary>What can be done to a specific agent, with a human-readable reason when not.</summary>
public sealed record AgentCapabilities(
    IReadOnlyList<string> MessageTransports,
    IReadOnlyDictionary<ControlAction, string?> Controls) // value null = supported; otherwise the reason it is not
{
    public bool Supports(ControlAction a) => Controls.TryGetValue(a, out var why) && why is null;
}

public sealed record OutgoingMessage(string MessageId, string Text);

public sealed record ControlResult(bool Ok, string Detail);

/// <summary>Accepted = queued or delivered; Delivered = confirmed written to the agent.</summary>
public sealed record DeliveryResult(bool Accepted, bool Delivered, string Transport, string Detail);

/// <summary>Actions that destroy or interrupt work and must be explicitly confirmed.</summary>
public static class DangerousActions
{
    public static bool RequiresConfirmation(ControlAction a) => a is ControlAction.Terminate or ControlAction.Kill;
}

/// <summary>
/// Adapter for one agent implementation (Claude Code, Codex, a simulator…).
/// Discovery/ingest is done by the provider's collectors; this interface covers the
/// interactive surface the daemon exposes for agents owned by the provider.
/// </summary>
public interface IAgentProvider
{
    /// <summary>Prefix of agent ids owned by this provider, e.g. "claude".</summary>
    string Id { get; }

    AgentCapabilities Capabilities(Agent agent);

    Task<ControlResult> ControlAsync(Agent agent, ControlAction action, CancellationToken ct);

    Task<DeliveryResult> SendAsync(Agent agent, OutgoingMessage message, CancellationToken ct);
}
