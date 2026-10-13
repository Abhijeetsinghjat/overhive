using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amc.Cli.Run;

/// <summary>
/// Mirrors PTY output to the daemon (/pty/attach) and receives injected input/resizes.
/// Never blocks the terminal: output is queued (bounded by bytes, oldest dropped) and the
/// connection is retried with exponential backoff (1 s … 30 s) while the command keeps running.
/// </summary>
internal sealed class DaemonLink : IAsyncDisposable
{
    public const int MaxQueuedBytes = 1024 * 1024;

    private readonly Uri _uri;
    private readonly string? _token;
    private readonly Func<JsonObject> _hello;
    private readonly Action<byte[]> _onInput;
    private readonly Action<int, int> _onResize;
    private readonly Action<string> _notice;
    private readonly Queue<(byte[] Data, bool Text)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private long _queuedBytes;
    private int? _exitCode;
    private volatile bool _finishing;
    private bool _noticed;
    private Task? _loop;

    public bool Connected { get; private set; }
    public long DroppedBytes { get; private set; }

    public DaemonLink(Uri attachUri, string? token, Func<JsonObject> hello, Action<byte[]> onInput, Action<int, int> onResize, Action<string> notice)
    {
        _uri = attachUri; _token = token; _hello = hello; _onInput = onInput; _onResize = onResize; _notice = notice;
    }

    public static Uri AttachUri(string daemonUrl, string terminalId)
    {
        var b = new UriBuilder(daemonUrl.TrimEnd('/'));
        b.Scheme = b.Scheme == "https" ? "wss" : "ws";
        b.Path = b.Path.TrimEnd('/') + "/pty/attach";
        b.Query = "id=" + Uri.EscapeDataString(terminalId);
        return b.Uri;
    }

    public void Start() => _loop = Task.Run(RunAsync);

    public void EnqueueOutput(ReadOnlySpan<byte> data) => Enqueue(data.ToArray(), text: false);

    public void SendResize(int cols, int rows) =>
        Enqueue(Encoding.UTF8.GetBytes(new JsonObject { ["t"] = "resize", ["cols"] = cols, ["rows"] = rows }.ToJsonString()), text: true);

    private void Enqueue(byte[] data, bool text)
    {
        lock (_queue)
        {
            _queue.Enqueue((data, text));
            _queuedBytes += data.Length;
            while (_queuedBytes > MaxQueuedBytes && _queue.Count > 1)
            {
                var d = _queue.Dequeue();
                _queuedBytes -= d.Data.Length;
                DroppedBytes += d.Data.Length;
            }
        }
        _signal.Release();
    }

    /// <summary>Flushes queued output, reports the exit code and closes; gives up after <paramref name="timeout"/>.</summary>
    public async Task CompleteAsync(int exitCode, TimeSpan timeout)
    {
        _exitCode = exitCode;
        _finishing = true;
        _signal.Release();
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(timeout));
        await _stop.CancelAsync();
    }

    private async Task RunAsync()
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!_stop.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            if (_token is not null) ws.Options.SetRequestHeader("X-Amc-Token", _token);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(3));
                    await ws.ConnectAsync(_uri, connectCts.Token);
                }
                await ws.SendAsync(Encoding.UTF8.GetBytes(_hello().ToJsonString()), WebSocketMessageType.Text, true, _stop.Token);
                Connected = true;
                delay = TimeSpan.FromSeconds(1);
                if (await PumpAsync(ws)) return; // finished cleanly
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or HttpRequestException)
            {
                if (_stop.IsCancellationRequested) return;
                if (!_noticed)
                {
                    _noticed = true;
                    _notice($"mission control daemon not reachable ({Short(e)}); running locally, will keep retrying");
                }
            }
            finally { Connected = false; }

            if (_finishing) return; // the command is over; do not wait for a daemon that isn't there
            DropQueued(); // output produced while disconnected is not replayed beyond the queue bound
            try { await Task.WhenAny(Task.Delay(delay, _stop.Token), WaitFinishing()); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
        }
    }

    private async Task WaitFinishing()
    {
        while (!_finishing && !_stop.IsCancellationRequested) await Task.Delay(100);
    }

    private void DropQueued()
    {
        lock (_queue)
            if (_queuedBytes > MaxQueuedBytes / 2)
            {
                DroppedBytes += _queuedBytes;
                _queue.Clear();
                _queuedBytes = 0;
            }
    }

    /// <summary>Returns true when the exit was reported and the socket closed.</summary>
    private async Task<bool> PumpAsync(ClientWebSocket ws)
    {
        var receive = ReceiveLoop(ws);
        while (ws.State == WebSocketState.Open)
        {
            var wait = _signal.WaitAsync(_stop.Token);
            if (await Task.WhenAny(wait, receive) == receive) return false;
            await wait;
            while (true)
            {
                (byte[] Data, bool Text) item;
                lock (_queue)
                {
                    if (_queue.Count == 0) break;
                    item = _queue.Dequeue();
                    _queuedBytes -= item.Data.Length;
                }
                await ws.SendAsync(item.Data, item.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, _stop.Token);
            }
            if (_finishing)
            {
                var exit = new JsonObject { ["t"] = "exit", ["code"] = _exitCode }.ToJsonString();
                await ws.SendAsync(Encoding.UTF8.GetBytes(exit), WebSocketMessageType.Text, true, _stop.Token);
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "exited", _stop.Token); }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
                return true;
            }
        }
        return false;
    }

    private async Task ReceiveLoop(ClientWebSocket ws)
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, _stop.Token);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) { if (ms.Length > 1024 * 1024) return; continue; }
                var msg = ms.ToArray();
                ms.SetLength(0);
                if (r.MessageType != WebSocketMessageType.Text) continue;
                Handle(msg);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
    }

    private void Handle(byte[] utf8)
    {
        JsonObject? json;
        try { json = JsonNode.Parse(utf8) as JsonObject; } catch (JsonException) { return; }
        switch ((string?)json?["t"])
        {
            case "input" when json!["data"] is JsonValue v && v.TryGetValue<string>(out var b64):
                try { _onInput(Convert.FromBase64String(b64)); } catch (FormatException) { }
                break;
            case "resize" when json!["cols"] is JsonValue c && c.TryGetValue<int>(out var cols)
                               && json["rows"] is JsonValue r && r.TryGetValue<int>(out var rows) && cols is > 0 and < 1000 && rows is > 0 and < 1000:
                _onResize(cols, rows);
                break;
        }
    }

    private static string Short(Exception e) => e is OperationCanceledException ? "timeout" : e.Message.Split('\n')[0];

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null) await _loop.ContinueWith(_ => { }, TaskScheduler.Default);
        _stop.Dispose();
    }
}
