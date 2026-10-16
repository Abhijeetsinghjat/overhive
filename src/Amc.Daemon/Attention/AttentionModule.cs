using Amc.Core.Attention;
using Amc.Core.Domain;
using Amc.Daemon.Api;
using Amc.Daemon.Infrastructure;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Attention;

public sealed record AttentionMarkRequest(string Id, double? Minutes = null);

/// <summary>
/// The "needs you" queue across all tools: providers → <see cref="AttentionService"/> → API, WebSocket push,
/// dashboard inbox, phone page (/m) and local / self-hosted notifications.
/// <para>Extension point for other features: <c>services.AddSingleton&lt;IAttentionProvider, MyProvider&gt;()</c>.</para>
/// </summary>
public static class AttentionModule
{
    public const int MaxIdLength = 2048;

    public static IServiceCollection AddAttention(this IServiceCollection s, AmcOptions options)
    {
        foreach (var p in AttentionDeriver.BuiltIn) s.AddSingleton(p);
        s.AddSingleton(sp => new AttentionInbox(Path.Combine(options.DataDir, "attention-marks.json"),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<AttentionInbox>()));
        s.AddSingleton(options.Notifications);
        s.AddSingleton<INotifier, DesktopNotifier>();
        s.AddSingleton<INotifier, NtfyNotifier>(sp => new NtfyNotifier(options.Notifications,
            sp.GetRequiredService<ILogger<NtfyNotifier>>(), sp.GetRequiredService<TimeProvider>()));
        s.AddSingleton(sp =>
        {
            var names = AgentToolCatalog.Merge(options.Tools).ToDictionary(t => t.Id, t => t.Name);
            return new NotificationDispatcher(sp.GetServices<INotifier>(), options.Notifications,
                id => id is not null && names.TryGetValue(id, out var n) ? n : id ?? "agent",
                sp.GetRequiredService<HealthRegistry>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<NotificationDispatcher>());
        });
        s.AddSingleton<AttentionService>();
        s.AddHostedService(sp => sp.GetRequiredService<AttentionService>());
        return s;
    }

    public static void MapAttentionEndpoints(WebApplication app)
    {
        var api = app.MapGroup("/api/attention");
        api.MapGet("", (HttpRequest r, AttentionService svc) => Results.Ok(svc.Current(r.Query["all"] == "true")));

        api.MapPost("/dismiss", (AttentionMarkRequest body, AttentionService svc, AttentionInbox inbox, TimeProvider clock) =>
            Mark(body, svc, id => inbox.Dismiss(id, clock.GetUtcNow())));
        api.MapPost("/snooze", (AttentionMarkRequest body, AttentionService svc, AttentionInbox inbox, TimeProvider clock) =>
            body.Minutes is not (> 0 and <= 7 * 24 * 60)
                ? Results.BadRequest(new { error = "minutes must be between 1 and 10080" })
                : Mark(body, svc, id => inbox.Snooze(id, TimeSpan.FromMinutes(body.Minutes.Value), clock.GetUtcNow())));
        api.MapPost("/restore", (AttentionMarkRequest body, AttentionService svc, AttentionInbox inbox) =>
            Mark(body, svc, inbox.Restore));

        // Compact phone view of the queue. Same shell rules as "/": token embedded, served only to allowed Hosts.
        app.MapGet("/m", (IWebHostEnvironment env, ApiToken token) =>
        {
            var path = Path.Combine(env.WebRootPath, "attention.html");
            if (!File.Exists(path)) return Results.Text("UI not built: wwwroot/attention.html missing", statusCode: 500);
            return Results.Content(File.ReadAllText(path).Replace("__AMC_TOKEN__", token.Value), "text/html; charset=utf-8");
        });
    }

    private static IResult Mark(AttentionMarkRequest body, AttentionService svc, Action<string> apply)
    {
        if (string.IsNullOrEmpty(body.Id) || body.Id.Length > MaxIdLength) return Results.BadRequest(new { error = "id is required" });
        var known = svc.Current(includeHidden: true);
        if (!known.Items.Any(i => i.Id == body.Id) && known.HiddenItems?.Any(i => i.Id == body.Id) != true)
            return Results.NotFound(new { error = "No such attention item (it may already be resolved)" });
        apply(body.Id);
        return Results.Ok(svc.Recompute());
    }
}
