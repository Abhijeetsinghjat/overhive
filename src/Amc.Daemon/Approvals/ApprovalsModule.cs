using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Core.Approvals;
using Amc.Core.Events;
using Amc.Daemon.Api;
using Amc.Daemon.Messaging;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Approvals;

/// <summary>Body of POST /api/approvals/{id}/allow|deny (every field optional).</summary>
public sealed record ApprovalDecisionRequest(string? Reason = null, bool AllowSimilar = false, string? Client = null);

/// <summary>
/// Dashboard approvals of Claude Code tool calls. Deciding is a control action: it is gated exactly like
/// /api/agents/{id}/control (token, Origin and Host checks of <see cref="SecurityMiddleware"/>, and <c>EnableControl</c>),
/// and additionally by <c>Approvals.AllowRemote</c> for clients that come through an AllowedHosts name.
/// </summary>
public static class ApprovalsModule
{
    private static readonly HashSet<string> Clients = ["dashboard", "phone", "cli"];

    public static IServiceCollection AddApprovals(this IServiceCollection s)
    {
        s.AddSingleton<ApprovalBroker>();
        s.AddHostedService<ApprovalRestorer>();
        return s;
    }

    public static WebApplication MapApprovalEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/approvals");

        // Pending approvals (from the state: already redacted) and the configuration that produced them.
        api.MapGet("", (StateStore state, ApprovalBroker broker) =>
        {
            var items = state.Read(s => Json.Serialize(s.Approvals.Values.OrderBy(a => a.RequestedAt).ToList()));
            var a = broker.Settings;
            return Results.Content(
                $$"""{"mode":{{JsonSerializer.Serialize(a.EffectiveMode)}},"timeoutSeconds":{{a.EffectiveTimeoutSeconds}},"minSeverity":{{JsonSerializer.Serialize(Amc.Core.Risk.RiskSeverity.Normalize(a.MinSeverity))}},"items":{{items}}}""",
                "application/json");
        });

        api.MapPost("/{id}/{decision}", async (string id, string decision, HttpContext http, ApprovalBroker broker, IOptions<AmcOptions> opt,
            CancellationToken ct) =>
        {
            if (decision is not (ApprovalDecisions.Allow or ApprovalDecisions.Deny)) return Results.NotFound(new { error = "use /allow or /deny" });
            if (!opt.Value.EnableControl)
                return Results.Json(new OperationResult(false, "Control is disabled (EnableControl=false)", 403), statusCode: 403);
            var remote = !SecurityMiddleware.IsUnixSocket(http) && !SecurityMiddleware.IsLoopbackHost(http.Request.Host.Host);
            if (remote && !opt.Value.Approvals.AllowRemote)
                return Results.Json(new OperationResult(false, "Approvals from remote clients are disabled (Approvals.AllowRemote=false)", 403), statusCode: 403);

            var body = await ReadBody(http, ct);
            var client = body.Client is { } c && Clients.Contains(c) ? c : null;
            var r = await broker.ResolveAsync(id, decision, body.Reason, body.AllowSimilar,
                remote ? ApprovalActors.Remote : ApprovalActors.Local, client, ct);
            return Results.Json(new { ok = r.Ok, message = r.Message, latencyMs = r.LatencyMs }, statusCode: r.StatusCode);
        });
        return app;
    }

    /// <summary>The body is optional; an empty or malformed body means "no reason".</summary>
    private static async Task<ApprovalDecisionRequest> ReadBody(HttpContext http, CancellationToken ct)
    {
        try
        {
            if (http.Request.ContentLength is 0) return new();
            return await JsonSerializer.DeserializeAsync<ApprovalDecisionRequest>(http.Request.Body, Json.Options, ct) ?? new();
        }
        catch (JsonException) { return new(); }
    }
}
