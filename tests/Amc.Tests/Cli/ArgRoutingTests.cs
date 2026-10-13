using Amc.Cli;
using Amc.Cli.Infrastructure;

namespace Amc.Tests.Cli;

public class ArgRoutingTests
{
    private static ParsedArgs Parse(params string[] argv) => ArgParser.Parse(argv, Router.Find);

    [Fact]
    public void Parses_command_flags_values_and_positionals()
    {
        var a = Parse("events", "--agent", "abc", "--type", "tool.started", "--type=command.failed", "-n", "5", "-f", "--json");
        Assert.Equal("events", a.Command);
        Assert.Equal("abc", a.Opt("agent"));
        Assert.Equal(["tool.started", "command.failed"], a.Options["type"]);
        Assert.Equal(5, a.IntOpt("limit"));
        Assert.True(a.Flag("follow"));
        Assert.True(a.Json);
    }

    [Fact]
    public void Global_options_may_precede_the_command()
    {
        var a = Parse("--json", "--url", "http://127.0.0.1:1", "agents", "--all");
        Assert.Equal("agents", a.Command);
        Assert.True(a.Json);
        Assert.True(a.Flag("all"));
        Assert.Equal("http://127.0.0.1:1", a.Opt("url"));
    }

    [Fact]
    public void Double_dash_collects_the_rest_verbatim()
    {
        var a = Parse("run", "--", "claude", "--resume", "-f", "x");
        Assert.True(a.HasRest);
        Assert.Equal(["claude", "--resume", "-f", "x"], a.Rest);
        Assert.Empty(a.Positionals);
    }

    [Fact]
    public void Message_joins_multiple_words_and_keeps_negative_numbers_positional()
    {
        var a = Parse("message", "abc", "hello", "-1", "world");
        Assert.Equal(["abc", "hello", "-1", "world"], a.Positionals);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("agents", "--bogus")]
    [InlineData("inspect")]
    [InlineData("inspect", "a", "b")]
    [InlineData("events", "--limit")]
    [InlineData("agents", "--all=yes")]
    [InlineData("status", "-f")]
    [InlineData("pause", "x", "--", "y")]
    public void Usage_errors_are_rejected(params string[] argv) =>
        Assert.Throws<UsageException>(() => Parse(argv));

    [Fact]
    public void Limit_must_be_positive()
    {
        var a = Parse("events", "--limit", "0");
        Assert.Throws<UsageException>(() => a.IntOpt("limit"));
    }

    [Theory]
    [InlineData(2, "frobnicate")]
    [InlineData(2, "agents", "--bogus")]
    [InlineData(2)]
    [InlineData(0, "--help")]
    [InlineData(0, "help")]
    [InlineData(0, "help", "events")]
    [InlineData(0, "events", "--help")]
    [InlineData(2, "run")]
    [InlineData(2, "hooks", "frob")]
    [InlineData(2, "diff")]
    public async Task Exit_codes_for_routing(int expected, params string[] argv)
    {
        using var h = new CliHarness();
        Assert.Equal(expected, await h.Run(argv));
    }

    [Fact]
    public async Task Every_registered_command_has_help()
    {
        foreach (var c in Router.Commands)
        {
            using var h = new CliHarness();
            Assert.Equal(0, await h.Run(c.Name, "--help"));
            Assert.Contains("usage: agentctl " + c.Name, h.Out.ToString());
        }
    }
}
