using Amc.Daemon.Collectors.Processes;

namespace Amc.Daemon;

/// <summary>Wiring for the /proc collector. Call after the null defaults in <c>Composition</c>.</summary>
public static class ProcessModule
{
    public static IServiceCollection AddProcesses(this IServiceCollection services)
    {
        services.AddSingleton<IProcSource>(_ => new ProcFs());
        services.AddSingleton<LiveProcessTable>();
        services.AddSingleton<ILiveProcesses>(sp => sp.GetRequiredService<LiveProcessTable>());
        services.AddSingleton<LiveProcessCollector>();
        services.AddHostedService(sp => sp.GetRequiredService<LiveProcessCollector>());
        return services;
    }

    /// <summary>GET /api/processes/{pid} → the live process and its descendant pids. (/api/processes is in ApiEndpoints.)</summary>
    public static WebApplication MapProcessEndpoints(this WebApplication app)
    {
        app.MapGet("/api/processes/{pid:int}", (int pid, ILiveProcesses live) =>
            live.Get(pid) is { } p ? Results.Ok(new { process = p, tree = live.Tree(pid) }) : Results.NotFound());
        return app;
    }
}
