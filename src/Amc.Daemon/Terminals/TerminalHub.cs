using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Amc.Core.Events;
using Amc.Daemon.Collectors.Processes;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Terminals;

/// <summary>
/// First text frame sent by `agentctl run` on /pty/attach. <c>Pid</c> is the PTY child's pid.
/// </summary>
public sealed record TerminalHello(string Id, string? Command, string? Cwd, int? Pid, int Cols, int Rows);

/// <summary>
/// PTY sessions hosted by `agentctl run` (the producer) and watched/typed into by UI viewers.
///
/// Wire protocol (all JSON text frames are UTF-8 objects with a "t" discriminator):
/// <code>
/// producer → daemon : hello {id,command,cwd,pid,cols,rows} (first frame, no "t")
///                     binary = raw PTY output
///                     {"t":"resize","cols","rows"}   local terminal resized
///                     {"t":"exit","code":N}          child exited (sent before close)
/// daemon → producer : {"t":"input","data":"&lt;base64&gt;"} | {"t":"resize","cols","rows"}
/// viewer ← daemon   : binary ring-buffer contents (always first, possibly empty), then live binary output;
///                     {"t":"resize","cols","rows"} | {"t":"exit","code"}
/// viewer → daemon   : {"t":"input","data":"&lt;base64&gt;"} | {"t":"resize","cols","rows"}
/// </code>
/// </summary>
public sealed partial class TerminalHub : ITerminalHub, ITerminalDirectory, IDisposable
{
    public const string HealthName = "terminals";
    public const int MaxFrameBytes = 1024 * 1024;
    public const int MaxInputBytes = 64 * 1024;
    public const int ViewerQueue = 1024;
    public static readonly TimeSpan DetachGrace = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    /// <summary>How long an exited terminal's ring buffer stays in memory before only the disk log remains.</summary>
    public static readonly TimeSpan ExitedRetention = TimeSpan.FromMinutes(10);
    private readonly IEventSink _sink;
    private readonly HealthRegistry _health;
    private readonly IProcSource _proc;
    private readonly ILogger<TerminalHub> _log;
    private readonly string _dir;
    private readonly int _ringBytes;
    private long _viewerCount;

    public TerminalHub(IEventSink sink, HealthRegistry health, IOptions<AmcOptions> options, ILogger<TerminalHub> log,
        IProcSource? proc = null, int ringBytes = RingBuffer.DefaultCapacity)
    {
        _sink = sink; _health = health; _log = log; _proc = proc ?? new ProcFs();
        _dir = options.Value.TerminalDir;
        _ringBytes = ringBytes;
        _health.Register(HealthName);
        _health.Ok(HealthName, "0 terminal(s)");
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    public static partial Regex ValidId();

    private sealed class Producer(WebSocket ws)
    {
        public WebSocket Ws { get; } = ws;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private sealed class Viewer
    {
        public string Id { get; } = Ulid.NewId();
        public Channel<(byte[] Data, bool Text)> Out { get; } =
            Channel.CreateBounded<(byte[], bool)>(new BoundedChannelOptions(ViewerQueue) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        public volatile bool Lagging;
    }

    private sealed class Session(string id, RingBuffer ring, TerminalLog log)
    {
        public string Id { get; } = id;
        public object Gate { get; } = new();
        public RingBuffer Ring { get; } = ring;
        public TerminalLog Log { get; } = log;
        public Dictionary<string, Viewer> Viewers { get; } = [];
        public Producer? Producer;
        public string? Command, Cwd, ProcStart;
        public int? Pid;
        public int Cols = 80, Rows = 24;
        public bool Exited;
        public int? ExitCode;
        public DateTimeOffset? DetachedAt;
        public DateTimeOffset? ExitedAt;
    }

    // ---- ITerminalHub ----------------------------------------------------------------

    public bool IsAttached(string terminalId) => _sessions.TryGetValue(terminalId, out var s) && s.Producer is not null && !s.Exited;

    public async Task<bool> WriteAsync(string terminalId, ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        if (input.Length == 0) return IsAttached(terminalId);
        if (!_sessions.TryGetValue(terminalId, out var s)) return false;
        for (var off = 0; off < input.Length; off += MaxInputBytes)
        {
            var chunk = input.Slice(off, Math.Min(MaxInputBytes, input.Length - off));
            var msg = new JsonObject { ["t"] = "input", ["data"] = Convert.ToBase64String(chunk.Span) }.ToJsonString();
            if (!await SendToProducer(s, msg, ct)) return false;
        }
        _health.Increment(HealthName, "input_bytes", input.Length);
        return true;
    }

    public byte[] Buffer(string terminalId)
    {
        if (_sessions.TryGetValue(terminalId, out var s)) lock (s.Gate) return s.Ring.ToArray();
        return ValidId().IsMatch(terminalId) ? TerminalLog.ReadTail(_dir, terminalId, _ringBytes) : [];
    }

    public Task<bool> ResizeAsync(string terminalId, int cols, int rows, CancellationToken ct) =>
        _sessions.TryGetValue(terminalId, out var s) && cols is > 0 and < 1000 && rows is > 0 and < 1000
            ? SendToProducer(s, new JsonObject { ["t"] = "resize", ["cols"] = cols, ["rows"] = rows }.ToJsonString(), ct)
            : Task.FromResult(false);

    // ---- ITerminalDirectory -----------------------------------------------------------

    public IReadOnlyList<TerminalRef> ActiveTerminals() =>
        _sessions.Values.Where(s => !s.Exited && s.Pid is > 1).Select(s => new TerminalRef(s.Id, s.Pid!.Value, s.ProcStart)).ToList();

    // ---- producer --------------------------------------------------------------------

    public async Task RunProducerAsync(WebSocket ws, string? queryId, CancellationToken ct)
    {
        TerminalHello? hello;
        using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            helloCts.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await ReceiveAsync(ws, 64 * 1024, helloCts.Token);
            hello = first is { Text: true } ? TryParse<TerminalHello>(first.Value.Data) : null;
        }
        if (hello is null || !ValidId().IsMatch(hello.Id) || (queryId is { Length: > 0 } && queryId != hello.Id))
        {
            await CloseQuietly(ws, WebSocketCloseStatus.PolicyViolation, "invalid hello");
            return;
        }

        var s = _sessions.GetOrAdd(hello.Id, id => new Session(id, new RingBuffer(_ringBytes), new TerminalLog(_dir, id)));
        var producer = new Producer(ws);
        Producer? previous;
        bool isNew;
        lock (s.Gate)
        {
            previous = s.Producer;
            isNew = s.Command is null;
            s.Producer = producer;
            s.DetachedAt = null;
            s.Command ??= hello.Command;
            s.Cwd ??= hello.Cwd;
            if (hello.Pid is > 1 && s.Pid != hello.Pid)
            {
                s.Pid = hello.Pid;
                s.ProcStart = ProcParsers.ParseStat(_proc.ReadStat(hello.Pid.Value))?.ProcStart;
            }
            if (hello.Cols > 0 && hello.Rows > 0) { s.Cols = hello.Cols; s.Rows = hello.Rows; }
        }
        if (previous is not null) _ = CloseQuietly(previous.Ws, WebSocketCloseStatus.PolicyViolation, "replaced by a new producer");
        _health.Increment(HealthName, isNew ? "started" : "reattached");
        UpdateHealth();

        await _sink.PublishAsync(AgentEvent.Create(EventTypes.TerminalStarted, EventSources.Pty,
            new TerminalData(s.Id, s.Pid, s.Command, s.Cwd)) with { DedupeKey = $"term:{s.Id}:start" }, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await ReceiveAsync(ws, MaxFrameBytes, ct);
                if (msg is null) break;
                if (!msg.Value.Text) { OnOutput(s, msg.Value.Data); continue; }
                var json = TryParse<JsonObject>(msg.Value.Data);
                switch ((string?)json?["t"])
                {
                    case "exit":
                        await MarkExited(s, json!["code"] is JsonValue v && v.TryGetValue<int>(out var code) ? code : null, ct);
                        break;
                    case "resize":
                        if (Int(json!["cols"]) is { } c and > 0 && Int(json["rows"]) is { } r and > 0)
                        {
                            lock (s.Gate) { s.Cols = c; s.Rows = r; }
                            Broadcast(s, Encoding.UTF8.GetBytes(new JsonObject { ["t"] = "resize", ["cols"] = c, ["rows"] = r }.ToJsonString()), text: true);
                        }
                        break;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        finally
        {
            lock (s.Gate)
            {
                if (ReferenceEquals(s.Producer, producer)) { s.Producer = null; s.DetachedAt = DateTimeOffset.UtcNow; }
            }
            UpdateHealth();
        }
    }

    private void OnOutput(Session s, byte[] data)
    {
        lock (s.Gate)
        {
            s.Ring.Append(data);
            try { s.Log.Append(data); }
            catch (IOException e) { _health.Increment(HealthName, "log_errors"); _log.LogDebug(e, "terminal log write failed"); }
        }
        Broadcast(s, data, text: false);
        _health.Increment(HealthName, "output_bytes", data.Length);
    }

    private void Broadcast(Session s, byte[] data, bool text)
    {
        lock (s.Gate)
        {
            foreach (var v in s.Viewers.Values)
            {
                if (v.Out.Writer.TryWrite((data, text))) continue;
                // Too slow: drop the viewer; it reconnects and receives the ring buffer again.
                v.Lagging = true;
                v.Out.Writer.TryComplete();
                _health.Increment(HealthName, "viewer_drops");
            }
        }
    }

    private async Task MarkExited(Session s, int? code, CancellationToken ct)
    {
        lock (s.Gate)
        {
            if (s.Exited) return;
            s.Exited = true;
            s.ExitedAt = DateTimeOffset.UtcNow;
            s.ExitCode = code;
        }
        var exitMsg = new JsonObject { ["t"] = "exit", ["code"] = code }.ToJsonString();
        Broadcast(s, Encoding.UTF8.GetBytes(exitMsg), text: true);
        _health.Increment(HealthName, "exited");
        UpdateHealth();
        await _sink.PublishAsync(AgentEvent.Create(EventTypes.TerminalExited, EventSources.Pty,
            new TerminalData(s.Id, s.Pid, s.Command, s.Cwd, code)) with { DedupeKey = $"term:{s.Id}:exit" }, ct);
    }

    /// <summary>
    /// A producer that vanished without an exit frame (agentctl killed, daemon restart) is
    /// declared exited once its child pid is gone and the grace period has passed.
    /// Also closes terminals restored from state whose pid no longer exists.
    /// </summary>
    public async Task SweepAsync(IEnumerable<(string Id, int? Pid)> restoredAlive, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var s in _sessions.Values)
        {
            int? pid; string? start; DateTimeOffset? detached; bool exited;
            lock (s.Gate) { pid = s.Pid; start = s.ProcStart; detached = s.DetachedAt; exited = s.Exited; }
            if (exited || detached is null || now - detached < DetachGrace) continue;
            if (pid is null || !ProcessSignals.IsAlive(pid.Value, start, _proc))
                await MarkExited(s, null, ct);
        }
        // Free the in-memory ring (2 MiB) of terminals that exited a while ago and have no viewer;
        // their output stays available from the on-disk log (see Buffer / RunViewerAsync).
        foreach (var s in _sessions.Values)
        {
            bool evict;
            lock (s.Gate) evict = s.Exited && s.ExitedAt is { } at && now - at > ExitedRetention && s.Viewers.Count == 0;
            if (!evict || !_sessions.TryRemove(s.Id, out _)) continue;
            lock (s.Gate) s.Log.Dispose();
        }
        foreach (var (id, pid) in restoredAlive)
        {
            if (_sessions.ContainsKey(id)) continue;
            if (pid is null || !ProcessSignals.IsAlive(pid.Value, null, _proc))
                await _sink.PublishAsync(AgentEvent.Create(EventTypes.TerminalExited, EventSources.Pty,
                    new TerminalData(id, pid)) with { DedupeKey = $"term:{id}:exit" }, ct);
        }
    }

    // ---- viewer ----------------------------------------------------------------------

    public bool Exists(string id) => _sessions.ContainsKey(id);

    public async Task RunViewerAsync(WebSocket ws, string id, CancellationToken ct)
    {
        var s = _sessions.GetValueOrDefault(id);
        var viewer = new Viewer();
        byte[] initial;
        if (s is null)
        {
            // Unknown in memory (e.g. after a daemon restart): replay the on-disk log tail, read-only.
            initial = Buffer(id);
        }
        else lock (s.Gate)
        {
            initial = s.Ring.ToArray();   // snapshot + subscribe atomically: no gap, no duplicates
            s.Viewers[viewer.Id] = viewer;
            if (s.Exited)
                viewer.Out.Writer.TryWrite((Encoding.UTF8.GetBytes(new JsonObject { ["t"] = "exit", ["code"] = s.ExitCode }.ToJsonString()), true));
        }
        Interlocked.Increment(ref _viewerCount);
        UpdateHealth();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await ws.SendAsync(initial, WebSocketMessageType.Binary, true, cts.Token);
            if (s is null) { await CloseQuietly(ws, WebSocketCloseStatus.NormalClosure, "terminal not active"); return; }

            var receive = ViewerReceiveLoop(ws, s, viewer, cts.Token);
            var reader = viewer.Out.Reader;
            while (await reader.WaitToReadAsync(cts.Token))
                while (reader.TryRead(out var f))
                    await ws.SendAsync(f.Data, f.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, cts.Token);
            // channel completed: viewer fell behind
            await CloseQuietly(ws, viewer.Lagging ? (WebSocketCloseStatus)1013 : WebSocketCloseStatus.NormalClosure, viewer.Lagging ? "lagging; reconnect" : "closed");
            await cts.CancelAsync();
            await receive.ContinueWith(_ => { }, TaskScheduler.Default);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        finally
        {
            if (s is not null) lock (s.Gate) s.Viewers.Remove(viewer.Id);
            Interlocked.Decrement(ref _viewerCount);
            UpdateHealth();
        }
    }

    private async Task ViewerReceiveLoop(WebSocket ws, Session s, Viewer viewer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await ReceiveAsync(ws, MaxInputBytes * 2, ct);
                if (msg is null) break;
                if (!msg.Value.Text) continue;
                var json = TryParse<JsonObject>(msg.Value.Data);
                switch ((string?)json?["t"])
                {
                    case "input" when json!["data"] is JsonValue d && d.TryGetValue<string>(out var b64):
                        byte[] bytes;
                        try { bytes = Convert.FromBase64String(b64); } catch (FormatException) { continue; }
                        await WriteAsync(s.Id, bytes, ct);
                        break;
                    case "resize":
                        if (Int(json!["cols"]) is { } c && Int(json["rows"]) is { } r) await ResizeAsync(s.Id, c, r, ct);
                        break;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        finally
        {
            // Viewer went away: stop its send loop.
            viewer.Out.Writer.TryComplete();
        }
    }

    // ---- helpers ---------------------------------------------------------------------

    private async Task<bool> SendToProducer(Session s, string json, CancellationToken ct)
    {
        Producer? p;
        lock (s.Gate) p = s.Exited ? null : s.Producer;
        if (p is null || p.Ws.State != WebSocketState.Open) return false;
        await p.SendLock.WaitAsync(ct);
        try
        {
            await p.Ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { return false; }
        finally { p.SendLock.Release(); }
    }

    private static async Task<(byte[] Data, bool Text)?> ReceiveAsync(WebSocket ws, int max, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                await CloseQuietly(ws, WebSocketCloseStatus.NormalClosure, "bye");
                return null;
            }
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > max)
            {
                await CloseQuietly(ws, WebSocketCloseStatus.MessageTooBig, "frame too large");
                return null;
            }
            if (r.EndOfMessage) return (ms.ToArray(), r.MessageType == WebSocketMessageType.Text);
        }
    }

    private static async Task CloseQuietly(WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(status, reason, cts.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static T? TryParse<T>(byte[] utf8) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(utf8, Json.Options); }
        catch (JsonException) { return null; }
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private void UpdateHealth()
    {
        var attached = _sessions.Values.Count(x => x.Producer is not null && !x.Exited);
        _health.SetCounter(HealthName, "sessions", _sessions.Count);
        _health.SetCounter(HealthName, "attached", attached);
        _health.SetCounter(HealthName, "viewers", Interlocked.Read(ref _viewerCount));
        _health.Ok(HealthName, $"{attached} attached terminal(s), {Interlocked.Read(ref _viewerCount)} viewer(s)");
    }

    public void Dispose()
    {
        foreach (var s in _sessions.Values) lock (s.Gate) s.Log.Dispose();
    }
}
