using Amc.Cli.Infrastructure;
using Amc.Core.Domain;

namespace Amc.Cli.Commands;

/// <summary>Token usage per session (including sub-agents), optionally per agent, plus totals per model.</summary>
public static class UsageCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var agents = (await Api.Agents(ctx, ct)).ToDictionary(a => a.Id);
        var sessions = agents.Values.Where(a => a.ParentId is null || !agents.ContainsKey(a.ParentId))
            .Select(a => (Agent: a, Usage: Rollup(a, agents))).OrderByDescending(x => x.Usage.Total).ToList();
        if (args.Json)
        {
            Api.PrintJson(ctx, System.Text.Json.JsonSerializer.Serialize(sessions.Select(x => new
            {
                id = x.Agent.Id, title = x.Agent.Title, own = x.Agent.Usage, withSubAgents = x.Usage,
            }), Amc.Core.Events.Json.Options));
            return ExitCodes.Ok;
        }
        var s = ctx.Style;
        var t = new TextTable("SESSION / AGENT", "INPUT", "OUTPUT", "CACHE W", "CACHE R", "TOTAL", "RESP");
        var all = new TokenUsage();
        foreach (var (a, u) in sessions)
        {
            Add(all, u);
            t.Add(s.Bold(Fmt.Trunc(a.Title ?? a.Id, 48)), Tok(u.Input), Tok(u.Output), Tok(u.CacheWrite), Tok(u.CacheRead), s.Bold(Tok(u.Total)), u.Responses.ToString("N0"));
            if (!args.Flag("agents")) continue;
            foreach (var c in a.ChildIds.Select(agents.GetValueOrDefault).OfType<Agent>().OrderByDescending(c => Rollup(c, agents).Total))
            {
                var cu = Rollup(c, agents);
                t.Add("  └ " + Fmt.Trunc(c.Title ?? c.AgentType ?? c.Id, 44), Tok(cu.Input), Tok(cu.Output), Tok(cu.CacheWrite), Tok(cu.CacheRead), Tok(cu.Total), cu.Responses.ToString("N0"));
            }
        }
        t.Add(s.Bold("ALL AGENTS"), Tok(all.Input), Tok(all.Output), Tok(all.CacheWrite), Tok(all.CacheRead), s.Bold(Tok(all.Total)), all.Responses.ToString("N0"));
        t.Write(ctx.Out, s);

        var byModel = new Dictionary<string, TokenUsage>();
        foreach (var a in agents.Values)
        foreach (var (m, u) in a.UsageByModel)
            Add(byModel.TryGetValue(m, out var acc) ? acc : byModel[m] = new TokenUsage(), u);
        if (byModel.Count > 0)
        {
            ctx.Out.WriteLine();
            var mt = new TextTable("MODEL", "INPUT", "OUTPUT", "CACHE W", "CACHE R", "TOTAL", "RESP");
            foreach (var (m, u) in byModel.OrderByDescending(kv => kv.Value.Total))
                mt.Add(m, Tok(u.Input), Tok(u.Output), Tok(u.CacheWrite), Tok(u.CacheRead), s.Bold(Tok(u.Total)), u.Responses.ToString("N0"));
            mt.Write(ctx.Out, s);
        }
        ctx.Out.WriteLine(s.Dim("Tokens as reported by the provider on each model response. Cost is not estimated."));
        return ExitCodes.Ok;
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
