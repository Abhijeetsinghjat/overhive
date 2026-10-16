using System.Globalization;
using Amc.Core.Events;
using Amc.Core.Risk;
using Amc.Daemon.Api;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Risk;

namespace Amc.Daemon;

/// <summary>Wiring for the risky-action flight recorder (see docs/RISKS.md).</summary>
public static class RiskModule
{
    public static IServiceCollection AddRisk(this IServiceCollection services)
    {
        services.AddSingleton<RiskRecorder>();
        services.AddHostedService(sp => sp.GetRequiredService<RiskRecorder>());
        return services;
    }

    /// <summary>
    /// GET /api/risks?agent=&amp;descendants=true&amp;severity=info|warn|critical (minimum)&amp;since=ISO-8601|10m|2h|1d&amp;limit=
    /// → findings, newest first.
    /// </summary>
    public static WebApplication MapRiskEndpoints(this WebApplication app)
    {
        app.MapGet("/api/risks", (HttpRequest r, StateStore state) =>
        {
            var q = r.Query;
            var agent = q["agent"].FirstOrDefault();
            var minRank = RiskSeverity.Rank(q["severity"].FirstOrDefault());
            var since = Since(q["since"].FirstOrDefault(), DateTimeOffset.UtcNow);
            var limit = int.TryParse(q["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 5000) : 500;
            var json = state.Read(s =>
            {
                HashSet<string>? agents = agent is null ? null
                    : q["descendants"] == "true" ? [.. ApiEndpoints.Descendants(s, agent)] : [agent];
                var list = s.Risks.Values
                    .Where(x => (agents is null || (x.AgentId is not null && agents.Contains(x.AgentId)))
                                && RiskSeverity.Rank(x.Severity) >= minRank && (since is null || x.At >= since))
                    .OrderByDescending(x => x.At).ThenByDescending(x => x.Seq).Take(limit).ToList();
                return Json.Serialize(list);
            });
            return Results.Content(json, "application/json");
        });
        return app;
    }

    /// <summary>An ISO-8601 time or a relative age such as 30s, 10m, 2h, 7d.</summary>
    public static DateTimeOffset? Since(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length >= 2 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            TimeSpan? span = text[^1] switch
            {
                's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), 'd' => TimeSpan.FromDays(n), _ => null,
            };
            if (span is { } sp) return now - sp;
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }
}
