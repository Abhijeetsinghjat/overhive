using Amc.Cli.Infrastructure;
using Amc.Core.Domain;
using Amc.Core.Usage;

namespace Amc.Cli.Commands;

/// <summary>
/// Token usage per session (including sub-agents), optionally per agent, plus totals per model, with the estimated
/// API cost when the daemon reports it (docs/USAGE.md). <c>--history</c> prints usage per day or hour instead.
/// </summary>
public static class UsageCommand
{
    private const string CostNote = "Est. cost: what these tokens would cost at the provider's API list prices (prices as of {0}); not what a subscription charges.";

    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        if (args.Flag("history")) return await HistoryAsync(ctx, args, ct);
        var agents = (await Api.Agents(ctx, ct)).ToDictionary(a => a.Id);
        var report = await TryReport(ctx, "all", "day", ct);
        var pricing = report?.Pricing == true;
        var sessions = agents.Values.Where(a => a.ParentId is null || !agents.ContainsKey(a.ParentId))
            .Select(a => (Agent: a, Usage: Rollup(a, agents))).OrderByDescending(x => x.Usage.Total).ToList();
        (double Cost, long Unpriced)? CostOf(Agent a) => pricing ? CostRollup(a, agents, report!) : null;
        if (args.Json)
        {
            Api.PrintJson(ctx, System.Text.Json.JsonSerializer.Serialize(sessions.Select(x => new
            {
                id = x.Agent.Id, title = x.Agent.Title, own = x.Agent.Usage, withSubAgents = x.Usage,
                estimatedCost = CostOf(x.Agent) is { } c ? new { amount = Math.Round(c.Cost, 6), currency = report!.Currency, unpricedTokens = c.Unpriced } : null,
            }), Amc.Core.Events.Json.Options));
            return ExitCodes.Ok;
        }
        var s = ctx.Style;
        string[] head = ["SESSION / AGENT", "INPUT", "OUTPUT", "CACHE W", "CACHE R", "TOTAL", "RESP"];
        var t = new TextTable(pricing ? [.. head, "EST. COST"] : head);
        string[] Row(string name, TokenUsage u, (double, long)? cost, bool bold = false)
        {
            string[] cells = [name, Tok(u.Input), Tok(u.Output), Tok(u.CacheWrite), Tok(u.CacheRead), bold ? s.Bold(Tok(u.Total)) : Tok(u.Total), u.Responses.ToString("N0")];
            return pricing ? [.. cells, cost is { } c && u.Total > 0 ? Money(c.Item1, report!.Currency) + (c.Item2 > 0 ? "+" : "") : ""] : cells;
        }
        var all = new TokenUsage();
        double allCost = 0; long allUnpriced = 0;
        foreach (var (a, u) in sessions)
        {
            Add(all, u);
            var c = CostOf(a);
            if (c is { } cc) { allCost += cc.Cost; allUnpriced += cc.Unpriced; }
            t.Add(Row(s.Bold(Fmt.Trunc(a.Title ?? a.Id, 48)), u, c, bold: true));
            if (!args.Flag("agents")) continue;
            foreach (var ch in a.ChildIds.Select(agents.GetValueOrDefault).OfType<Agent>().OrderByDescending(ch => Rollup(ch, agents).Total))
                t.Add(Row("  └ " + Fmt.Trunc(ch.Title ?? ch.AgentType ?? ch.Id, 44), Rollup(ch, agents), CostOf(ch)));
        }
        t.Add(Row(s.Bold("ALL AGENTS"), all, pricing ? (allCost, allUnpriced) : null, bold: true));
        t.Write(ctx.Out, s);

        var byModel = new Dictionary<string, TokenUsage>();
        foreach (var a in agents.Values)
        foreach (var (m, u) in a.UsageByModel)
            Add(byModel.TryGetValue(m, out var acc) ? acc : byModel[m] = new TokenUsage(), u);
        if (byModel.Count > 0)
        {
            ctx.Out.WriteLine();
            string[] mhead = ["MODEL", "INPUT", "OUTPUT", "CACHE W", "CACHE R", "TOTAL", "RESP"];
            var mt = new TextTable(pricing ? [.. mhead, "EST. COST (ALL KEPT)"] : mhead);
            foreach (var (m, u) in byModel.OrderByDescending(kv => kv.Value.Total))
            {
                string[] cells = [m, Tok(u.Input), Tok(u.Output), Tok(u.CacheWrite), Tok(u.CacheRead), s.Bold(Tok(u.Total)), u.Responses.ToString("N0")];
                var row = report?.Models.FirstOrDefault(x => x.Model == m);
                mt.Add(pricing ? [.. cells, row is null ? "" : row.Priced ? Money(row.Totals.Cost ?? 0, report!.Currency) : "not priced"] : cells);
            }
            mt.Write(ctx.Out, s);
        }
        ctx.Out.WriteLine(s.Dim("Tokens as reported by the provider on each model response."));
        if (pricing) WriteCostNotes(ctx, report!, allUnpriced);
        else if (report is null) ctx.Out.WriteLine(s.Dim("Cost is not shown: this daemon has no usage report (update Overhive)."));
        return ExitCodes.Ok;
    }

    private static async Task<UsageReportResult?> TryReport(CliContext ctx, string range, string? bucket, CancellationToken ct)
    {
        try { return await Api.UsageReport(ctx, range, bucket, ct); }
        catch (ApiException e) when (e.Status == 404) { return null; } // older daemon
    }

    private static void WriteCostNotes(CliContext ctx, UsageReportResult r, long unpriced)
    {
        var s = ctx.Style;
        ctx.Out.WriteLine(s.Dim(string.Format(System.Globalization.CultureInfo.InvariantCulture, CostNote, r.PricesAsOf)));
        if (unpriced > 0 || r.UnpricedModels.Count > 0)
            ctx.Out.WriteLine(s.Dim($"'+': plus {Tok(Math.Max(unpriced, r.Totals.UnpricedTokens))} tokens of models without a price (not included): "
                + string.Join(", ", r.UnpricedModels) + ". Add prices in config \"Pricing\" (docs/USAGE.md)."));
        if (r.Totals.CacheWriteUnknownTtl > 0)
            ctx.Out.WriteLine(s.Dim($"Lower bound: {Tok(r.Totals.CacheWriteUnknownTtl)} cache-write tokens have no recorded cache lifetime (older usage) and are priced at the 5-minute rate."));
    }

    /// <summary>`usage --history`: one row per day (or hour) with tokens, responses, estimated cost and the top model.</summary>
    private static async Task<int> HistoryAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var by = args.Opt("by") ?? "day";
        if (by is not (UsageReport.Day or UsageReport.Hour)) throw new UsageException("--by: day or hour");
        var range = args.Opt("range") ?? (by == UsageReport.Hour ? "48h" : "30d");
        var r = await TryReport(ctx, range, by, ct) ?? throw new CliException("this daemon has no usage report (update Overhive)");
        if (args.Json)
        {
            Api.PrintJson(ctx, System.Text.Json.JsonSerializer.Serialize(r, Amc.Core.Events.Json.Options));
            return ExitCodes.Ok;
        }
        var s = ctx.Style;
        var pricing = r.Pricing;
        string[] head = [r.Bucket == UsageReport.Hour ? "HOUR" : "DAY", "TOKENS", "OUTPUT", "RESP", "TOP MODEL"];
        var t = new TextTable(pricing ? [head[0], "EST. COST", .. head[1..]] : head);
        var zone = UsageReport.Zone(r.TimeZone);
        foreach (var b in r.Buckets.Where(b => b.Totals.Responses > 0))
        {
            var local = TimeZoneInfo.ConvertTime(b.Start, zone);
            var label = r.Bucket == UsageReport.Hour ? local.ToString("ddd MMM d HH:00", System.Globalization.CultureInfo.InvariantCulture)
                : local.ToString("ddd yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var top = b.ByModel.OrderByDescending(kv => pricing ? kv.Value.Cost ?? 0 : kv.Value.Total).Select(kv => kv.Key).FirstOrDefault() ?? "";
            string[] cells = [label, Tok(b.Totals.Total), Tok(b.Totals.Output), b.Totals.Responses.ToString("N0"), top];
            t.Add(pricing ? [cells[0], s.Bold(Money(b.Totals.Cost ?? 0, r.Currency) + (b.Totals.UnpricedTokens > 0 ? "+" : "")), .. cells[1..]] : cells);
        }
        string[] totals = [s.Bold("TOTAL"), s.Bold(Tok(r.Totals.Total)), Tok(r.Totals.Output), r.Totals.Responses.ToString("N0"), ""];
        t.Add(pricing ? [totals[0], s.Bold(Money(r.Totals.Cost ?? 0, r.Currency) + (r.Totals.UnpricedTokens > 0 ? "+" : "")), .. totals[1..]] : totals);
        t.Write(ctx.Out, s);
        ctx.Out.WriteLine(s.Dim($"Per {r.Bucket} in {r.TimeZone}; days without usage are left out."));
        if (pricing) WriteCostNotes(ctx, r, r.Totals.UnpricedTokens);
        return ExitCodes.Ok;
    }

    /// <summary>Estimated cost of an agent and its sub-agents (any depth) from the report's per-agent totals.</summary>
    public static (double Cost, long Unpriced) CostRollup(Agent a, IReadOnlyDictionary<string, Agent> agents, UsageReportResult report)
    {
        double cost = 0; long unpriced = 0;
        var seen = new HashSet<string>();
        var stack = new Stack<Agent>([a]);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (!seen.Add(x.Id)) continue;
            if (report.Agents.TryGetValue(x.Id, out var t)) { cost += t.Cost ?? 0; unpriced += t.UnpricedTokens; }
            foreach (var id in x.ChildIds) if (agents.TryGetValue(id, out var c)) stack.Push(c);
        }
        return (cost, unpriced);
    }

    public static string Money(double v, string currency)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sym = currency == "USD" ? "$" : "";
        var suffix = sym.Length == 0 ? " " + currency : "";
        if (v > 0 && v < 0.01) return "<" + sym + "0.01" + suffix;
        return sym + v.ToString(v >= 1000 ? "N0" : "N2", inv) + suffix;
    }

    public static TokenUsage Rollup(Agent a, IReadOnlyDictionary<string, Agent> agents)
    {
        var acc = new TokenUsage();
        var seen = new HashSet<string>();
        var stack = new Stack<Agent>([a]);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (!seen.Add(x.Id)) continue;
            Add(acc, x.Usage);
            foreach (var id in x.ChildIds) if (agents.TryGetValue(id, out var c)) stack.Push(c);
        }
        return acc;
    }

    private static void Add(TokenUsage acc, TokenUsage u)
    {
        acc.Add(u.Input, u.Output, u.CacheWrite, u.CacheRead);
        acc.Responses += u.Responses;
    }

    public static string Tok(long n) => n switch
    {
        >= 1_000_000_000 => Trim(n / 1e9) + "B",
        >= 1_000_000 => Trim(n / 1e6) + "M",
        >= 1_000 => Trim(n / 1e3) + "k",
        _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Trim(double v)
    {
        var s = v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", System.Globalization.CultureInfo.InvariantCulture);
        return s.Contains('.') ? s.TrimEnd('0').TrimEnd('.') : s;
    }
}
