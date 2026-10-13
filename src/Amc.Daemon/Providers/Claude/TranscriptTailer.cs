using System.Collections.Concurrent;
using Amc.Daemon.Infrastructure;

namespace Amc.Daemon.Providers.Claude;

/// <summary>
/// Hosted driver for <see cref="TranscriptIngestor"/>: initial backfill, then a recursive
/// FileSystemWatcher on ~/.claude/projects (*.jsonl, *.meta.json) with per-session
/// debouncing, plus a slow safety rescan for missed notifications.
/// </summary>
public sealed class TranscriptTailer(TranscriptIngestor ingestor, ClaudeContext ctx, HealthRegistry health, ILogger<TranscriptTailer> log) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SafetyRescan = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<(string, string), byte> _dirty = new();
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _rescan;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        FileSystemWatcher? watcher = null;
        var lastFull = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (watcher is null && Directory.Exists(ingestor.ProjectsDir)) watcher = CreateWatcher();
                if (_rescan || DateTimeOffset.UtcNow - lastFull > SafetyRescan)
                {
                    _rescan = false;
                    lastFull = DateTimeOffset.UtcNow;
                    await ingestor.ScanAllAsync(ct);
                    await ingestor.BackfillUsageAsync(ct); // one-time; no-op afterwards
                    ctx.TranscriptBackfillDone = true;
                }
                foreach (var key in _dirty.Keys.ToList())
                {
                    _dirty.TryRemove(key, out _);
                    await ingestor.ProcessSessionAsync(key.Item1, key.Item2, ct);
                }
                health.Ok(TranscriptIngestor.HealthName, watcher is null ? "waiting for " + ingestor.ProjectsDir : "watching");
                await _signal.WaitAsync(TimeSpan.FromSeconds(10), ct);
                await Task.Delay(Debounce, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Transcript tailing failed");
                health.Degraded(TranscriptIngestor.HealthName, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        watcher?.Dispose();
    }

    private FileSystemWatcher CreateWatcher()
    {
        var w = new FileSystemWatcher(ingestor.ProjectsDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024,
        };
        w.Filters.Add("*.jsonl");
        w.Filters.Add("*.meta.json");
        FileSystemEventHandler onChange = (_, e) => Mark(e.FullPath);
        w.Changed += onChange;
        w.Created += onChange;
        w.Renamed += (_, e) => Mark(e.FullPath);
        w.Error += (_, e) =>
        {
            health.Increment(TranscriptIngestor.HealthName, "watcher_overflows");
            _rescan = true;
            _signal.Release();
        };
        w.EnableRaisingEvents = true;
        return w;
    }

    private void Mark(string path)
    {
        if (ingestor.SessionOf(path) is not { } s) return;
        _dirty[s] = 1;
        _signal.Release();
    }
}
