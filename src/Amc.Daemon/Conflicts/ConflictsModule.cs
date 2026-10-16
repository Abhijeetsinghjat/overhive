using Amc.Core.Events;
using Amc.Daemon.Collectors.Git;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Conflicts;

public sealed record WarnRequest(string Key, bool Confirm = false);

/// <summary>Wiring and REST endpoints for conflict overlap analysis and agent warnings.</summary>
public static class ConflictsModule
{
    public static IServiceCollection AddConflicts(this IServiceCollection s)
    {
        s.AddSingleton<ConflictWarner>();
        s.AddSingleton(sp => new ConflictAnalyzer(
            sp.GetRequiredService<GitCli>(), sp.GetRequiredService<StateStore>(), sp.GetRequiredService<IEventSink>(),
            sp.GetRequiredService<HealthRegistry>(), sp.GetRequiredService<IOptions<AmcOptions>>(), sp.GetRequiredService<ConflictWarner>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ConflictAnalyzer>>()));
        s.AddHostedService(sp => sp.GetRequiredService<ConflictAnalyzer>());
        return s;
    }

    /// <summary>Conflict keys contain '|' and paths, so they travel in the query string / body, not the route.</summary>
    public static void MapConflictEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/conflicts");

        api.MapGet("/detail", (string? key, StateStore state, ConflictWarner warner) =>
        {
            if (string.IsNullOrEmpty(key)) return Results.BadRequest(new { error = "key is required" });
            var conflict = state.Read(s => s.Conflicts.TryGetValue(key, out var c) ? Json.Serialize(c) : null);
            if (conflict is null) return Results.NotFound(new { error = "unknown conflict (it may have been resolved)" });
            var preview = warner.Preview(key);
            return Results.Content($$"""{"conflict":{{conflict}},"warn":{{Json.Serialize(preview)}}}""", "application/json");
        });

        api.MapPost("/warn", async (WarnRequest body, ConflictWarner warner, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(body.Key)) return Results.BadRequest(new { error = "key is required" });
            var r = await warner.WarnAsync(body.Key, body.Confirm, ct);
            return Results.Json(r, statusCode: r.StatusCode);
        });
    }
}
