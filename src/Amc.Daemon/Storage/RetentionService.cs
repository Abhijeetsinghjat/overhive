using Amc.Core.Events;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Storage;

/// <summary>
/// Keeps the event log bounded: compacts superseded "latest value" events, removes detail events after
/// <see cref="AmcOptions.DetailRetentionDays"/> and history after <see cref="AmcOptions.HistoryRetentionDays"/>.
/// A fresh state snapshot is saved first and only events covered by it are pruned, so restarts restore
/// exactly. (A reducer-version upgrade re-folds only the retained events; see TROUBLESHOOTING.md.)
/// </summary>
public sealed class RetentionService(EventStore store, StateStore state, HealthRegistry health, IOptions<AmcOptions> options,
    TimeProvider time, ILogger<RetentionService> log) : BackgroundService
{
    public const string HealthName = "retention";
    private readonly AmcOptions _o = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromHours(Math.Max(0.1, _o.RetentionIntervalHours));
        health.Register(HealthName, interval * 2 + TimeSpan.FromMinutes(10));
        await Task.Delay(TimeSpan.FromSeconds(30), time, ct); // let startup ingestion settle
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Run(() => RunOnce(ct), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Retention run failed");
                health.Degraded(HealthName, "last run failed: " + ex.Message);
            }
            await Task.Delay(interval, time, ct);
        }
    }

    public EventStore.PruneResult RunOnce(CancellationToken ct = default)
    {
        var before = store.FileBytes(_o.DbPath);
        var (seq, json) = state.Read(s => (s.Seq, Json.Serialize(s)));
        store.SaveSnapshot(seq, json);
        var r = store.Prune(time.GetUtcNow(), seq, _o.DetailRetentionDays, _o.HistoryRetentionDays, ct);
        var after = store.FileBytes(_o.DbPath);
        health.Increment(HealthName, "compacted", r.Compacted);
        health.Increment(HealthName, "detail_deleted", r.DetailDeleted);
        health.Increment(HealthName, "history_deleted", r.HistoryDeleted);
        health.SetCounter(HealthName, "db_mb", after / 1_000_000);
        health.Ok(HealthName, $"pruned {r.Compacted + r.DetailDeleted + r.HistoryDeleted:N0} event(s) in {r.Took.TotalSeconds:0.0}s; " +
                              $"db {before / 1_000_000} → {after / 1_000_000} MB; keeps detail {_o.DetailRetentionDays}d, history {_o.HistoryRetentionDays}d");
        log.LogInformation("Retention: compacted {C}, detail {D}, history {H} in {S:0.0}s; db {B} -> {A} MB",
            r.Compacted, r.DetailDeleted, r.HistoryDeleted, r.Took.TotalSeconds, before / 1_000_000, after / 1_000_000);
        return r;
    }
}
