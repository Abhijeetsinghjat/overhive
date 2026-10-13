using Amc.Daemon.Collectors.Processes;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Terminals;

namespace Amc.Daemon;

/// <summary>Wiring for the PTY bridge. Call after the null defaults in <c>Composition</c>.</summary>
public static class TerminalModule
{
    public static IServiceCollection AddTerminals(this IServiceCollection services)
    {
        services.AddSingleton<TerminalHub>(sp => ActivatorUtilities.CreateInstance<TerminalHub>(sp,
            sp.GetService<IProcSource>() ?? new ProcFs()));
        services.AddSingleton<ITerminalHub>(sp => sp.GetRequiredService<TerminalHub>());
        services.AddSingleton<ITerminalDirectory>(sp => sp.GetRequiredService<TerminalHub>());
        services.AddHostedService<TerminalSweeper>();
        return services;
    }

    /// <summary>
    /// WebSocket /pty/attach?token=&amp;id= (producer: agentctl run) and /pty/view?token=&amp;id= (UI viewer).
    /// Token is checked by SecurityMiddleware (query param accepted on /pty).
    /// </summary>
    public static WebApplication MapTerminalEndpoints(this WebApplication app)
    {
        app.Map("/pty/attach", async (HttpContext ctx, TerminalHub hub) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.RunProducerAsync(ws, ctx.Request.Query["id"].FirstOrDefault(), ctx.RequestAborted);
        });
        app.Map("/pty/view", async (HttpContext ctx, TerminalHub hub) =>
        {
            var id = ctx.Request.Query["id"].FirstOrDefault();
            if (id is null || !TerminalHub.ValidId().IsMatch(id)) { ctx.Response.StatusCode = 400; return; }
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.RunViewerAsync(ws, id, ctx.RequestAborted);
        });
        return app;
    }
}

/// <summary>Every 5 s: declares terminals exited whose producer vanished and whose child pid is gone.</summary>
internal sealed class TerminalSweeper(TerminalHub hub, StateStore state, HealthRegistry health, ILogger<TerminalSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var restored = state.Read(s => s.Terminals.Values.Where(t => t.Alive).Select(t => (t.Id, t.Pid)).ToList());
                await hub.SweepAsync(restored, DateTimeOffset.UtcNow, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Terminal sweep failed");
                health.Increment(TerminalHub.HealthName, "errors");
            }
        }
    }
}
