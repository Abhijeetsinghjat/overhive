using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Collectors.Git;

/// <summary>Wiring for the Git / worktree collector. Call after the defaults in Composition.</summary>
public static class GitModule
{
    public static IServiceCollection AddGit(this IServiceCollection s)
    {
        s.AddSingleton<GitCli>();
        s.AddSingleton(sp => new GitCollector(
            sp.GetRequiredService<GitCli>(), sp.GetRequiredService<IEventSink>(), sp.GetRequiredService<StateStore>(),
            sp.GetRequiredService<HealthRegistry>(), sp.GetRequiredService<IOptions<AmcOptions>>(),
            sp.GetRequiredService<ILogger<GitCollector>>()));
        s.RemoveAll<IWorktreeIndex>();
        s.AddSingleton<IWorktreeIndex>(sp => sp.GetRequiredService<GitCollector>());
        s.AddHostedService(sp => sp.GetRequiredService<GitCollector>());
        return s;
    }

    public static void MapGitEndpoints(WebApplication app) => GitEndpoints.Map(app);
}
