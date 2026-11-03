using Amc.Core.Attention;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Collectors.Git;
using Amc.Daemon.Collectors.Processes;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Worktrees;

/// <param name="ConfirmIgnored">Also delete ignored files that are not regenerable (e.g. .env) listed in the plan.</param>
public sealed record CleanupRequest(bool Confirm = false, bool DryRun = false, bool ConfirmIgnored = false);

/// <summary>
/// Periodic merge detection (every minute it checks whether a repository is due; gh is asked at most every
/// <see cref="WorktreeCleanupOptions.PollMinutes"/>) and, in auto mode, automatic cleanup after the grace period.
/// </summary>
public sealed class WorktreeCleanupService(MergeDetector detector, WorktreeCleaner cleaner, StateStore state, IOptions<AmcOptions> options,
    HealthRegistry health, TimeProvider clock, ILogger<WorktreeCleanupService> log) : BackgroundService
{
    public const string HealthName = "worktree-cleanup";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value.WorktreeCleanup;
        if (o.EffectiveMode == CleanupMode.Off) { health.Disabled(HealthName, "WorktreeCleanup:Mode=off"); return; }
        health.Register(HealthName, Tick * 5);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), clock, ct); // let repository discovery settle
            using var timer = new PeriodicTimer(Tick, clock);
            do await PassAsync(o, ct);
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task PassAsync(WorktreeCleanupOptions o, CancellationToken ct)
    {
        try
        {
            var r = await detector.CheckAsync(force: false, ct);
            if (o.EffectiveMode == CleanupMode.Auto) await RunAutoAsync(ct);
            health.Ok(HealthName, $"{o.EffectiveMode.ToString().ToLowerInvariant()}: {r.Merged} merged, {r.LikelyMerged} likely merged of {r.Candidates} linked worktrees"
                                  + (r.GhError is { } e ? $" ({e}; git-only detection)" : ""));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Worktree cleanup pass failed");
            health.Failed(HealthName, ex.Message);
        }
    }

    /// <summary>Auto mode: cleans every merged worktree whose grace period has passed. Failed and partial cleanups are not retried.</summary>
    public async Task<List<CleanupResult>> RunAutoAsync(CancellationToken ct)
    {
        var o = options.Value.WorktreeCleanup;
        var results = new List<CleanupResult>();
        if (o.EffectiveMode != CleanupMode.Auto) return results;
        var now = clock.GetUtcNow();
        var grace = TimeSpan.FromMinutes(Math.Max(0, o.GracePeriodMinutes));
        var due = state.Read(s => s.Worktrees.Values
            .Where(w => !w.Removed && !w.IsMain && w.Merged is { } m && now - m.DetectedAt >= grace
                        && (m.Source == MergeSources.GitHub || o.AutoRemoveLikelyMerged)
                        && w.LastCleanup?.Outcome is not (CleanupOutcomes.Failed or CleanupOutcomes.Partial))
            .Select(w => w.Id).ToList());
        foreach (var id in due) results.Add(await cleaner.CleanupAsync(id, confirmed: false, dryRun: false, auto: true, ct));
        return results;
    }
}

/// <summary>Wiring and REST endpoints for merged-worktree detection and cleanup.</summary>
public static class WorktreeCleanupModule
{
    public static IServiceCollection AddWorktreeCleanup(this IServiceCollection s, AmcOptions options)
    {
        s.AddSingleton<IGitHubCli, GhCli>();
        s.AddSingleton<MergeDetector>();
        s.AddSingleton(sp => new WorktreeCleaner(sp.GetRequiredService<GitCli>(), sp.GetRequiredService<StateStore>(), sp.GetRequiredService<IEventSink>(),
            sp.GetService<IProcSource>(), sp.GetRequiredService<IOptions<AmcOptions>>(), sp.GetService<GitCollector>(), sp.GetRequiredService<ILogger<WorktreeCleaner>>()));
        s.AddSingleton<WorktreeCleanupService>();
        s.AddHostedService(sp => sp.GetRequiredService<WorktreeCleanupService>());
        // Also in auto mode: a worktree auto mode may not remove (e.g. ignored .env files) stays a suggestion.
        if (options.WorktreeCleanup.EffectiveMode != CleanupMode.Off) s.AddSingleton<IAttentionProvider, MergedWorktreeAttentionProvider>();
        return s;
    }

    public static void MapWorktreeCleanupEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/worktrees");

        // Plan and live safety checks (read-only; what the confirm dialog shows).
        api.MapGet("/{id}/cleanup", async (string id, WorktreeCleaner cleaner, CancellationToken ct) =>
            await cleaner.PlanAsync(id, ct) is { } plan ? Results.Ok(plan) : Results.NotFound(new { error = "unknown worktree" }));

        // Destructive: gated exactly like /api/agents/{id}/control (token, Origin, EnableControl) plus confirm=true.
        api.MapPost("/{id}/cleanup", async (string id, CleanupRequest? body, WorktreeCleaner cleaner, IOptions<AmcOptions> opt, CancellationToken ct) =>
        {
            body ??= new CleanupRequest();
            if (!body.DryRun && !opt.Value.EnableControl)
                return Results.Json(new OperationResult(false, "Control is disabled (EnableControl=false)", 403), statusCode: 403);
            var r = await cleaner.CleanupAsync(id, body.Confirm, body.DryRun, auto: false, ct, body.ConfirmIgnored);
            return Results.Json(r, statusCode: r.StatusCode);
        });

        // On-demand merge check (asks gh at most every 30 s per repository).
        api.MapPost("/check-merged", async (MergeDetector detector, CancellationToken ct) => Results.Ok(await detector.CheckAsync(force: true, ct)));
    }
}
