using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Core.Events;
using Amc.Daemon.Terminals;
using Microsoft.AspNetCore.TestHost;

namespace Amc.Tests.Terminals;

public class RingBufferTests
{
    [Fact]
    public void KeepsMostRecentBytes()
    {
        var r = new RingBuffer(8);
        r.Append("abc"u8);
        Assert.Equal("abc", Encoding.ASCII.GetString(r.ToArray()));
        r.Append("defgh"u8);
        Assert.Equal("abcdefgh", Encoding.ASCII.GetString(r.ToArray()));
        r.Append("ij"u8);
        Assert.Equal("cdefghij", Encoding.ASCII.GetString(r.ToArray()));
        Assert.Equal(8, r.Count);
        r.Append("0123456789XYZ"u8); // larger than capacity
        Assert.Equal("56789XYZ", Encoding.ASCII.GetString(r.ToArray()));
        Assert.Equal(23, r.TotalWritten);
    }

    [Fact]
    public void ManyRandomAppends_NeverExceedCapacity()
    {
        var r = new RingBuffer(RingBuffer.DefaultCapacity);
        var reference = new List<byte>();
        var rnd = new Random(1);
        for (var i = 0; i < 300; i++)
        {
            var chunk = new byte[rnd.Next(1, 40_000)];
            rnd.NextBytes(chunk);
            r.Append(chunk);
            reference.AddRange(chunk);
        }
        Assert.Equal(RingBuffer.DefaultCapacity, r.Count);
        Assert.Equal(reference.Skip(reference.Count - RingBuffer.DefaultCapacity).ToArray(), r.ToArray());
    }

    [Fact]
    public void Log_RotatesAtCap_AndTailReadsAcrossFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amc-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new TerminalLog(dir, "T1", maxBytes: 10))
            {
                log.Append("01234567"u8);
                log.Append("abcdef"u8); // would exceed 10 → rotate
            }
            Assert.Equal("01234567", File.ReadAllText(Path.Combine(dir, "T1.log.1")));
            Assert.Equal("abcdef", File.ReadAllText(Path.Combine(dir, "T1.log")));
            Assert.Equal("4567abcdef", Encoding.ASCII.GetString(TerminalLog.ReadTail(dir, "T1", 10)));
            Assert.Empty(TerminalLog.ReadTail(dir, "nope", 10));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class TerminalHubTests
{
    private static async Task SendJson(WebSocket ws, JsonObject o) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<(byte[] Data, WebSocketMessageType Type)> Receive(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buf = new byte[1 << 20];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return (ms.ToArray(), r.MessageType);
        }
    }

    [Fact]
    public async Task Producer_Viewer_Routing()
    {
        await using var host = await TerminalHost.StartAsync();
        var client = host.App.GetTestServer().CreateWebSocketClient();
        var producer = await client.ConnectAsync(new Uri("ws://localhost/pty/attach?id=T1"), CancellationToken.None);
        await SendJson(producer, new JsonObject { ["id"] = "T1", ["command"] = "claude", ["cwd"] = "/w", ["pid"] = 999_999, ["cols"] = 100, ["rows"] = 30 });
        await producer.SendAsync("hello "u8.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);

        var started = await host.Sink.WaitFor(EventTypes.TerminalStarted);
        Assert.Equal("term:T1:start", started.DedupeKey);
        Assert.Equal("claude", started.Payload<TerminalData>()!.Command);
        await WaitUntil(() => host.Hub.Buffer("T1").Length == 6);
        Assert.True(host.Hub.IsAttached("T1"));

        // Viewer gets the ring buffer first, then live output.
        var viewer = await client.ConnectAsync(new Uri("ws://localhost/pty/view?id=T1"), CancellationToken.None);
        var first = await Receive(viewer);
        Assert.Equal(WebSocketMessageType.Binary, first.Type);
        Assert.Equal("hello ", Encoding.UTF8.GetString(first.Data));
        await producer.SendAsync("world"u8.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("world", Encoding.UTF8.GetString((await Receive(viewer)).Data));

        // Viewer input → producer.
        await SendJson(viewer, new JsonObject { ["t"] = "input", ["data"] = Convert.ToBase64String("ls\r"u8) });
        var input = JsonNode.Parse((await Receive(producer)).Data)!;
        Assert.Equal("input", (string?)input["t"]);
        Assert.Equal("ls\r", Encoding.UTF8.GetString(Convert.FromBase64String((string)input["data"]!)));

        // ITerminalHub.WriteAsync → producer.
        Assert.True(await host.Hub.WriteAsync("T1", "y"u8.ToArray(), CancellationToken.None));
        Assert.Equal("y", Encoding.UTF8.GetString(Convert.FromBase64String((string)JsonNode.Parse((await Receive(producer)).Data)!["data"]!)));
        Assert.False(await host.Hub.WriteAsync("nope", "y"u8.ToArray(), CancellationToken.None));

        // Viewer resize → producer.
        await SendJson(viewer, new JsonObject { ["t"] = "resize", ["cols"] = 120, ["rows"] = 40 });
        var resize = JsonNode.Parse((await Receive(producer)).Data)!;
        Assert.Equal(("resize", 120, 40), ((string)resize["t"]!, (int)resize["cols"]!, (int)resize["rows"]!));

        // Exit → event + viewer notified.
        await SendJson(producer, new JsonObject { ["t"] = "exit", ["code"] = 3 });
        var exited = await host.Sink.WaitFor(EventTypes.TerminalExited);
        Assert.Equal(3, exited.Payload<TerminalData>()!.ExitCode);
        Assert.Equal("term:T1:exit", exited.DedupeKey);
        var exitMsg = JsonNode.Parse((await Receive(viewer)).Data)!;
        Assert.Equal(("exit", 3), ((string)exitMsg["t"]!, (int)exitMsg["code"]!));
        Assert.False(host.Hub.IsAttached("T1"));
        Assert.False(await host.Hub.WriteAsync("T1", "x"u8.ToArray(), CancellationToken.None));

        // Log file on disk survives (used after daemon restart).
        Assert.Equal("hello world", File.ReadAllText(Path.Combine(host.DataDir, "terminals", "T1.log")));
    }

    [Fact]
    public async Task InvalidHello_IsRejected()
    {
        await using var host = await TerminalHost.StartAsync();
        var client = host.App.GetTestServer().CreateWebSocketClient();
        var ws = await client.ConnectAsync(new Uri("ws://localhost/pty/attach?id=T1"), CancellationToken.None);
        await SendJson(ws, new JsonObject { ["id"] = "../../etc", ["cols"] = 80, ["rows"] = 24 });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var r = await ws.ReceiveAsync(new byte[100], cts.Token);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
        Assert.Empty(host.Sink.Events);
    }

    [Fact]
    public async Task DetachedProducer_WithDeadPid_IsSweptAsExited()
    {
        await using var host = await TerminalHost.StartAsync();
        var client = host.App.GetTestServer().CreateWebSocketClient();
        var producer = await client.ConnectAsync(new Uri("ws://localhost/pty/attach?id=T2"), CancellationToken.None);
        await SendJson(producer, new JsonObject { ["id"] = "T2", ["pid"] = 999_999_9, ["cols"] = 80, ["rows"] = 24 });
        await host.Sink.WaitFor(EventTypes.TerminalStarted);
        Assert.Single(host.Hub.ActiveTerminals());
        await producer.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await WaitUntil(() => !host.Hub.IsAttached("T2"));

        await host.Hub.SweepAsync([], DateTimeOffset.UtcNow, CancellationToken.None); // within grace
        Assert.DoesNotContain(host.Sink.Events, e => e.Type == EventTypes.TerminalExited);
        await host.Hub.SweepAsync([("OLD", 999_999_8)], DateTimeOffset.UtcNow + TerminalHub.DetachGrace + TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(["OLD", "T2"], host.Sink.Events.Where(e => e.Type == EventTypes.TerminalExited)
            .Select(e => e.Payload<TerminalData>()!.TerminalId).Order());
        Assert.Empty(host.Hub.ActiveTerminals());
    }

    internal static async Task WaitUntil(Func<bool> cond, int ms = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}
