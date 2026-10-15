using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Guard;

public sealed record GuardDismissRequest(string? Key = null, string? RootAgentId = null);
public sealed record GuardPauseRequest(string RootAgentId);

/// <summary>Runaway guard wiring: the periodic service and its REST endpoints.</summary>
public static class GuardModule
{
    public static IServiceCollection AddGuard(this IServiceCollection services)
    {
        services.AddSingleton<GuardService>();
        services.AddHostedService(sp => sp.GetRequiredService<GuardService>());
        return services;
    }

    public static WebApplication MapGuardEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/guard");

        api.MapGet("", (GuardService guard, TimeProvider clock) =>
            Results.Content(guard.StatusJson(clock.GetUtcNow()), "application/json"));

        api.MapPost("/dismiss", async (GuardDismissRequest body, GuardService guard, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(body.Key) && string.IsNullOrEmpty(body.RootAgentId))
                return Results.BadRequest(new { error = "key or rootAgentId is required" });
            var n = await guard.DismissAsync(body.Key, body.RootAgentId, ct);
            return Results.Ok(new { dismissed = n });
        });

        // "Pause tree" from the UI/CLI: the user acting, so the audit is a normal user action.
        api.MapPost("/pause", async (GuardPauseRequest body, GuardService guard, IOptions<AmcOptions> opt, CancellationToken ct) =>
        {
            if (!opt.Value.EnableControl)
                return Results.Json(new OperationResult(false, "Control is disabled (EnableControl=false)", 403), statusCode: 403);
            var results = await guard.PauseTreeAsync(body.RootAgentId ?? "", null, ct);
            var ok = results.Count > 0 && results[0].Ok;
            return Results.Json(new { ok, detail = results[0].Detail, results }, statusCode: ok ? 200 : results[0].Detail == "Unknown agent" ? 404 : 409);
        });
        return app;
    }
}
