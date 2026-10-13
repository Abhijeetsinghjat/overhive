using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Providers;

namespace Amc.Cli.Commands;

public static class DiffCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var wq = args.Opt("worktree");
        if ((wq is null) == (args.Positionals.Count == 0))
            throw new UsageException("give either an agent or --worktree. Usage: agentctl diff <agent> | diff --worktree <wt> [--path P]");
        string path;
        if (wq is not null)
        {
            var wt = await Api.ResolveWorktree(ctx, wq, ct);
            path = $"api/worktrees/{AmcClient.Seg(wt.Id)}/diff" + AmcClient.Query(("path", args.Opt("path")));
        }
        else
        {
            if (args.Opt("path") is not null) throw new UsageException("--path is only supported with --worktree");
            var agent = await Api.ResolveAgent(ctx, args.Positionals[0], ct);
            path = $"api/agents/{AmcClient.Seg(agent.Id)}/diff";
        }

        var r = await ctx.Client.GetAsync(path, ct);
        if (args.Json)
        {
            Api.PrintJson(ctx, r.Body);
            return ExitCodes.Ok;
        }
        var text = ExtractDiff(r);
        if (string.IsNullOrWhiteSpace(text)) ctx.Out.WriteLine("No differences.");
        else ctx.Out.Write(Colorize(text, ctx.Style));
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The diff endpoints are built in parallel; accept plain text, {diff|patch: "..."}, or
    /// {files:[{path, diff|patch}]}/[...] so the CLI works with any of those shapes.
    /// </summary>
    public static string ExtractDiff(ApiResponse r)
    {
        var node = r.IsJson || r.Body.TrimStart().StartsWith('{') || r.Body.TrimStart().StartsWith('[') ? r.Node : null;
        if (node is null) return r.Body;
        return FromNode(node);

        static string FromNode(JsonNode? n) => n switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonObject o when Str(o, "diff", "patch", "unified", "text") is { } s => s,
            JsonObject o when o["files"] is JsonArray files => FromNode(files),
            JsonArray a => string.Concat(a.Select(x => Ensure(FromNode(x)))),
            JsonObject o => o.ToJsonString(Api.Pretty) + "\n",
            _ => "",
        };
        static string? Str(JsonObject o, params string[] keys) =>
            keys.Select(k => o[k]).OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>()).FirstOrDefault();
        static string Ensure(string s) => s.Length == 0 || s.EndsWith('\n') ? s : s + "\n";
    }

    public static string Colorize(string diff, Style s)
    {
        if (!s.Enabled) return diff.EndsWith('\n') ? diff : diff + "\n";
        var sb = new StringBuilder();
        foreach (var line in diff.TrimEnd('\n').Split('\n'))
        {
            sb.AppendLine(line switch
            {
                _ when line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("diff ") || line.StartsWith("index ") => s.Bold(line),
                _ when line.StartsWith("@@") => s.Cyan(line),
                _ when line.StartsWith('+') => s.Green(line),
                _ when line.StartsWith('-') => s.Red(line),
                _ => line,
            });
        }
        return sb.ToString();
    }
}

public static class MessageCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var agent = await Api.ResolveAgent(ctx, args.Positionals[0], ct);
        var text = string.Join(' ', args.Positionals.Skip(1));
        if (text == "-") text = await ctx.In.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) throw new UsageException("message text is empty");

        var r = await ctx.Client.SendAsync(HttpMethod.Post, $"api/agents/{AmcClient.Seg(agent.Id)}/messages", new { text }, ct);
        var node = r.Node as JsonObject;
        var ok = r.Success && node?["ok"]?.GetValue<bool>() != false;
        var messageId = node?["messageId"]?.ToString();
        var transport = node?["transport"]?.ToString();
        var detail = node?["detail"]?.ToString() ?? node?["error"]?.ToString();

        // Delivered vs queued: the response may carry it; otherwise ask the message store (state is updated asynchronously).
        string status = !ok ? "failed" : node?["delivered"]?.GetValue<bool>() == true ? "delivered" : "queued";
        if (ok && status == "queued" && messageId is { Length: > 0 })
            status = await LookupStatusAsync(ctx, agent.Id, messageId, ct) ?? status;

        if (args.Json)
        {
            var outNode = (node?.DeepClone() as JsonObject) ?? new JsonObject { ["body"] = r.Body };
            outNode["httpStatus"] = r.Status;
            outNode["status"] = status;
            Api.PrintJson(ctx, outNode);
            return ok ? ExitCodes.Ok : ExitCodes.Error;
        }

        var s = ctx.Style;
        if (!ok)
        {
            if (r.Status == 401) throw AmcClient.ToException(r, "messages");
            ctx.Err.WriteLine($"{s.Red("✗")} message to {AgentRef.Label(agent)} not accepted: {detail ?? r.ErrorText()}");
            return ExitCodes.Error;
        }
        var statusText = status switch
        {
            "delivered" => s.Green("delivered"),
            "answered" => s.Green("answered"),
            "failed" => s.Red("failed"),
            _ => s.Yellow("queued"),
        };
        ctx.Out.WriteLine($"{statusText} via {transport ?? "?"} to {AgentRef.Label(agent)}" + (messageId is null ? "" : s.Dim($"  (message {messageId})")));
        if (detail is { Length: > 0 }) ctx.Out.WriteLine(s.Dim("  " + detail));
        if (status == "queued" && transport == Transports.HookMailbox)
            ctx.Out.WriteLine(s.Dim("  A busy agent receives it at its next tool call; an idle agent is woken immediately (asyncRewake hook)."));
        return ExitCodes.Ok;
    }

    private static async Task<string?> LookupStatusAsync(CliContext ctx, string agentId, string messageId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var r = await ctx.Client.SendAsync(HttpMethod.Get, "api/messages" + AmcClient.Query(("agent", agentId)), null, ct);
                if (!r.Success) return null;
                var m = JsonSerializer.Deserialize<List<Message>>(r.Body, Json.Options)?.FirstOrDefault(x => x.Id == messageId);
                if (m is not null && m.Status != MessageStatus.Queued) return Fmt.JsonName(m.Status);
                if (m is not null && attempt == 2) return "queued";
            }
            catch (Exception ex) when (ex is JsonException or CliException) { return null; }
            await Task.Delay(150, ct);
        }
        return null;
    }
}

public static class ControlCommand
{
    /// <summary>CLI verb → provider action. "stop" is a graceful terminate (SIGTERM).</summary>
    public static readonly IReadOnlyDictionary<string, ControlAction> Verbs = new Dictionary<string, ControlAction>
    {
        ["pause"] = ControlAction.Pause,
        ["resume"] = ControlAction.Resume,
        ["interrupt"] = ControlAction.Interrupt,
        ["stop"] = ControlAction.Terminate,
        ["kill"] = ControlAction.Kill,
    };

    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var action = Verbs[args.Command];
        var agent = await Api.ResolveAgent(ctx, args.Positionals[0], ct);
        var s = ctx.Style;
        var confirm = false;
        if (DangerousActions.RequiresConfirmation(action))
        {
            if (!args.Flag("yes"))
            {
                if (!ctx.Interactive)
                    throw new UsageException($"{args.Command} is destructive; refusing without confirmation in a non-interactive session (pass --yes)");
                var shortId = AgentRef.Short(agent.Id);
                ctx.Err.WriteLine($"{s.Red(action.ToString().ToUpperInvariant())} {AgentRef.Label(agent)}  [{Fmt.Status(agent.Status)}{(agent.Pid is { } pid ? $", pid {pid}" : "")}]");
                ctx.Err.Write($"Type the agent id {s.Bold(shortId)} to confirm: ");
                ctx.Err.Flush();
                var typed = ctx.In.ReadLine()?.Trim();
                if (typed != shortId)
                {
                    ctx.Err.WriteLine("Aborted; nothing was sent.");
                    return ExitCodes.Error;
                }
            }
            confirm = true;
        }

        var r = await ctx.Client.SendAsync(HttpMethod.Post, $"api/agents/{AmcClient.Seg(agent.Id)}/control",
            new { action = action.ToString(), confirm }, ct);
        var node = r.Node as JsonObject;
        var ok = r.Success && node?["ok"]?.GetValue<bool>() != false;
        var detail = node?["detail"]?.ToString() ?? node?["error"]?.ToString() ?? r.ErrorText();
        if (args.Json)
        {
            Api.PrintJson(ctx, node ?? new JsonObject { ["httpStatus"] = r.Status, ["body"] = r.Body });
            return ok ? ExitCodes.Ok : ExitCodes.Error;
        }
        if (r.Status == 401) throw AmcClient.ToException(r, "control");
        if (ok) ctx.Out.WriteLine($"{s.Green("✓")} {args.Command} {AgentRef.Label(agent)}" + (detail is { Length: > 0 } ? s.Dim(" — " + detail) : ""));
        else ctx.Err.WriteLine($"{s.Red("✗")} {args.Command} {AgentRef.Label(agent)} failed ({r.Status}): {detail}");
        return ok ? ExitCodes.Ok : ExitCodes.Error;
    }
}
