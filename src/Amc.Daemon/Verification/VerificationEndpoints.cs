using Amc.Core.Events;
using Amc.Core.Verification;
using Amc.Daemon.Pipeline;

namespace Amc.Daemon.Verification;

public static class VerificationEndpoints
{
    /// <summary>GET /api/agents/{id}/verification: the agent's claims checked against evidence (computed from state on request).</summary>
    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/agents/{id}/verification", (string id, StateStore state) =>
        {
            var json = state.Read(s => s.Agents.ContainsKey(id) ? Json.Serialize(Verifier.Evaluate(s, id)) : null);
            return json is null ? Results.NotFound(new { error = $"Unknown agent '{id}'" }) : Results.Content(json, "application/json");
        });
}
