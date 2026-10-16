using Amc.Cli.Infrastructure;
using Amc.Core.Risk;

namespace Amc.Cli.Commands;

/// <summary>`risks`: risky actions recorded for agents (GET /api/risks), newest first.</summary>
public static class RisksCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        string? agentId = null;
        if (args.Opt("agent") is { } aq) agentId = (await Api.ResolveAgent(ctx, aq, ct)).Id;
        var severity = args.Opt("severity") is { } sv ? RiskSeverity.Normalize(sv) : null;
        if (args.Opt("severity") is { } raw && RiskSeverity.Normalize(raw) == RiskSeverity.Info && !raw.Equals("info", StringComparison.OrdinalIgnoreCase))
            throw new UsageException("--severity must be info, warn or critical");
        string? since = args.Opt("since") is { } s ? Fmt.ParseSince(s, ctx.Now()).ToString("O") : null;
        var path = "api/risks" + AmcClient.Query(("agent", agentId), ("descendants", agentId is null ? null : "true"), ("severity", severity),
            ("since", since), ("limit", args.IntOpt("limit")?.ToString()));
        if (args.Json)
        {
            Api.PrintJson(ctx, (await ctx.Client.GetAsync(path, ct)).Body);
            return ExitCodes.Ok;
        }
        var risks = await ctx.Client.GetAsync<List<Risk>>(path, ct);
        if (risks.Count == 0)
        {
            ctx.Out.WriteLine("No risky actions recorded" + (severity is null && since is null && agentId is null ? "." : " for these filters."));
            return ExitCodes.Ok;
        }
        Build(risks, ctx.Style, ctx.Now()).Write(ctx.Out, ctx.Style);
        return ExitCodes.Ok;
    }

    public static TextTable Build(IEnumerable<Risk> risks, Style s, DateTimeOffset now)
    {
        var t = new TextTable("AGE", "SEVERITY", "AGENT", "RULE", "EVIDENCE", "MATCHED", "WHY");
        foreach (var r in risks)
            t.Add(Fmt.Age(now, r.At),
                r.Severity switch { RiskSeverity.Critical => s.Red(s.Bold(r.Severity)), RiskSeverity.Warn => s.Yellow(r.Severity), _ => s.Dim(r.Severity) },
                r.AgentId is null ? "-" : AgentRef.Short(r.AgentId),
                r.RuleId,
                r.Evidence.ToString().ToLowerInvariant(),
                Fmt.Trunc(Fmt.OneLine(r.Span), 60),
                Fmt.Trunc(r.Explanation, 80));
        return t;
    }
}
