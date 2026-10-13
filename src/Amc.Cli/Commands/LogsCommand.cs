using System.Text;
using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Events;

namespace Amc.Cli.Commands;

/// <summary>Command / tool activity of one agent, including bounded stdout/stderr.</summary>
public static class LogsCommand
{
    public static readonly string[] Types =
    [
        EventTypes.CommandStarted, EventTypes.CommandCompleted, EventTypes.CommandFailed,
        EventTypes.ToolStarted, EventTypes.ToolCompleted, EventTypes.ToolFailed,
        EventTypes.TestPassed, EventTypes.TestFailed,
    ];

    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var agent = await Api.ResolveAgent(ctx, args.Positionals[0], ct);
        var maxLines = args.IntOpt("lines") ?? 15;
        var filter = new EventFilter(agent.Id, Types);
        DateTimeOffset? from = args.Opt("since") is { } since ? Fmt.ParseSince(since, ctx.Now()) : null;
        await EventsCommand.ShowAndFollowAsync(ctx, args, filter, from, args.IntOpt("limit") ?? 40, (e, c) => Format(e, c, maxLines), ct);
        return ExitCodes.Ok;
    }

    public static string Format(AgentEvent e, CliContext ctx, int maxLines)
    {
        var s = ctx.Style;
        var d = e.Data;
        string? S(string k) => d[k] is JsonValue v ? v.ToString() : null;
        var time = s.Dim(e.Ts.ToLocalTime().ToString("HH:mm:ss"));
        var sb = new StringBuilder();
        switch (e.Type)
        {
            case EventTypes.CommandStarted:
                sb.Append($"{time} {s.Dim("▶")} {s.Bold("$ " + S("command"))}");
                if (S("description") is { } desc) sb.Append(s.Dim("  # " + Fmt.Trunc(desc, 80)));
                break;
            case EventTypes.CommandCompleted or EventTypes.CommandFailed:
                var exit = S("exitCode");
                var ok = e.Type == EventTypes.CommandCompleted && exit is null or "0";
                sb.Append($"{time} {s.Ok(ok)} {s.Bold("$ " + S("command"))}  {(exit is null ? "" : ok ? s.Dim("exit " + exit) : s.Red("exit " + exit))}{Dur(S("durationMs"), s)}");
                if (d["interrupted"]?.GetValue<bool>() == true) sb.Append(s.Yellow("  (interrupted)"));
                Block(sb, S("stdout"), maxLines, s, "│", null);
                Block(sb, S("stderr"), maxLines, s, "┃", "31");
                break;
            case EventTypes.ToolStarted:
                sb.Append($"{time} {s.Dim("▶")} {s.Cyan(S("tool") ?? "?")} {Fmt.Trunc(S("summary") ?? S("filePath"), 110)}");
                break;
            case EventTypes.ToolCompleted or EventTypes.ToolFailed:
                var failed = e.Type == EventTypes.ToolFailed;
                sb.Append($"{time} {s.Ok(!failed)} {s.Cyan(S("tool") ?? "?")} {Fmt.Trunc(S("summary") ?? S("filePath"), 110)}{Dur(S("durationMs"), s)}");
                if (S("error") is { } err) Block(sb, err, maxLines, s, "┃", "31");
                else Block(sb, S("output"), Math.Min(maxLines, 5), s, "│", null);
                break;
            default:
                sb.Append($"{time} {s.Ok(e.Type != EventTypes.TestFailed)} {e.Type} {EventFormatter.Summary(e)}");
                break;
        }
        return sb.ToString();
    }

    private static void Block(StringBuilder sb, string? text, int maxLines, Style s, string gutter, string? color)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var lines = text.TrimEnd().Split('\n');
        var shown = lines.Length > maxLines ? lines[^maxLines..] : lines;
        if (lines.Length > maxLines) sb.Append('\n').Append(s.Dim($"    {gutter} … {lines.Length - maxLines} earlier line(s)"));
        foreach (var l in shown)
        {
            var line = l.TrimEnd('\r');
            if (line.Length > 400) line = line[..399] + "…";
            sb.Append('\n').Append(s.Dim("    " + gutter + " ")).Append(color is null ? line : s.Wrap(color, line));
        }
    }

    private static string Dur(string? ms, Style s) =>
        long.TryParse(ms, out var v) ? s.Dim(v >= 1000 ? $" ({v / 1000.0:0.0}s)" : $" ({v}ms)") : "";
}
