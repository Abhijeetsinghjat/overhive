using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;

namespace Amc.Daemon.Providers.Claude;

/// <summary>
/// Hourly cleanup of the Claude module's in-memory correlation (see <see cref="ClaudeContext.Sweep"/>), the empty
/// mailbox queues and the rewake idle flags, so a daemon that runs for weeks keeps memory proportional to the
/// sessions that are alive, not to every session it ever saw.
/// </summary>
public sealed class ClaudeContextSweeper(ClaudeContext ctx, Mailbox mailbox, HealthRegistry health, TimeProvider time,
    ILogger<ClaudeContextSweeper> log, ClaudeRewake? rewake = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>One sweep. Returns the number of entries removed.</summary>
    public int SweepOnce()
    {
        var now = time.GetUtcNow();
        var removed = ctx.Sweep(now) + mailbox.PruneEmpty() + (rewake?.Prune(now) ?? 0);
        foreach (var (name, count) in ctx.Counts()) health.SetCounter(ClaudeHookReceiver.HealthName, "ctx_" + name, count);
        health.Increment(ClaudeHookReceiver.HealthName, "ctx_swept", removed);
        return removed;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var n = SweepOnce();
                if (n > 0) log.LogDebug("Claude context sweep removed {Count} entries", n);
            }
            catch (Exception ex) { log.LogWarning(ex, "Claude context sweep failed"); }
        }
    }
}
