using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Events;

namespace Amc.Cli.Commands;

/// <summary>Event filter shared by the REST query and the client-side WebSocket filter (they must agree).</summary>
public sealed record EventFilter(string? AgentId = null, IReadOnlyList<string>? Types = null, string? File = null, string? WorktreeId = null)
{
    public bool Matches(AgentEvent e) =>
        (AgentId is null || e.AgentId == AgentId) &&
        (WorktreeId is null || e.WorktreeId == WorktreeId) &&
        (File is null || e.FilePath == File) &&
        (Types is null || Types.Count == 0 || Types.Any(t => t.EndsWith('*') ? e.Type.StartsWith(t.TrimEnd('*'), StringComparison.Ordinal) : e.Type == t));

    public string Query(int limit, long? before = null, DateTimeOffset? from = null)
    {
        var pairs = new List<(string, string?)> { ("agent", AgentId), ("worktree", WorktreeId), ("file", File) };
        foreach (var t in Types ?? []) pairs.Add(("type", t));
        if (from is { } f) pairs.Add(("from", f.ToUniversalTime().ToString("O")));
        if (before is { } b) pairs.Add(("before", b.ToString()));
        pairs.Add(("limit", limit.ToString()));
        pairs.Add(("order", "desc"));
        return "api/events" + AmcClient.Query(pairs.ToArray());
    }
}

public static class EventsCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        string? agentId = null, worktreeId = null;
        if (args.Opt("agent") is { } aq) agentId = (await Api.ResolveAgent(ctx, aq, ct)).Id;
        if (args.Opt("worktree") is { } wq) worktreeId = (await Api.ResolveWorktree(ctx, wq, ct)).Id;
        var types = args.Options.GetValueOrDefault("type")?.SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (types is { Count: > 1 } && types.Any(t => t.EndsWith('*')))
            throw new UsageException("a wildcard --type (e.g. tool.*) cannot be combined with other --type values");
        var filter = new EventFilter(agentId, types, args.Opt("file"), worktreeId);
        DateTimeOffset? from = args.Opt("since") is { } since ? Fmt.ParseSince(since, ctx.Now()) : null;
        var limit = args.IntOpt("limit") ?? (args.Flag("follow") ? 20 : 50);
        await ShowAndFollowAsync(ctx, args, filter, from, limit, EventFormatter.Line, ct);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Prints the latest <paramref name="limit"/> matching events (oldest first) and, with --follow,
    /// streams new ones over the WebSocket starting exactly at the head seen when the query ran
    /// (no duplicates, no gap).
    /// </summary>
    public static async Task ShowAndFollowAsync(CliContext ctx, ParsedArgs args, EventFilter filter, DateTimeOffset? from, int limit,
        Func<AgentEvent, CliContext, string> format, CancellationToken ct)
    {
        var follow = args.Flag("follow");
        long head = 0;
        if (follow) head = (await Api.Health(ctx, ct))["seq"]?.GetValue<long>() ?? 0;

        var raw = (await ctx.Client.GetAsync(filter.Query(limit, follow ? head + 1 : null, from), ct)).Body;
        var events = JsonSerializer.Deserialize<List<AgentEvent>>(raw, Json.Options) ?? [];
        events.Reverse(); // API returned newest first
        foreach (var e in events) Print(ctx, args.Json, e, format);
        if (!follow)
        {
            if (events.Count == 0 && !args.Json) ctx.Out.WriteLine(ctx.Style.Dim("No matching events."));
            return;
        }
        if (!args.Json) ctx.Err.WriteLine(ctx.Style.Dim($"— following from seq {head} (Ctrl+C to stop) —"));
        await FollowAsync(ctx, head, e => { if (filter.Matches(e)) Print(ctx, args.Json, e, format); }, ct);
    }

    private static void Print(CliContext ctx, bool json, AgentEvent e, Func<AgentEvent, CliContext, string> format)
    {
        if (json) ctx.Out.WriteLine(JsonSerializer.Serialize(e, Json.Options)); // NDJSON
        else ctx.Out.WriteLine(format(e, ctx));
        ctx.Out.Flush();
    }

    /// <summary>WebSocket /ws?since=&lt;seq&gt; tail with automatic reconnect from the last seen seq.</summary>
    public static async Task FollowAsync(CliContext ctx, long since, Action<AgentEvent> onEvent, CancellationToken ct)
    {
        var cursor = since;
        var connectedOnce = false;
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader(AmcClient.TokenHeader, ctx.Token);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            var uri = WebSocketUri(ctx.Settings.DaemonUrl, cursor);
            try
            {
                await ws.ConnectAsync(uri, ct);
                connectedOnce = true;
                using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buffer, ct);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close) break;

                    var outcome = HandleFrame(ms.ToArray(), ref cursor, onEvent);
                    if (outcome is { } note)
                    {
                        ctx.Err.WriteLine(ctx.Style.Yellow(note));
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is WebSocketException or IOException)
            {
                if (!connectedOnce) throw new DaemonUnreachableException($"cannot open event stream at {uri.GetLeftPart(UriPartial.Path)}: {ex.Message}");
            }
            if (ct.IsCancellationRequested) return;
            ctx.Err.WriteLine(ctx.Style.Dim($"— stream interrupted; reconnecting from seq {cursor} —"));
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Processes one server frame. Returns a note when the connection must be re-established (resync).</summary>
    public static string? HandleFrame(ReadOnlySpan<byte> frame, ref long cursor, Action<AgentEvent> onEvent)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(frame); }
        catch (JsonException) { return null; }
        switch (msg?["t"]?.ToString())
        {
            case "event":
                var e = msg["e"].Deserialize<AgentEvent>(Json.Options);
                if (e is null || e.Seq <= cursor) return null;
                cursor = e.Seq;
                onEvent(e);
                return null;
            case "hello" when cursor == 0:
                cursor = msg["seq"]?.GetValue<long>() ?? 0;
                return null;
            case "resync":
                var reason = msg["reason"]?.ToString() ?? "resync requested";
                if (reason.Contains("gap")) return $"— daemon cannot replay that far back ({reason}); some events were skipped —";
                return $"— {reason}; reconnecting —";
            default:
                return null;
        }
    }

    public static Uri WebSocketUri(Uri daemon, long since)
    {
        var b = new UriBuilder(daemon) { Scheme = daemon.Scheme == "https" ? "wss" : "ws", Path = "/ws", Query = $"since={since}" };
        return b.Uri;
    }
}

/// <summary>One dense line per event.</summary>
public static class EventFormatter
{
    public static string Line(AgentEvent e, CliContext ctx)
    {
        var s = ctx.Style;
        var type = e.Type switch
        {
            var t when t.EndsWith("failed") || t == EventTypes.GitConflict => s.Red(t),
            var t when t.StartsWith("file.") || t.StartsWith("git.") => s.Yellow(t),
            var t when t.StartsWith("agent.") => s.Cyan(t),
            var t when t.StartsWith("audit.") => s.Magenta(t),
            var t => t,
        };
        var agent = e.AgentId is { } a ? AgentRef.Short(a) : "-";
        return $"{s.Dim(e.Ts.ToLocalTime().ToString("HH:mm:ss"))} {s.Dim(e.Seq.ToString().PadLeft(7))} {Pad(type, e.Type.Length, 22)} {agent,-8} {s.Dim(Fmt.Evidence(e.Evidence))} {Summary(e)}";
    }

    private static string Pad(string styled, int visible, int width) => styled + new string(' ', Math.Max(0, width - visible));

    public static string Summary(AgentEvent e)
    {
        var d = e.Data;
        string? S(string k) => d[k] is JsonValue v ? v.ToString() : null;
        var text = e.Type switch
        {
            _ when e.Type.StartsWith("command.") => "$ " + S("command") + (S("exitCode") is { } x ? $"  → exit {x}" : "") + Dur(S("durationMs")),
            _ when e.Type.StartsWith("tool.") => $"{S("tool")} {S("summary") ?? S("filePath")}" + (S("error") is { } err ? $"  ✗ {err}" : "") + Dur(S("durationMs")),
            _ when e.Type.StartsWith("file.") => (e.FilePath ?? S("absolutePath") ?? "") + (S("added") is { } ad ? $" +{ad}/-{S("removed") ?? "0"}" : "") + (S("tool") is { } tl ? $" ({tl})" : ""),
            _ when e.Type.StartsWith("test.") => $"{S("runner")} passed={S("passed") ?? "?"} failed={S("failed") ?? "?"} skipped={S("skipped") ?? "?"}",
            EventTypes.GitFileStatus => $"{S("status")} {e.FilePath}",
            EventTypes.GitCommitCreated => $"{Short7(S("sha"))} {S("subject")}",
            _ when e.Type.StartsWith("git.") => $"{S("branch")} {Short7(S("head"))} {S("detail")}",
            _ when e.Type.StartsWith("agent.message") => $"[{S("transport")}] {S("text") ?? S("detail")}",
            EventTypes.AgentPrompt or EventTypes.AgentOutput or EventTypes.AgentNotification => S("text"),
            _ when e.Type.StartsWith("agent.") => $"{S("status") ?? S("title")} {S("reason")}",
            EventTypes.AuditControl => $"{S("action")} → {S("outcome")} {S("detail")}",
            _ when e.Type.StartsWith("process.") => $"pid {S("pid")} {S("command")}{(S("exitCode") is { } ec ? " exit " + ec : "")}",
            _ when e.Type.StartsWith("worktree.") || e.Type.StartsWith("repository.") => $"{S("root") ?? S("name")} {S("branch")}",
            _ => d.Count == 0 ? "" : d.ToJsonString(),
        };
        return Fmt.Trunc(text?.Trim(), 140);
    }

    private static string Short7(string? sha) => sha is null ? "" : sha[..Math.Min(7, sha.Length)];

    private static string Dur(string? ms) => long.TryParse(ms, out var v) ? v >= 1000 ? $" ({v / 1000.0:0.0}s)" : $" ({v}ms)" : "";
}
