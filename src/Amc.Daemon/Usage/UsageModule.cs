using System.Collections.Concurrent;
using Amc.Core.Usage;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Storage;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Usage;

/// <summary>
/// Usage over time and estimated API cost, computed on request from the event log (no extra tables, no background
/// work). Reports are cached briefly: the Usage tab polls, and a 180-day report reads every usage record kept.
/// </summary>
public sealed class UsageService(EventStore store, IOptions<AmcOptions> options, TimeProvider clock)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, Lazy<UsageReportResult> Report)> _cache = new();
    private PriceTable? _prices;

    public bool PricingEnabled => options.Value.Pricing.Enabled;
    public PriceTable Prices => _prices ??= new PriceTable(options.Value.Pricing);
    public int RetentionDays => Math.Max(1, options.Value.HistoryRetentionDays);

    /// <param name="range">"48h", "7d", "30d" … ("h" or "d" suffix) or "all" (everything kept, see HistoryRetentionDays).</param>
    public UsageReportResult Report(string? range, string? bucket, string? tz)
    {
        var span = ParseRange(range, RetentionDays);
        var b = bucket is UsageReport.Hour or UsageReport.Day ? bucket : span <= TimeSpan.FromDays(3) ? UsageReport.Hour : UsageReport.Day;
        if (b == UsageReport.Hour && span.TotalHours > UsageReport.MaxBuckets - 2) b = UsageReport.Day;
        var zone = UsageReport.Zone(tz);
        var key = $"{span.TotalMinutes}|{b}|{zone.Id}";
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(key, out var hit) && now - hit.At < CacheFor) return hit.Report.Value;
        var lazy = new Lazy<UsageReportResult>(() =>
        {
            var to = now.AddMinutes(1);
            var from = now - span;
            // Start at the first bucket boundary so the oldest bar is a whole hour or day.
            var starts = UsageReport.BucketStarts(from, to, b, zone);
            if (starts.Count > 0) from = starts[0];
            return UsageReport.Build(store.UsageResponses(from, to), PricingEnabled ? Prices : null, from, to, b, zone);
        });
        _cache[key] = (now, lazy);
        if (_cache.Count > 32) foreach (var k in _cache.Where(kv => now - kv.Value.At >= CacheFor).Select(kv => kv.Key).ToList()) _cache.TryRemove(k, out _);
        return lazy.Value;
    }

    public static TimeSpan ParseRange(string? range, int retentionDays)
    {
        var max = TimeSpan.FromDays(retentionDays);
        if (string.IsNullOrWhiteSpace(range) || range == "all") return max;
        var r = range.Trim().ToLowerInvariant();
        if (r.Length < 2 || !int.TryParse(r[..^1], out var n) || n <= 0) throw new ArgumentException("range: a number of hours or days such as 48h or 30d, or all");
        var span = r[^1] switch
        {
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => throw new ArgumentException("range: a number of hours or days such as 48h or 30d, or all"),
        };
        return span > max ? max : span;
    }
}

public static class UsageModule
{
    public static IServiceCollection AddUsage(this IServiceCollection services)
    {
        services.AddSingleton<UsageService>();
        return services;
    }

    public static WebApplication MapUsageEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/usage/report", (HttpRequest r, UsageService usage) =>
        {
            try
            {
                return Results.Ok(usage.Report(r.Query["range"].FirstOrDefault(), r.Query["bucket"].FirstOrDefault(), r.Query["tz"].FirstOrDefault()));
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

        api.MapGet("/pricing", (UsageService usage) => Results.Ok(new
        {
            enabled = usage.PricingEnabled,
            currency = usage.Prices.Currency,
            asOf = PriceTable.BuiltInAsOf,
            source = PriceTable.Source,
            unit = "per million tokens",
            models = usage.Prices.Entries.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new { model = kv.Key, custom = usage.Prices.Overridden.Contains(kv.Key), price = kv.Value }),
        }));
        return app;
    }
}
