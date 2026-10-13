using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;

namespace Amc.Cli.Commands;

/// <summary>
/// The open protocol from the shell, for any AI tool's hooks or a wrapper script:
///   agentctl emit tool_start --tool codex --agent "$SESSION" --cwd "$PWD" --data '{"id":"t1","name":"shell","command":"npm test"}'
///   echo '{"text":"done"}' | agentctl emit output --tool codex --agent "$SESSION"
///   agentctl inbox --tool codex --agent "$SESSION" --wait 30
/// </summary>
public static class EmitCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var kind = args.Positionals.Count > 0 ? args.Positionals[0] : throw new UsageException("kind required: start, status, prompt, output, tool_start, tool_end, file, usage, end");
        var data = ParseData(args.Opt("data") ?? (Console.IsInputRedirected ? await Console.In.ReadToEndAsync(ct) : null));
        foreach (var (flag, key) in new[] { ("status", "status"), ("text", "text"), ("path", "path"), ("change", "change"), ("reason", "reason") })
            if (args.Opt(flag) is { } v) data[key] = v;
        var agent = new JsonObject { ["id"] = Required(args, "agent") };
        foreach (var k in new[] { "parent", "title", "cwd", "model", "type" })
            if (args.Opt(k) is { } v) agent[k] = v;
        if (args.Opt("cwd") is null && kind == "start") agent["cwd"] = ctx.Cwd;
        if (args.Opt("pid") is { } p && int.TryParse(p, out var pid)) agent["pid"] = pid;
        var body = new JsonObject { ["tool"] = Required(args, "tool"), ["kind"] = kind, ["agent"] = agent, ["data"] = data };
        var r = await ctx.Client.SendAsync(HttpMethod.Post, "api/v1/report", body, ct);
        if (r.Success) return ExitCodes.Ok;
        ctx.Err.WriteLine($"agentctl emit: {r.Status} {r.Body}");
        return ExitCodes.Error;
    }

    private static JsonObject ParseData(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try { return JsonNode.Parse(raw) as JsonObject ?? throw new UsageException("--data / stdin must be a JSON object"); }
        catch (System.Text.Json.JsonException e) { throw new UsageException("--data / stdin is not valid JSON: " + e.Message); }
    }

    internal static string Required(ParsedArgs args, string name) => args.Opt(name) ?? throw new UsageException($"--{name} is required");
}

public static class InboxCommand
{
    /// <summary>Prints queued messages for the agent (one per line, or JSON with --json); exit 0 with no output if none.</summary>
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var wait = int.TryParse(args.Opt("wait"), out var w) ? Math.Clamp(w, 0, 50) : 0;
        var body = new JsonObject { ["tool"] = EmitCommand.Required(args, "tool"), ["agent"] = EmitCommand.Required(args, "agent"), ["wait"] = wait };
        var r = await ctx.Client.SendAsync(HttpMethod.Post, "api/v1/inbox", body, ct);
        if (r.Status == 204) return ExitCodes.Ok;
        if (!r.Success) { ctx.Err.WriteLine($"agentctl inbox: {r.Status} {r.Body}"); return ExitCodes.Error; }
        if (args.Json) { ctx.Out.WriteLine(r.Body); return ExitCodes.Ok; }
        foreach (var m in JsonNode.Parse(r.Body)?["messages"]?.AsArray() ?? [])
            ctx.Out.WriteLine((string?)m?["text"]);
        return ExitCodes.Ok;
    }
}
