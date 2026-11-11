namespace Amc.Core.Usage;

/// <summary>Token counts plus estimated cost. Cost covers only priced responses; <see cref="UnpricedTokens"/> are the rest.</summary>
public sealed class UsageTotals
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheWrite { get; set; }
    public long CacheRead { get; set; }
    public long Responses { get; set; }
    /// <summary>Null when pricing is off.</summary>
    public double? Cost { get; set; }
    /// <summary>Tokens of responses whose model has no price (missing from <see cref="Cost"/>, not zero).</summary>
    public long UnpricedTokens { get; set; }
    /// <summary>
    /// Priced cache-write tokens whose cache lifetime was not recorded (usage from before Overhive kept it): priced at
    /// the 5-minute rate, so the cost is a lower bound when the tool actually wrote 1-hour cache entries.
    /// </summary>
    public long CacheWriteUnknownTtl { get; set; }

    public long Total => Input + Output + CacheWrite + CacheRead;

    public void Add(UsageResponse r, double? cost, bool pricing)
    {
        Input += r.Input; Output += r.Output; CacheWrite += r.CacheWrite; CacheRead += r.CacheRead; Responses++;
        if (!pricing) return;
        Cost = (Cost ?? 0) + (cost ?? 0);
        if (cost is null) UnpricedTokens += r.Total;
        else if (r.CacheWrite1h is null) CacheWriteUnknownTtl += r.CacheWrite;
    }
}

public sealed record UsageBucket(DateTimeOffset Start, UsageTotals Totals, IReadOnlyDictionary<string, UsageTotals> ByModel);

public sealed record UsageModelRow(string Model, bool Priced, UsageTotals Totals);

public sealed record UsageReportResult(
    DateTimeOffset From, DateTimeOffset To, string Bucket, string TimeZone, bool Pricing, string Currency,
    string PricesAsOf, string PricesSource,
    UsageTotals Totals, IReadOnlyList<UsageBucket> Buckets, IReadOnlyList<UsageModelRow> Models,
    IReadOnlyDictionary<string, UsageTotals> Agents, IReadOnlyList<string> UnpricedModels);

/// <summary>Builds usage-over-time reports from model responses. Pure: no I/O, no clock.</summary>
public static class UsageReport
{
    public const string Hour = "hour", Day = "day";
    /// <summary>Upper bound on buckets per report (a year of days, or ~30 days of hours).</summary>
    public const int MaxBuckets = 750;
    private const string NoModel = "(unknown model)";

    /// <param name="responses">Responses with <see cref="UsageResponse.At"/> in [from, to).</param>
    /// <param name="prices">Null when pricing is off.</param>
    public static UsageReportResult Build(IEnumerable<UsageResponse> responses, PriceTable? prices, DateTimeOffset from, DateTimeOffset to,
        string bucket, TimeZoneInfo tz)
    {
        bucket = bucket == Hour ? Hour : Day;
        var pricing = prices is not null;
        var starts = BucketStarts(from, to, bucket, tz);
        var buckets = starts.Select(s => (Start: s, Totals: new UsageTotals(), ByModel: new Dictionary<string, UsageTotals>(StringComparer.Ordinal))).ToList();
        var totals = new UsageTotals();
        var models = new Dictionary<string, (bool Priced, UsageTotals T)>(StringComparer.Ordinal);
        var agents = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
        if (pricing) totals.Cost = 0;

        foreach (var r in responses)
        {
            if (r.At < from || r.At >= to) continue;
            var cost = prices?.Cost(r);
            var model = string.IsNullOrWhiteSpace(r.Model) ? NoModel : r.Model;
            totals.Add(r, cost, pricing);
            if (!models.TryGetValue(model, out var m)) models[model] = m = (cost is not null || !pricing, new UsageTotals());
            m.T.Add(r, cost, pricing);
            if (r.AgentId is { } aid)
            {
                if (!agents.TryGetValue(aid, out var at)) agents[aid] = at = new UsageTotals();
                at.Add(r, cost, pricing);
            }
            var i = Index(starts, r.At);
            if (i < 0) continue;
            var b = buckets[i];
            b.Totals.Add(r, cost, pricing);
            if (!b.ByModel.TryGetValue(model, out var bm)) b.ByModel[model] = bm = new UsageTotals();
            bm.Add(r, cost, pricing);
        }

        return new UsageReportResult(from, to, bucket, tz.Id, pricing, prices?.Currency ?? "USD", PriceTable.BuiltInAsOf, PriceTable.Source,
            totals,
            buckets.Select(b => new UsageBucket(b.Start, b.Totals, b.ByModel)).ToList(),
            models.OrderByDescending(kv => kv.Value.T.Cost ?? 0).ThenByDescending(kv => kv.Value.T.Total)
                .Select(kv => new UsageModelRow(kv.Key, kv.Value.Priced, kv.Value.T)).ToList(),
            agents,
            pricing ? models.Where(kv => !kv.Value.Priced).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList() : []);
    }

    /// <summary>
    /// Bucket boundaries in local time of <paramref name="tz"/> (midnight or the top of the hour; a DST day has 23 or
    /// 25 hours), as UTC instants, starting with the bucket that contains <paramref name="from"/>.
    /// </summary>
    public static List<DateTimeOffset> BucketStarts(DateTimeOffset from, DateTimeOffset to, string bucket, TimeZoneInfo tz)
    {
        var list = new List<DateTimeOffset>();
        var local = TimeZoneInfo.ConvertTime(from, tz).DateTime;
        var cursor = bucket == Hour ? new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0) : local.Date;
        while (list.Count < MaxBuckets)
        {
            var start = ToInstant(cursor, tz);
            if (start >= to) break;
            if (list.Count == 0 || start > list[^1]) list.Add(start); // a repeated (DST fall-back) hour collapses into one bucket
            cursor = bucket == Hour ? cursor.AddHours(1) : cursor.AddDays(1);
        }
        return list;
    }

    /// <summary>Local wall-clock time → instant; a time skipped by a DST jump maps to the first valid instant after it.</summary>
    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddMinutes(15);
        var offset = tz.IsAmbiguousTime(unspecified) ? tz.GetAmbiguousTimeOffsets(unspecified).Max() : tz.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    private static int Index(List<DateTimeOffset> starts, DateTimeOffset at)
    {
        if (starts.Count == 0 || at < starts[0]) return -1;
        int lo = 0, hi = starts.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (starts[mid] <= at) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>An IANA (or Windows) zone id, falling back to the machine's local zone when unknown or empty.</summary>
    public static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }
}
