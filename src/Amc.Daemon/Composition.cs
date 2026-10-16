using Amc.Daemon.Attention;
using Amc.Daemon.Collectors.Git;
using Amc.Daemon.Conflicts;
using Amc.Daemon.Guard;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Providers.Claude;
using Amc.Daemon.Tools;

namespace Amc.Daemon;

/// <summary>
/// Single place where collectors, providers and their endpoints are wired. Each module
/// (Git, Process, Terminal, Claude provider, Simulator) contributes one line here.
/// </summary>
public static class Composition
{
    public static IServiceCollection AddCollectors(this IServiceCollection services, AmcOptions options)
    {
        // Defaults; modules replace these registrations as they are added.
        services.AddSingleton<IWorktreeIndex, NullWorktreeIndex>();
        services.AddSingleton<ILiveProcesses, NullLiveProcesses>();
        services.AddSingleton<ITerminalHub, NullTerminalHub>();
        services.AddGit();
        services.AddProcesses();
        services.AddTerminals();
        services.AddClaude();
        services.AddTools(options);
        services.AddConflicts();
        services.AddGuard();
        services.AddAttention(options);
        return services;
    }

    public static WebApplication MapCollectorEndpoints(this WebApplication app)
    {
        GitModule.MapGitEndpoints(app);
        app.MapProcessEndpoints();
        app.MapTerminalEndpoints();
        ClaudeModule.MapClaudeEndpoints(app);
        ToolsModule.MapToolEndpoints(app);
        app.MapConflictEndpoints();
        app.MapGuardEndpoints();
        AttentionModule.MapAttentionEndpoints(app);
        return app;
    }
}
