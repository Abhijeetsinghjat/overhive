using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Approvals;
using Amc.Core.Events;
using Amc.Core.Risk;

namespace Amc.Cli.Commands;

/// <summary>
/// `overhive approvals`: Claude Code tool calls waiting for a decision (GET /api/approvals);
/// `approvals allow|deny &lt;id&gt; [--reason TEXT] [--similar]` decides one (an id prefix is enough).
/// </summary>
public static class ApprovalsCommand
{
    public sealed record Listing(string Mode, int TimeoutSeconds, string? MinSeverity, List<PendingApproval> Items);

    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var sub = args.Positionals.Count > 0 ? args.Positionals[0] : "list";
        switch (sub)
        {
            case "list":
            {
                var body = (await ctx.Client.GetAsync("api/approvals", ct)).Body;
                if (args.Json) { Api.PrintJson(ctx, body); return ExitCodes.Ok; }
                var listing = JsonSerializer.Deserialize<Listing>(body, Json.Options) ?? new Listing(ApprovalModes.Off, 0, null, []);
                if (listing.Items.Count == 0)
                    ctx.Out.WriteLine(listing.Mode == ApprovalModes.Off
                        ? "Nothing is waiting. Approvals are off (config \"Approvals\": {\"Mode\": \"risky\"} to turn them on, then `agentctl hooks install`)."
                        : $"Nothing is waiting for a decision (mode {listing.Mode}).");
                else Build(listing.Items, ctx.Now(), ctx.Style).Write(ctx.Out, ctx.Style);
                return ExitCodes.Ok;
            }
            case "allow":
            case "deny":
            {
                if (args.Positionals.Count != 2) throw new UsageException($"usage: agentctl approvals {sub} <id> [--reason TEXT]{(sub == "allow" ? " [--similar]" : "")}");
                if (sub == "deny" && args.Flag("similar")) throw new UsageException("--similar only applies to allow");
                var id = await ResolveId(ctx, args.Positionals[1], ct);
                var r = await ctx.Client.SendAsync(HttpMethod.Post, $"api/approvals/{AmcClient.Seg(id)}/{sub}",
                    new { reason = args.Opt("reason"), allowSimilar = args.Flag("similar"), client = "cli" }, ct);
                if (r.Status == 401) throw AmcClient.ToException(r, "approvals");
                var node = r.Node as JsonObject;
                if (args.Json) { Api.PrintJson(ctx, node ?? new JsonObject { ["httpStatus"] = r.Status, ["body"] = r.Body }); return r.Success ? ExitCodes.Ok : ExitCodes.Error; }
                var message = node?["message"]?.ToString() ?? node?["detail"]?.ToString() ?? r.ErrorText();
                if (r.Success) ctx.Out.WriteLine($"{ctx.Style.Green("✓")} {message} ({Short(id)})");
                else ctx.Err.WriteLine($"{ctx.Style.Red("✗")} {sub} {Short(id)} failed ({r.Status}): {message}");
                return r.Success ? ExitCodes.Ok : ExitCodes.Error;
            }
            default:
                throw new UsageException($"unknown approvals subcommand '{sub}' (list | allow | deny)");
        }
    }

    public static string Short(string id) => id.Length > 8 ? id[..8] : id;

    /// <summary>A full id, or a unique prefix of a pending one.</summary>
    private static async Task<string> ResolveId(CliContext ctx, string text, CancellationToken ct)
    {
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 32) return text;
        if (text.Length < 4) throw new UsageException("approval id too short (give at least 4 characters)");
        var listing = await ctx.Client.GetAsync<Listing>("api/approvals", ct);
        var matches = listing.Items.Where(i => i.Id.StartsWith(text, StringComparison.Ordinal)).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw new CliException($"no pending approval starts with '{text}' (already decided or expired?)"),
            _ => throw new UsageException($"'{text}' matches {matches.Count} pending approvals; give more characters"),
        };
    }

    /// <summary>Pure rendering (unit-tested).</summary>
    public static TextTable Build(IEnumerable<PendingApproval> items, DateTimeOffset now, Style s)
    {
        var t = new TextTable("ID", "AGE", "AGENT", "TOOL", "RISK", "WHAT", "WHY");
        foreach (var p in items)
            t.Add(s.Bold(Short(p.Id)), Fmt.Age(now, p.RequestedAt), p.AgentId is null ? "-" : AgentRef.Short(p.AgentId), p.Tool,
                p.Severity switch { RiskSeverity.Critical => s.Red(s.Bold(p.Severity)), RiskSeverity.Warn => s.Yellow(p.Severity), null => s.Dim("edit"), _ => p.Severity },
                Fmt.Trunc(Fmt.OneLine(p.Command ?? p.FilePath ?? ""), 60),
                Fmt.Trunc(p.Findings.FirstOrDefault()?.Explanation ?? "edit (approvals for all edits are on)", 60));
        return t;
    }
}
