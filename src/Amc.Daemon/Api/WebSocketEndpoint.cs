using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Core.Events;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Storage;

namespace Amc.Daemon.Api;

/// <summary>
/// WebSocket protocol (see ARCHITECTURE.md §11):
///   → hello {seq}; replay of events with seq &gt; since (paged); then live event/patch/live messages.
///   → resync when the client fell behind (client refetches /api/state and reconnects with since=state.seq).
/// No event is lost across reconnects because the client always resumes from its last seq.
/// </summary>
public static class WebSocketEndpoint
{
    private const int ReplayPage = 1000;
    private const int MaxReplay = 20_000;

    public static async Task Handle(HttpContext ctx, Broadcaster broadcaster, EventStore store, StateStore state, ILoggerFactory lf)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var log = lf.CreateLogger("ws");
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var client = broadcaster.Connect(); // connect before replay so nothing published during replay is missed
        var ct = ctx.RequestAborted;
        try
        {
            long.TryParse(ctx.Request.Query["since"], out var since);
            var head = state.Read(s => s.Seq);
            await Send(ws, new JsonObject { ["t"] = "hello", ["seq"] = head, ["serverTime"] = DateTimeOffset.UtcNow }.ToJsonString(), ct);

            if (since > 0 && head - since > MaxReplay)
                await Send(ws, """{"t":"resync","reason":"gap too large"}""", ct);
            else if (since > 0)
            {
                var cursor = since;
                while (true)
                {
                    var page = store.Query(new EventQuery(After: cursor, Limit: ReplayPage));
                    foreach (var e in page)
                        await Send(ws, new JsonObject { ["t"] = "event", ["replay"] = true, ["e"] = Json.ToObject(e) }.ToJsonString(Json.Options), ct);
                    if (page.Count < ReplayPage) break;
                    cursor = page[^1].Seq;
                }
            }

            var receive = ReceiveLoop(ws, ct);
            var resynced = false;
            while (!resynced && !ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var readTask = client.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(readTask, receive) == receive) break;
                if (!await readTask) break;
                while (client.TryRead(out var msg))
                {
                    await Send(ws, msg, ct);
                    if (ReferenceEquals(msg, ClientConnection.ResyncMessage)) { resynced = true; break; }
                }
            }
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            log.LogDebug("WebSocket closed: {Message}", ex.Message);
        }
        finally { broadcaster.Disconnect(client); }
    }

    private static async Task ReceiveLoop(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[4096];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return;
            // Client → server messages are only pings today; control actions go through REST so they are audited uniformly.
        }
    }

    /// <summary>A send that makes no progress for this long (peer asleep, network gone) aborts the socket.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private static async Task Send(WebSocket ws, string json, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
    }
}
