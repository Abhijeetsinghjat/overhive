using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Domain;
using Amc.Core.Events;

namespace Amc.Cli.Commands;

public static class StatusCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var health = await Api.Health(ctx, ct);
        var agentsRaw = (await ctx.Client.GetAsync("api/agents", ct)).Body;
        var conflictsRaw = (await ctx.Client.GetAsync("api/conflicts", ct)).Body;
        var agents = JsonSerializer.Deserialize<List<Agent>>(agentsRaw, Json.Options) ?? [];
        var conflicts = JsonSerializer.Deserialize<List<Conflict>>(conflictsRaw, Json.Options) ?? [];
        var counts = agents.GroupBy(a => a.Status).OrderBy(g => g.Key).ToDictionary(g => Fmt.Status(g.Key), g => g.Count());

        if (args.Json)
        {
            var countsNode = new JsonObject();
            foreach (var (k, v) in counts) countsNode[k] = v;
            Api.PrintJson(ctx, new JsonObject
            {
                ["daemonUrl"] = ctx.Settings.DaemonUrl.ToString(),
                ["health"] = health.DeepClone(),
                ["agents"] = new JsonObject { ["total"] = agents.Count, ["byStatus"] = countsNode },
                ["conflicts"] = JsonNode.Parse(conflictsRaw),
            });
            return ExitCodes.Ok;
        }

        Render(ctx, health, agents, conflicts);
        return ExitCodes.Ok;
    }

    public static void Render(CliContext ctx, JsonNode health, IReadOnlyList<Agent> agents, IReadOnlyList<Conflict> conflicts)
    {
        var s = ctx.Style;
        var w = ctx.Out;
        var now = ctx.Now();
        var overall = health["overall"]?.ToString();
        w.WriteLine($"{s.Bold("amcd")}      {s.Health(overall, overall ?? "?")}  {ctx.Settings.DaemonUrl.ToString().TrimEnd('/')}" +
                    $"  seq {health["seq"]}" + (health["version"] is { } v ? $"  v{v}" : ""));
        var db = health["database"];
        var dbOk = db?["ok"]?.GetValue<bool>() == true;
        w.WriteLine($"{s.Bold("database")}  {(dbOk ? s.Green("ok") : s.Red("FAILED"))}  {db?["events"]} events");
        var dropped = health["queue"]?["dropped"]?.GetValue<long>() ?? 0;
        w.WriteLine($"{s.Bold("queue")}     depth {health["queue"]?["depth"]}  dropped {(dropped > 0 ? s.Red(dropped.ToString()) : "0")}");
        w.WriteLine($"{s.Bold("websocket")} {health["websocket"]?["clients"]} client(s)");

        if (health["components"] is JsonArray comps && comps.Count > 0)
        {
            w.WriteLine(s.Bold("components"));
            var t = new TextTable("NAME", "STATE", "LAST OK", "DETAIL");
            foreach (var c in comps.OfType<JsonObject>())
            {
                var state = c["state"]?.ToString();
                DateTimeOffset? last = c["lastSuccessAt"] is JsonValue lv && lv.TryGetValue<DateTimeOffset>(out var d) ? d : null;
                t.Add("  " + c["name"], s.Health(state, state ?? "?"), Fmt.Age(now, last), Fmt.Trunc(c["detail"]?.ToString(), 70));
            }
            t.Write(w, s, header: false);
        }

        var live = agents.Count(a => !a.IsTerminal);
        var parts = agents.GroupBy(a => a.Status).OrderBy(g => g.Key)
            .Select(g => s.Status(g.Key, $"{g.Count()} {Fmt.Status(g.Key)}"));
        w.WriteLine($"{s.Bold("agents")}    {agents.Count} total, {live} active" + (agents.Count > 0 ? "  — " + string.Join(" · ", parts) : ""));

        if (conflicts.Count == 0) w.WriteLine($"{s.Bold("conflicts")} none");
        else
        {
            w.WriteLine($"{s.Bold("conflicts")} {s.Red(conflicts.Count.ToString())}");
            foreach (var c in conflicts.OrderBy(c => c.Kind))
                w.WriteLine($"  {s.Yellow(Fmt.JsonName(c.Kind)),-14}  {Fmt.Trunc(c.Description, 90)}" +
                            (c.AgentIds.Count > 0 ? s.Dim("  [" + string.Join(", ", c.AgentIds.Select(AgentRef.Short)) + "]") : ""));
        }
    }
}
