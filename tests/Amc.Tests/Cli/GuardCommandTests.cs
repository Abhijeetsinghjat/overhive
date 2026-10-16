using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Guard;
using static Amc.Tests.Cli.CliHarness;

namespace Amc.Tests.Cli;

public class GuardCommandTests : IDisposable
{
    private const string MainId = "claude:c441aaaa-1111";
    private readonly CliHarness _h = new();

    public GuardCommandTests()
    {
        var g = new GuardSettings();
        _h.Daemon
            .Get("/api/agents", new List<Agent> { Agent(MainId, "Refactor billing", pid: 4242) })
            .Get("/api/guard", new GuardStatus(true, false, true, Now, GuardEvaluator.Describe(g),
                [new GuardMetrics(MainId, 31, 40, 5, 520_000, 9_000_000, 12_000_000, Evidence.Reported)],
                [new GuardAlert
                {
                    Key = GuardAlert.KeyOf(MainId, GuardRules.ActiveSubAgents), RootAgentId = MainId, Rule = GuardRules.ActiveSubAgents,
                    Severity = GuardSeverity.Critical, Value = 31, Threshold = 30, Summary = "31 sub-agents active at once (threshold 30)",
                    Evidence = Evidence.Reported, RaisedAt = Now - TimeSpan.FromMinutes(2),
                }]));
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Prints_alerts_live_values_and_thresholds()
    {
        Assert.Equal(0, await _h.Run("guard"));
        var o = _h.Out.ToString();
        Assert.Contains("critical", o);
        Assert.Contains("Refactor billing", o);
        Assert.Contains("31 sub-agents active at once", o);
        Assert.Contains("520k", o);
        Assert.Contains("Token burn rate", o);
        Assert.Contains("400,000", o);
    }

    [Fact]
    public async Task Json_prints_the_raw_status()
    {
        Assert.Equal(0, await _h.Run("guard", "--json"));
        Assert.Contains("\"activeSubAgents\": 31", _h.Out.ToString());
    }
}
