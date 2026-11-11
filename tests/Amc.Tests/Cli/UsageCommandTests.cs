using System.Net;
using Amc.Cli.Infrastructure;
using Amc.Core.Domain;
using Amc.Core.Usage;
using static Amc.Tests.Cli.CliHarness;

namespace Amc.Tests.Cli;

public class UsageCommandTests : IDisposable
{
    private const string MainId = "claude:c441aaaa-1111", SubId = "claude:c441aaaa-1111:sub1", IdleId = "claude:d000-0000";
    private readonly CliHarness _h = new();

    public UsageCommandTests()
    {
        var main = Agent(MainId, "Refactor billing");
        main.ChildIds.Add(SubId);
        main.Usage.Add(1000, 200, 0, 5000);
        main.Usage.Responses = 3;
        main.UsageByModel["claude-opus-5-5"] = new TokenUsage { Input = 1000, Output = 200, CacheRead = 5000, Responses = 3 };
        var sub = Agent(SubId, "Explore", parent: MainId);
        sub.Usage.Add(10, 20, 0, 0);
        sub.Usage.Responses = 1;
        sub.UsageByModel["gpt-5-codex"] = new TokenUsage { Input = 10, Output = 20, Responses = 1 };
        _h.Daemon.Get("/api/agents", new List<Agent> { main, sub, Agent(IdleId, "Idle") });
    }

    public void Dispose() => _h.Dispose();

    private static UsageReportResult Report(DateTimeOffset from, string bucket = UsageReport.Day) =>
        UsageReport.Build([
            new UsageResponse(MainId, "claude-opus-5-5", Now.AddHours(-2), 1_000_000, 100_000, 0, 0),
            new UsageResponse(SubId, "gpt-5-codex", Now.AddHours(-1), 10, 20, 0, 0),
        ], new PriceTable(), from, Now.AddMinutes(1), bucket, TimeZoneInfo.Utc);

    [Fact]
    public async Task Sessions_show_the_estimated_cost_including_sub_agents_and_unpriced_models_are_flagged()
    {
        _h.Daemon.Get("/api/usage/report", Report(Now.AddDays(-180)));
        Assert.Equal(0, await _h.Run("usage", "--agents"));
        var o = _h.Out.ToString();
        Assert.Contains("EST. COST", o);
        Assert.Contains("$6.00+", o);        // opus 5.5: 1M in ($4) + 100k out ($2); "+": the sub-agent's model has no price
        Assert.Contains("not priced", o);    // gpt-5-codex in the model table
        Assert.Contains("gpt-5-codex", o);
        Assert.Contains("not what a subscription charges", o);
        var idleLine = o.Split('\n').Single(l => l.StartsWith("Idle"));
        Assert.DoesNotContain("$", idleLine); // no usage: blank, not $0.00
    }

    [Fact]
    public async Task An_older_daemon_without_the_report_still_prints_tokens()
    {
        _h.Daemon.On("GET", "/api/usage/report", _ => (HttpStatusCode.NotFound, """{"error":"no route"}"""));
        Assert.Equal(0, await _h.Run("usage"));
        var o = _h.Out.ToString();
        Assert.Contains("Refactor billing", o);
        Assert.DoesNotContain("EST. COST", o);
        Assert.Contains("update Overhive", o);
    }

    [Fact]
    public async Task History_prints_one_row_per_day_with_cost_and_top_model_and_passes_range_and_bucket()
    {
        _h.Daemon.Get("/api/usage/report", Report(Now.AddDays(-7)));
        Assert.Equal(0, await _h.Run("usage", "--history", "--range", "7d"));
        var o = _h.Out.ToString();
        Assert.Contains("DAY", o);
        Assert.Contains("Wed 2026-10-07", o);
        Assert.Contains("claude-opus-5-5", o);
        Assert.Contains("$6.00+", o);
        var q = _h.Daemon.Single("GET", "/api/usage/report").Uri.Query;
        Assert.Contains("range=7d", q);
        Assert.Contains("bucket=day", q);
    }

    [Fact]
    public async Task History_rejects_an_unknown_bucket()
    {
        Assert.Equal(ExitCodes.Usage, await _h.Run("usage", "--history", "--by", "week"));
    }

    [Theory]
    [InlineData(0.004, "USD", "<$0.01")]
    [InlineData(12.5, "USD", "$12.50")]
    [InlineData(1234.6, "USD", "$1,235")]
    [InlineData(3, "EUR", "3.00 EUR")]
    public void Money_formats_small_large_and_other_currencies(double v, string currency, string expected) =>
        Assert.Equal(expected, Amc.Cli.Commands.UsageCommand.Money(v, currency));
}
