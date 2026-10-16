using Amc.Cli.Infrastructure;
using Amc.Core.Domain;
using Amc.Core.Guard;

namespace Amc.Cli.Commands;

/// <summary>Runaway guard: active alerts, live values per session and the configured thresholds.</summary>
public static class GuardCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        if (args.Json)
        {
            Api.PrintJson(ctx, (await ctx.Client.GetAsync("api/guard", ct)).Body);
            return ExitCodes.Ok;
        }
        var g = await ctx.Client.GetAsync<GuardStatus>("api/guard", ct);
        var agents = (await Api.Agents(ctx, ct)).ToDictionary(a => a.Id);
        var s = ctx.Style;
        var w = ctx.Out;
        var now = ctx.Now();
        string Name(string id) => Fmt.Trunc(agents.GetValueOrDefault(id) is { } a ? a.Title ?? a.Id : id, 40);

        w.WriteLine($"{s.Bold("guard")}     {(g.Enabled ? s.Green("on") : s.Dim("off"))}  auto-pause {(g.AutoPause ? s.Yellow("on") : "off")}" +
                    (g.AutoPause && !g.ControlEnabled ? s.Dim(" (inactive: EnableControl=false)") : ""));
        if (g.Alerts.Count == 0) w.WriteLine($"{s.Bold("alerts")}    {s.Green("none")}");
        else
        {
            w.WriteLine(s.Bold("alerts"));
            var t = new TextTable("SEVERITY", "SESSION", "RULE", "DETAIL", "SINCE", "EVIDENCE");
            foreach (var a in g.Alerts)
                t.Add((a.Severity == GuardSeverity.Critical ? s.Red("critical") : s.Yellow("warn")) + (a.Dismissed ? s.Dim(" (dismissed)") : ""),
                    Name(a.RootAgentId), a.Rule, Fmt.Trunc(a.Summary, 70), Fmt.Age(now, a.RaisedAt), Fmt.Evidence(a.Evidence));
            t.Write(w, s);
        }

        var roots = g.Roots.Where(r => r.ActiveSubAgents > 0 || r.SpawnedInWindow > 0 || r.TokensLastHour > 0).ToList();
        if (roots.Count > 0)
        {
            w.WriteLine(s.Bold("sessions"));
            var t = new TextTable("SESSION", "ACTIVE", "SPAWNED", "DEPTH", "TOK/MIN", "TOK/HOUR", "TOTAL");
            foreach (var r in roots.OrderByDescending(r => r.TokensPerMinute))
                t.Add(Name(r.RootAgentId), r.ActiveSubAgents.ToString(), r.SpawnedInWindow.ToString(), r.MaxDepth.ToString(),
                    UsageCommand.Tok(r.TokensPerMinute), UsageCommand.Tok(r.TokensLastHour), UsageCommand.Tok(r.SessionTokens));
            t.Write(w, s);
        }

        w.WriteLine(s.Bold("thresholds"));
        var rt = new TextTable("RULE", "WARN", "CRITICAL", "UNIT");
        foreach (var r in g.Rules)
            rt.Add(r.Label, r.Warn > 0 ? r.Warn.ToString("N0") : "off", r.Critical > 0 ? r.Critical.ToString("N0") : "off", r.Unit);
        rt.Write(w, s);
        w.WriteLine(s.Dim("Tokens exclude cache reads and are reported by the tools. Configure in config.json → \"Guard\"."));
        return ExitCodes.Ok;
    }
}
