using Amc.Core.Usage;

namespace Amc.Tests.Core;

public class PriceTableTests
{
    private static readonly PriceTable Builtin = new();

    [Theory]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4-5")]          // dated snapshot
    [InlineData("us.anthropic.claude-opus-4-8-v1:0", "claude-opus-4-8")]   // Bedrock
    [InlineData("anthropic.claude-sonnet-4-5-20250929-v1:0", "claude-sonnet-4-5")]
    [InlineData("claude-opus-4-5@20251101", "claude-opus-4-5")]            // Vertex
    [InlineData("claude-opus-5-5[1m]", "claude-opus-5-5")]                 // Claude Code context suffix
    [InlineData("CLAUDE-OPUS-5-5", "claude-opus-5-5")]
    public void Model_ids_are_normalized_before_lookup(string reported, string entry) =>
        Assert.Same(Builtin.Entries[entry], Builtin.Find(reported));

    [Fact]
    public void A_shorter_id_never_matches_a_longer_model_and_unknown_models_have_no_price()
    {
        Assert.Equal(5, Builtin.Find("claude-opus-5")!.Input);
        Assert.Equal(4, Builtin.Find("claude-opus-5-5")!.Input);
        Assert.Null(Builtin.Find("claude-opus-6"));
        Assert.Null(Builtin.Find("gpt-5-codex"));
        Assert.Null(Builtin.Find(null));
        Assert.Null(Builtin.Find("  "));
    }

    [Fact]
    public void Config_entries_add_models_replace_builtins_and_prefix_wildcards_use_the_longest_match()
    {
        var t = new PriceTable(new PricingSettings
        {
            Models =
            {
                ["gpt-5*"] = new ModelPrice { Input = 1.25, Output = 10, CacheRead = 0.125 },
                ["gpt-5-mini*"] = new ModelPrice { Input = 0.25, Output = 2 },
                ["claude-opus-5-5"] = new ModelPrice { Input = 1, Output = 1 },
            },
        });
        Assert.Equal(1.25, t.Find("gpt-5-codex")!.Input);
        Assert.Equal(0.25, t.Find("gpt-5-mini-2026")!.Input);
        Assert.Equal(1, t.Find("claude-opus-5-5")!.Input);
        Assert.Contains("claude-opus-5-5", t.Overridden);
        Assert.Equal(4, Builtin.Find("claude-opus-5-5")!.Input); // the built-in table itself is untouched
    }

    private static UsageResponse R(string model, long i = 0, long o = 0, long cw = 0, long cr = 0, long? cw1h = null, string? speed = null, string? geo = null) =>
        new("a", model, DateTimeOffset.UnixEpoch, i, o, cw, cr, cw1h, speed, geo);

    [Fact]
    public void Cost_uses_each_token_kind_and_splits_cache_writes_by_lifetime()
    {
        Assert.Equal(4.0, Builtin.Cost(R("claude-opus-5-5", i: 1_000_000))!.Value, 9);
        Assert.Equal(20.0, Builtin.Cost(R("claude-opus-5-5", o: 1_000_000))!.Value, 9);
        Assert.Equal(0.2, Builtin.Cost(R("claude-opus-5-5", cr: 1_000_000))!.Value, 9);
        Assert.Equal(5.0, Builtin.Cost(R("claude-opus-5-5", cw: 1_000_000))!.Value, 9);                 // lifetime unknown: 5-minute rate
        Assert.Equal(0.4 * 5 + 0.6 * 8, Builtin.Cost(R("claude-opus-5-5", cw: 1_000_000, cw1h: 600_000))!.Value, 9);
        Assert.Equal(8.0, Builtin.Cost(R("claude-opus-5-5", cw: 1_000_000, cw1h: 5_000_000))!.Value, 9); // clamped to the writes
        Assert.Null(Builtin.Cost(R("gpt-5", i: 1_000_000)));
    }

    [Fact]
    public void Haiku_5_5_prompts_over_100k_tokens_pay_the_long_context_prices_counting_cache_tokens()
    {
        // 100,000 prompt tokens exactly: base prices. One more (via a cache read): every token at the higher tier.
        Assert.Equal((60_000 * 0.10 + 40_000 * 0.01 + 1_000 * 0.50) / 1e6, Builtin.Cost(R("claude-haiku-5-5", i: 60_000, cr: 40_000, o: 1_000))!.Value, 12);
        Assert.Equal((60_000 * 0.50 + 40_001 * 0.05 + 1_000 * 2.50) / 1e6, Builtin.Cost(R("claude-haiku-5-5", i: 60_000, cr: 40_001, o: 1_000))!.Value, 12);
        // The long tier keeps the base entry's US-only multiplier.
        Assert.Equal(1.1 * 200_000 * 0.50 / 1e6, Builtin.Cost(R("claude-haiku-5-5", i: 200_000, geo: "us"))!.Value, 12);
    }

    [Fact]
    public void Fast_mode_and_US_only_inference_multiply_where_the_model_has_those_prices()
    {
        Assert.Equal(8.0, Builtin.Cost(R("claude-opus-5-5", i: 1_000_000, speed: "fast"))!.Value, 9);
        Assert.Equal(4.4, Builtin.Cost(R("claude-opus-5-5", i: 1_000_000, geo: "us"))!.Value, 9);
        Assert.Equal(8.8, Builtin.Cost(R("claude-opus-5-5", i: 1_000_000, speed: "fast", geo: "us"))!.Value, 9);
        Assert.Equal(5.0, Builtin.Cost(R("claude-opus-4-7", i: 1_000_000, speed: "fast"))!.Value, 9); // no fast price listed
        Assert.Equal(5.0, Builtin.Cost(R("claude-opus-4-5", i: 1_000_000, geo: "us"))!.Value, 9);     // before 4.6: no surcharge
    }

    [Fact]
    public void Missing_cache_prices_fall_back_to_the_input_price()
    {
        var t = new PriceTable(new PricingSettings { Models = { ["m"] = new ModelPrice { Input = 2, Output = 3 } } });
        Assert.Equal(0.5 * 2 + 0.5 * 2 + 1 * 2, t.Cost(R("m", cw: 1_000_000, cw1h: 500_000, cr: 1_000_000))!.Value, 9); // writes (both lifetimes) + reads
    }
}

public class UsageReportTests
{
    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static UsageResponse R(string at, string? model = "claude-opus-5-5", string agent = "a1", long i = 0, long o = 0, long cw = 0, long? cw1h = null) =>
        new(agent, model, DateTimeOffset.Parse(at), i, o, cw, 0, cw1h);

    [Fact]
    public void Day_buckets_start_at_local_midnight_of_the_viewer_including_half_hour_offsets()
    {
        var from = DateTimeOffset.Parse("2026-11-09T18:30:00Z"); // Nov 10 00:00 IST
        var to = DateTimeOffset.Parse("2026-11-11T18:30:00Z");
        var rep = UsageReport.Build([
            R("2026-11-10T18:29:59Z", i: 1_000_000),               // Nov 10, 23:59:59 IST
            R("2026-11-10T18:30:00Z", o: 1_000_000),               // Nov 11, 00:00 IST
            R("2026-11-11T18:30:00Z", i: 1),                       // outside [from, to)
        ], new PriceTable(), from, to, UsageReport.Day, Kolkata);
        Assert.Equal([DateTimeOffset.Parse("2026-11-09T18:30:00Z"), DateTimeOffset.Parse("2026-11-10T18:30:00Z")], rep.Buckets.Select(b => b.Start));
        Assert.Equal(4.0, rep.Buckets[0].Totals.Cost!.Value, 9);
        Assert.Equal(20.0, rep.Buckets[1].Totals.Cost!.Value, 9);
        Assert.Equal(24.0, rep.Totals.Cost!.Value, 9);
        Assert.Equal(2, rep.Totals.Responses);
        Assert.Equal("Asia/Kolkata", rep.TimeZone);
    }

    [Fact]
    public void Daylight_saving_days_have_25_hours_and_the_repeated_hour_is_one_bucket()
    {
        // 2026-11-01: New York falls back from EDT (-4) to EST (-5) at 02:00 local.
        var days = UsageReport.BucketStarts(DateTimeOffset.Parse("2026-11-01T04:00:00Z"), DateTimeOffset.Parse("2026-11-03T05:00:00Z"), UsageReport.Day, NewYork);
        Assert.Equal([DateTimeOffset.Parse("2026-11-01T04:00:00Z"), DateTimeOffset.Parse("2026-11-02T05:00:00Z")], days);
        var hours = UsageReport.BucketStarts(DateTimeOffset.Parse("2026-11-01T04:00:00Z"), DateTimeOffset.Parse("2026-11-02T05:00:00Z"), UsageReport.Hour, NewYork);
        Assert.Equal(24, hours.Count); // 25 real hours, the repeated 01:00 is one 2-hour bucket
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:00:00Z"), hours[1]);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T07:00:00Z"), hours[2]);
        var rep = UsageReport.Build([R("2026-11-01T06:30:00Z", i: 10)], null, hours[0], DateTimeOffset.Parse("2026-11-02T05:00:00Z"), UsageReport.Hour, NewYork);
        Assert.Equal(10, rep.Buckets[1].Totals.Input);
    }

    [Fact]
    public void Spring_forward_hour_is_skipped_without_an_empty_or_duplicate_bucket()
    {
        // 2026-03-08: 02:00 local does not exist in New York.
        var hours = UsageReport.BucketStarts(DateTimeOffset.Parse("2026-03-08T05:00:00Z"), DateTimeOffset.Parse("2026-03-09T04:00:00Z"), UsageReport.Hour, NewYork);
        Assert.Equal(23, hours.Count);
        Assert.Equal(hours.Count, hours.Distinct().Count());
    }

    [Fact]
    public void Unpriced_models_are_missing_from_the_cost_not_zero_and_are_listed()
    {
        var from = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
        var rep = UsageReport.Build([
            R("2026-11-10T01:00:00Z", i: 1_000_000),
            R("2026-11-10T02:00:00Z", model: "gpt-5-codex", agent: "codex:1", i: 500, o: 500),
            R("2026-11-10T03:00:00Z", model: null, agent: "a2", i: 7),
        ], new PriceTable(), from, from.AddDays(1), UsageReport.Day, TimeZoneInfo.Utc);
        Assert.Equal(4.0, rep.Totals.Cost!.Value, 9);
        Assert.Equal(1007, rep.Totals.UnpricedTokens);
        Assert.Equal(["(unknown model)", "gpt-5-codex"], rep.UnpricedModels);
        Assert.False(rep.Models.Single(m => m.Model == "gpt-5-codex").Priced);
        Assert.True(rep.Models.Single(m => m.Model == "claude-opus-5-5").Priced);
        Assert.Equal(1000, rep.Agents["codex:1"].UnpricedTokens);
        Assert.Equal(0, rep.Agents["codex:1"].Cost);
        Assert.Equal("claude-opus-5-5", rep.Models[0].Model); // most expensive first
    }

    [Fact]
    public void Cache_writes_without_a_recorded_lifetime_are_counted_so_the_estimate_can_say_it_is_a_floor()
    {
        var from = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
        var rep = UsageReport.Build([
            R("2026-11-10T01:00:00Z", cw: 100),                // older usage: lifetime unknown
            R("2026-11-10T02:00:00Z", cw: 50, cw1h: 0),        // recorded: all 5-minute
            R("2026-11-10T03:00:00Z", cw: 30, cw1h: 30),       // recorded: all 1-hour
        ], new PriceTable(), from, from.AddDays(1), UsageReport.Day, TimeZoneInfo.Utc);
        Assert.Equal(100, rep.Totals.CacheWriteUnknownTtl);
    }

    [Fact]
    public void Pricing_off_reports_tokens_only()
    {
        var from = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
        var rep = UsageReport.Build([R("2026-11-10T01:00:00Z", i: 5)], null, from, from.AddHours(3), UsageReport.Hour, TimeZoneInfo.Utc);
        Assert.False(rep.Pricing);
        Assert.Null(rep.Totals.Cost);
        Assert.Equal(0, rep.Totals.UnpricedTokens);
        Assert.Empty(rep.UnpricedModels);
        Assert.Equal(3, rep.Buckets.Count);
        Assert.Equal(5, rep.Buckets[1].ByModel["claude-opus-5-5"].Input);
    }

    [Fact]
    public void Bucket_count_is_capped_and_unknown_zones_fall_back_to_local()
    {
        var from = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        Assert.Equal(UsageReport.MaxBuckets, UsageReport.BucketStarts(from, from.AddDays(40), UsageReport.Hour, TimeZoneInfo.Utc).Count);
        Assert.Equal(TimeZoneInfo.Local, UsageReport.Zone("Mars/Olympus_Mons"));
        Assert.Equal(TimeZoneInfo.Local, UsageReport.Zone(null));
        Assert.Equal("Asia/Kolkata", UsageReport.Zone("Asia/Kolkata").Id);
    }
}
