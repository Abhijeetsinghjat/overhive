using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Conflicts;
using Amc.Core.Domain;

namespace Amc.Cli.Commands;

/// <summary>`conflicts` lists conflicts with their compared line ranges; `conflicts warn &lt;n|key&gt;` warns the involved agents.</summary>
public static class ConflictsCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var conflicts = (await Api.Conflicts(ctx, ct)).OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        if (args.Positionals.Count == 0)
        {
            if (args.Json) { Api.PrintJson(ctx, (await ctx.Client.GetAsync("api/conflicts", ct)).Body); return ExitCodes.Ok; }
            Render(ctx, conflicts, await Api.Worktrees(ctx, ct));
            return ExitCodes.Ok;
        }
        if (args.Positionals[0] != "warn" || args.Positionals.Count != 2)
            throw new UsageException("usage: agentctl conflicts [warn <n|key>] [--yes]");
        return await WarnAsync(ctx, args, Resolve(conflicts, args.Positionals[1]), ct);
    }

    /// <summary>By 1-based number as listed, exact key, or unique key/path substring.</summary>
    public static Conflict Resolve(IReadOnlyList<Conflict> conflicts, string query)
    {
        if (int.TryParse(query, out var n) && n >= 1 && n <= conflicts.Count) return conflicts[n - 1];
        if (conflicts.FirstOrDefault(c => c.Key == query) is { } exact) return exact;
        var hits = conflicts.Where(c => c.Key.Contains(query, StringComparison.Ordinal)).ToList();
        return hits.Count switch
        {
            1 => hits[0],
            0 => throw new UsageException($"no conflict matches '{query}' (run 'agentctl conflicts')"),
            _ => throw new UsageException($"'{query}' matches {hits.Count} conflicts; use the number from 'agentctl conflicts'"),
        };
    }

    public static void Render(CliContext ctx, IReadOnlyList<Conflict> conflicts, IReadOnlyList<Worktree> worktrees)
    {
        var s = ctx.Style;
        var w = ctx.Out;
        if (conflicts.Count == 0) { w.WriteLine("No conflicts."); return; }
        string Branch(string id) => worktrees.FirstOrDefault(x => x.Id == id) is { } wt ? wt.Branch ?? (wt.Head is { Length: >= 7 } h ? "detached@" + h[..7] : "detached") : id;
        for (var i = 0; i < conflicts.Count; i++)
        {
            var c = conflicts[i];
            var o = c.Insight?.Overlap;
            var verdict = o is null ? "" : o.Verdict switch
            {
                OverlapVerdict.Overlapping => s.Red("overlapping lines") + " ",
                OverlapVerdict.Separate => s.Green("different regions") + " ",
                _ => s.Dim("lines not determined") + " ",
            };
            w.WriteLine($"{s.Bold((i + 1).ToString()),3}  {s.Yellow(Fmt.JsonName(c.Kind))}  {verdict}{c.Path ?? Fmt.Trunc(c.Description, 90)}");
            foreach (var p in o?.Pairs ?? [])
            {
                var line = p.Note is not null && !p.Overlapping ? $"{Branch(p.WorktreeA)} vs {Branch(p.WorktreeB)}: not compared ({p.Note})"
                    : p.Overlapping ? $"{Branch(p.WorktreeA)} {OverlapMath.FormatLines(p.OverlapA)} overlaps {Branch(p.WorktreeB)} {OverlapMath.FormatLines(p.OverlapB)}"
                    : $"{Branch(p.WorktreeA)} {OverlapMath.FormatLines(p.ChangedA)} · {Branch(p.WorktreeB)} {OverlapMath.FormatLines(p.ChangedB)}";
                var merge = p.CommittedConflict switch { true => s.Red("  [merge-tree: committed heads conflict]"), false => s.Dim("  [merge-tree: committed heads merge cleanly]"), _ => "" };
                w.WriteLine($"       {line}{merge}");
            }
            if (o is not null) w.WriteLine(s.Dim($"       evidence {Fmt.Evidence(o.Evidence)} · analysed {Fmt.Age(ctx.Now(), o.AnalyzedAt)} ago"));
            if (c.Insight?.Warnings is { Count: > 0 } ws && ws[^1] is { } last)
                w.WriteLine(s.Dim($"       warned {Fmt.Age(ctx.Now(), last.At)} ago{(last.Auto ? " (automatic)" : "")}: " +
                                  string.Join(", ", last.Recipients.Select(r => $"{AgentRef.Short(r.AgentId)} {r.Outcome}"))));
        }
    }

    private static async Task<int> WarnAsync(CliContext ctx, ParsedArgs args, Conflict c, CancellationToken ct)
    {
        var s = ctx.Style;
        var detail = await ctx.Client.GetNodeAsync("api/conflicts/detail" + AmcClient.Query(("key", c.Key)), ct);
        var warn = detail["warn"] as JsonObject;
        if (warn?["error"] is { } err) throw new CliException(err.ToString());
        var recipients = warn?["recipients"] as JsonArray ?? [];
        if (recipients.Count == 0) throw new CliException("no active agent is attributed to this file");
        if (!args.Flag("yes"))
        {
            if (!ctx.Interactive) throw new UsageException("warning agents sends them messages; refusing without confirmation in a non-interactive session (pass --yes)");
            foreach (var r in recipients.OfType<JsonObject>())
            {
                var can = r["canReceive"]?.GetValue<bool>() == true;
                ctx.Err.WriteLine($"{s.Bold(r["name"]?.ToString() ?? "?")} ({r["tool"]}, {r["branch"] ?? "detached"}) " +
                                  (can ? s.Dim("via " + r["transport"]) : s.Yellow("cannot receive messages")));
                ctx.Err.WriteLine("  " + r["text"]);
            }
            ctx.Err.Write($"Send these warnings for {c.Path}? [y/N] ");
            ctx.Err.Flush();
            if (ctx.In.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                ctx.Err.WriteLine("Aborted; nothing was sent.");
                return ExitCodes.Error;
            }
        }
        var res = await ctx.Client.SendAsync(HttpMethod.Post, "api/conflicts/warn", new { key = c.Key, confirm = true }, ct);
        var node = res.Node as JsonObject;
        if (args.Json) { Api.PrintJson(ctx, node ?? new JsonObject { ["httpStatus"] = res.Status, ["body"] = res.Body }); return res.Success ? ExitCodes.Ok : ExitCodes.Error; }
        if (res.Status == 401) throw AmcClient.ToException(res, "conflicts warn");
        foreach (var a in (node?["agents"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var outcome = a["outcome"]?.ToString();
            var mark = outcome is "delivered" or "queued" ? s.Green("✓") : s.Red("✗");
            ctx.Out.WriteLine($"{mark} {a["name"]}: {outcome}" + s.Dim(" — " + a["detail"]));
        }
        var summary = node?["detail"]?.ToString() ?? node?["error"]?.ToString() ?? res.ErrorText();
        (res.Success ? ctx.Out : ctx.Err).WriteLine(summary);
        return res.Success ? ExitCodes.Ok : ExitCodes.Error;
    }
}
