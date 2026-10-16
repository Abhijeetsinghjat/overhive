using System.Text.Json;
using Amc.Cli.Infrastructure;
using Amc.Core.Attention;
using Amc.Core.Events;

namespace Amc.Cli.Commands;

/// <summary>`overhive attention`: the "needs you" queue across all tools (same data as the dashboard inbox).</summary>
public static class AttentionCommand
{
    public sealed record Snapshot(List<AttentionItem> Items, int Urgent, int Hidden, List<AttentionItem>? HiddenItems = null);

    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var path = "api/attention" + (args.Flag("all") ? "?all=true" : "");
        var body = (await ctx.Client.GetAsync(path, ct)).Body;
        if (args.Json)
        {
            Api.PrintJson(ctx, body);
            return ExitCodes.Ok;
        }
        var snap = JsonSerializer.Deserialize<Snapshot>(body, Json.Options) ?? new Snapshot([], 0, 0);
        var table = Build(snap.Items.Concat(snap.HiddenItems ?? []).ToList(), ctx.Now(), ctx.Style);
        if (table.Count == 0) ctx.Out.WriteLine("Nothing needs you right now.");
        else table.Write(ctx.Out, ctx.Style);
        if (snap.Hidden > 0 && snap.HiddenItems is null)
            ctx.Out.WriteLine(ctx.Style.Dim($"({snap.Hidden} snoozed or dismissed; --all to show)"));
        return ExitCodes.Ok;
    }

    /// <summary>Pure rendering (unit-tested).</summary>
    public static TextTable Build(IReadOnlyList<AttentionItem> items, DateTimeOffset now, Style style)
    {
        var table = new TextTable("PRIORITY", "ITEM", "TOOL", "EV", "AGE", "AGENT");
        foreach (var i in items)
        {
            var prio = i.Priority switch
            {
                AttentionPriority.Urgent => style.Yellow("urgent"),
                AttentionPriority.Normal => style.Cyan("normal"),
                _ => style.Dim("low"),
            };
            if (i.Dismissed == true) prio += style.Dim(" (dismissed)");
            else if (i.SnoozedUntil is not null) prio += style.Dim(" (snoozed)");
            table.Add(prio, Fmt.Trunc(i.Title, 60), i.Tool ?? "", style.Dim(Fmt.Evidence(i.Evidence)), Fmt.Age(now, i.Since),
                i.AgentIds.Count > 0 ? AgentRef.Short(i.AgentIds[0]) : "");
        }
        return table;
    }
}
