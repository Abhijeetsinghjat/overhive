using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Providers;
using Amc.Daemon.Pipeline;

namespace Amc.Daemon.Messaging;

public sealed record SendResult(bool Ok, string MessageId, string? Transport, string Detail, int StatusCode = 200, bool Delivered = false);
public sealed record OperationResult(bool Ok, string Detail, int StatusCode = 200);

/// <summary>
/// Routes user messages and control actions to the provider that owns the agent,
/// records everything as events (message lifecycle + audit), and enforces confirmation
/// for dangerous actions. Shared by the REST API (UI) and agentctl.
/// </summary>
public sealed class AgentOperations(IEnumerable<IAgentProvider> providers, StateStore state, IEventSink sink)
{
    private readonly Dictionary<string, IAgentProvider> _providers = providers.ToDictionary(p => p.Id);

    public IAgentProvider? ProviderFor(Agent a) => _providers.GetValueOrDefault(a.Provider);

    public AgentCapabilities? Capabilities(string agentId)
    {
        var a = Snapshot(agentId);
        return a is null ? null : ProviderFor(a)?.Capabilities(a);
    }

    public async Task<SendResult> SendMessageAsync(string agentId, string text, CancellationToken ct, string author = "user")
    {
        text = text.Trim();
        if (text.Length is 0 or > 8000) return new(false, "", null, "Message must be 1–8000 characters", 400);
        var a = Snapshot(agentId);
        if (a is null) return new(false, "", null, "Unknown agent", 404);
        var provider = ProviderFor(a);
        if (provider is null) return new(false, "", null, $"No provider '{a.Provider}' loaded", 409);
        var caps = provider.Capabilities(a);
        if (caps.MessageTransports.Count == 0)
            return new(false, "", null, "This agent cannot receive messages: no supported transport (see AGENT_INTEGRATION.md)", 409);

        var id = Ulid.NewId();
        await sink.PublishAndWaitAsync(new AgentEvent
        {
            Type = EventTypes.MessageSent, Source = EventSources.User, Evidence = Evidence.Observed,
            AgentId = a.Id, SessionId = a.SessionId,
            Data = Json.ToObject(new MessageData(id, text, caps.MessageTransports[0], author)),
        }, ct);

        DeliveryResult r;
        try { r = await provider.SendAsync(a, new OutgoingMessage(id, text), ct); }
        catch (Exception ex) { r = new DeliveryResult(false, false, caps.MessageTransports[0], ex.Message); }

        if (!r.Accepted)
            await sink.PublishAsync(new AgentEvent
            {
                Type = EventTypes.MessageFailed, Source = EventSources.User, AgentId = a.Id, SessionId = a.SessionId,
                Data = Json.ToObject(new MessageStatusData(id, r.Transport, r.Detail)),
            }, ct);
        else if (r.Delivered)
            await sink.PublishAsync(new AgentEvent
            {
                Type = EventTypes.MessageDelivered, Source = EventSources.User, AgentId = a.Id, SessionId = a.SessionId,
                DedupeKey = $"msg:{id}:delivered",
                Data = Json.ToObject(new MessageStatusData(id, r.Transport, r.Detail)),
            }, ct);
        return new(r.Accepted, id, r.Transport, r.Detail, r.Accepted ? 202 : 409, r.Delivered);
    }

    public async Task<OperationResult> ControlAsync(string agentId, ControlAction action, bool confirmed, CancellationToken ct)
    {
        var a = Snapshot(agentId);
        if (a is null) return new(false, "Unknown agent", 404);
        var provider = ProviderFor(a);
        if (provider is null) return new(false, $"No provider '{a.Provider}' loaded", 409);
        var caps = provider.Capabilities(a);
        if (!caps.Supports(action))
            return new(false, caps.Controls.GetValueOrDefault(action) ?? $"{action} is not supported for this agent", 409);
        if (DangerousActions.RequiresConfirmation(action) && !confirmed)
            return new(false, $"{action} is destructive and requires confirm=true", 428);

        ControlResult r;
        try { r = await provider.ControlAsync(a, action, ct); }
        catch (Exception ex) { r = new ControlResult(false, ex.Message); }

        await sink.PublishAsync(new AgentEvent
        {
            Type = EventTypes.AuditControl, Source = EventSources.User, Evidence = Evidence.Observed,
            AgentId = a.Id, SessionId = a.SessionId,
            Data = Json.ToObject(new AuditData(action.ToString().ToLowerInvariant(), a.Id, r.Ok ? "ok" : "failed", r.Detail)),
        }, ct);
        return new(r.Ok, r.Detail, r.Ok ? 200 : 500);
    }

    /// <summary>Copies the agent out of state so providers never touch shared mutable entities.</summary>
    private Agent? Snapshot(string agentId) =>
        state.Read(s => s.Agents.TryGetValue(agentId, out var a) ? Json.Deserialize<Agent>(Json.Serialize(a)) : null);
}
