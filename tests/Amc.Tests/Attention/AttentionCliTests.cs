using Amc.Core.Attention;
using Amc.Tests.Cli;
using static Amc.Tests.Attention.NotificationTests;

namespace Amc.Tests.Attention;

public sealed class AttentionCliTests : IDisposable
{
    private readonly CliHarness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Attention_lists_the_queue_with_priority_tool_and_evidence()
    {
        _h.Daemon.Get("/api/attention", """
            {"items":[{"id":"a","kind":"agent.blocked","priority":"urgent","agentIds":["codex:s1"],"tool":"codex",
              "title":"Needs your approval: Fix tests","since":"2026-10-07T11:59:00Z","evidence":"reported",
              "action":{"kind":"open-agent","label":"Open","target":"codex:s1"}}],"urgent":1,"hidden":2,"serverTime":"2026-10-07T12:00:00Z"}
            """);
        Assert.Equal(0, await _h.Run("attention"));
        var o = _h.Out.ToString();
        Assert.Contains("urgent", o);
        Assert.Contains("Needs your approval: Fix tests", o);
        Assert.Contains("codex", o);
        Assert.Contains("2 snoozed or dismissed", o);
    }

    [Fact]
    public async Task Empty_queue_says_so()
    {
        _h.Daemon.Get("/api/attention", """{"items":[],"urgent":0,"hidden":0}""");
        Assert.Equal(0, await _h.Run("attention"));
        Assert.Contains("Nothing needs you", _h.Out.ToString());
    }

    [Fact]
    public void Build_marks_hidden_items()
    {
        var t = Amc.Cli.Commands.AttentionCommand.Build([Item("a") with { Dismissed = true }, Item("b", AttentionPriority.Low)], DateTimeOffset.UtcNow, new(false));
        Assert.Equal(2, t.Count);
    }
}
