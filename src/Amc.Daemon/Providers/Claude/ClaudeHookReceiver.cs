using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Security;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Providers.Claude;

/// <summary>
/// POST /hooks/claude. The synchronous part only logs the payload, decides mailbox delivery
/// and answers (hooks block the agent, so this must take milliseconds). Mapping to events —
/// which may need worktree resolution — runs on a single background worker in arrival order.
/// </summary>
public sealed class ClaudeHookReceiver : BackgroundService
{
    public const string HealthName = "claude-hooks";
    public const long HookLogMaxBytes = 5 * 1024 * 1024;
    private const int LogStringCap = 2000;

    private readonly ClaudeEventMapper _mapper;
    private readonly ClaudeContext _ctx;
    private readonly Mailbox _mailbox;
    private readonly IEventSink _sink;
    private readonly HealthRegistry _health;
    private readonly AmcOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ClaudeHookReceiver> _log;
    private readonly ClaudeHooksState? _hooks;
    private readonly ClaudeRewake? _rewake;
    private readonly Channel<HookWork> _work = Channel.CreateBounded<HookWork>(new BoundedChannelOptions(10_000)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly object _logGate = new();

    public sealed record HookWork(JsonObject Payload, DateTimeOffset At, bool Delivered);

    public ClaudeHookReceiver(ClaudeEventMapper mapper, Mailbox mailbox, IEventSink sink, HealthRegistry health,
        IOptions<AmcOptions> options, TimeProvider time, ILogger<ClaudeHookReceiver> log, ClaudeHooksState? hooks = null, ClaudeRewake? rewake = null)
    {
        _mapper = mapper; _ctx = mapper.Context; _mailbox = mailbox; _sink = sink; _health = health;
        _options = options.Value; _time = time; _log = log; _hooks = hooks; _rewake = rewake;
        _health.Register(HealthName);
    }

    // ---- fast path ---------------------------------------------------------------------

    /// <summary>Handles one hook call; returns the JSON to print on the hook's stdout, or null for none.</summary>
    public async Task<JsonObject?> HandleAsync(JsonObject payload, CancellationToken ct = default)
    {
        var (response, work) = await AcceptAsync(payload, ct);
        if (work is not null && !_work.Writer.TryWrite(work))
        {
            _health.Increment(HealthName, "dropped");
            _health.Degraded(HealthName, "hook work queue full");
        }
        return response;
    }

    /// <summary>Same as <see cref="HandleAsync"/> but maps the payload inline (tests, deterministic ordering).</summary>
    public async Task<JsonObject?> HandleAndProcessAsync(JsonObject payload, CancellationToken ct = default)
    {
        var (response, work) = await AcceptAsync(payload, ct);
        if (work is not null) await ProcessAsync(work, ct);
        return response;
    }

    private async Task<(JsonObject? Response, HookWork? Work)> AcceptAsync(JsonObject payload, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        _health.Increment(HealthName, "received");
        LogPayload(payload, now);
        var evt = Str(payload, "hook_event_name");
        var sid = Str(payload, "session_id");
        if (evt is null || sid is null)
        {
            _health.Increment(HealthName, "invalid");
            return (null, null);
        }
        _health.Increment(HealthName, "event:" + evt);
        _rewake?.Observe(evt, sid, payload); // idle/busy tracking for immediate delivery to idle sessions
        JsonObject? response = null;
        try { response = await DeliverAsync(evt, sid, payload, now, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Mailbox delivery via {Hook} failed", evt);
            _health.Increment(HealthName, "errors");
        }
        _health.Ok(HealthName, $"last hook {evt} at {now:HH:mm:ss}");
        return (response, new HookWork(payload, now, response is not null));
    }

    /// <summary>The agent a hook fired for: the sub-agent when agent_id is present, otherwise the main agent.</summary>
    public static string AgentFor(string sessionId, JsonObject payload) =>
        Str(payload, "agent_id") is { Length: > 0 } aid ? ClaudePaths.SubId(sessionId, aid) : ClaudePaths.MainId(sessionId);

    private async Task<JsonObject?> DeliverAsync(string evt, string sid, JsonObject payload, DateTimeOffset now, CancellationToken ct)
    {
        var target = AgentFor(sid, payload);
        var viaContext = evt is "PostToolUse" or "UserPromptSubmit";
        var viaBlock = evt is "Stop" or "SubagentStop" && !Bool(payload, "stop_hook_active");
        if (!(viaContext || viaBlock) || !_mailbox.HasPending(target)) return null;
        var messages = _mailbox.DrainFor(target);
        if (messages.Count == 0) return null;

        var text = string.Join("\n\n", messages.Select(Frame));
        var response = viaContext
            ? new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = evt, ["additionalContext"] = text } }
            : new JsonObject { ["decision"] = "block", ["reason"] = text };
        var detail = viaContext ? $"Delivered via {evt} hook (additionalContext)" : $"Delivered via {evt} hook (decision: block)";
        var o = new Origin(EventSources.Hook, sid, target, now, Str(payload, "cwd"));
        foreach (var m in messages)
        {
            await _sink.PublishAsync(ClaudeEventMapper.New(o, EventTypes.MessageDelivered,
                new MessageStatusData(m.MessageId, Transports.HookMailbox, detail), $"msg:{m.MessageId}:delivered", evidence: Evidence.Observed), ct);
            _ctx.AddPendingReply(target, new PendingReply(m.MessageId, now));
        }
        _health.Increment(HealthName, "messages_delivered", messages.Count);
        return response;
    }

    public static string Frame(PendingMessage m) =>
        $"Message from the user via Overhive (sent {m.QueuedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}): {m.Text}";

    // ---- background mapping --------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A quiet but correctly configured receiver is healthy; a missing install is explicit, not "starting".
        if (_hooks?.IsInstalled() == true) _health.Ok(HealthName, "hooks installed; waiting for hook events");
        else if (_hooks is not null) _health.Disabled(HealthName, $"hooks not installed in {_hooks.SettingsPath} — run `agentctl hooks install` for real-time events and messaging");
        await foreach (var w in _work.Reader.ReadAllAsync(ct))
        {
            try { await ProcessAsync(w, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hook mapping failed");
                _health.Increment(HealthName, "errors");
                _health.Degraded(HealthName, "mapping failed: " + ex.Message);
            }
        }
    }

    public async Task ProcessAsync(HookWork w, CancellationToken ct)
    {
        var events = await MapAsync(w.Payload, w.At, w.Delivered, ct);
        foreach (var e in events) await _sink.PublishAsync(e, ct);
        _health.Increment(HealthName, "events", events.Count);
    }

    public async Task<List<AgentEvent>> MapAsync(JsonObject p, DateTimeOffset at, bool delivered, CancellationToken ct)
    {
        var evt = Str(p, "hook_event_name")!;
        var sid = Str(p, "session_id")!;
        var mainId = ClaudePaths.MainId(sid);
        var agentId = AgentFor(sid, p);
        var cwd = Str(p, "cwd");
        var transcript = Str(p, "transcript_path");
        var o = new Origin(EventSources.Hook, sid, agentId, at, cwd);
        var main = o with { AgentId = mainId };
        var list = new List<AgentEvent>();
        list.AddRange(_mapper.EnsureMain(main, transcript));

        switch (evt)
        {
            case "SessionStart":
            {
                var source = Str(p, "source") ?? "startup";
                if (source != "compact")
                    list.Add(ClaudeEventMapper.Status(main, EventTypes.AgentStarted, AgentStatus.Waiting,
                        $"Session started ({source}); waiting for input"));
                if (transcript is not null && _ctx.Changed(mainId, "transcript", transcript))
                    list.Add(ClaudeEventMapper.New(main, EventTypes.AgentUpdated, new AgentUpdatedData(TranscriptPath: transcript), null));
                break;
            }
            case "UserPromptSubmit" when Str(p, "prompt") is { } raw:
            {
                var text = TranscriptIngestor.CleanPrompt(raw);
                if (text is null) break;
                list.Add(_mapper.Prompt(main, text, _ctx.PromptKey(sid, Str(p, "prompt_id"), text, EventSources.Hook)));
                list.Add(ClaudeEventMapper.Status(main, EventTypes.AgentStatusChanged, AgentStatus.Running, "Prompt submitted"));
                break;
            }
            case "PreToolUse" when Str(p, "tool_use_id") is { } id && Str(p, "tool_name") is { } tool:
                list.AddRange(EnsureSub(o, p));
                list.AddRange(await _mapper.ToolStartAsync(o, tool, id, p["tool_input"] as JsonObject ?? new JsonObject(), ct));
                break;
            case "PostToolUse" when Str(p, "tool_use_id") is { } id:
            {
                list.AddRange(EnsureSub(o, p));
                var response = p["tool_response"] ?? p["tool_output"] ?? p["tool_result"];
                var text = response is JsonValue v && v.TryGetValue<string>(out var s) ? s : TranscriptIngestor.ContentText(response);
                list.AddRange(await _mapper.ToolEndAsync(o, Str(p, "tool_name"), id, p["tool_input"] as JsonObject, response, text, false, ct));
                break;
            }
            case "PostToolUseFailure" when Str(p, "tool_use_id") is { } id:
            {
                list.AddRange(EnsureSub(o, p));
                var response = p["tool_response"] ?? p["tool_output"];
                var error = Str(p, "error") ?? Str(p["error"], "message") ?? Str(p, "error_message") ?? Str(p, "message")
                            ?? TranscriptIngestor.ContentText(response) ?? (Bool(p, "is_interrupt") ? "Interrupted by user" : "Tool call failed");
                list.AddRange(await _mapper.ToolEndAsync(o, Str(p, "tool_name"), id, p["tool_input"] as JsonObject, response as JsonObject, error, true, ct));
                break;
            }
            case "SubagentStart" when Str(p, "agent_id") is { } aid:
                list.AddRange(EnsureSub(o, p, Str(p, "agent_transcript_path")));
                break;
            case "SubagentStop" when Str(p, "agent_id") is { } aid:
            {
                // Claude Code also fires SubagentStop for internal helper agents that never started,
                // have no type and no transcript. With no evidence of a real sub-agent, record nothing.
                if (!_ctx.IsKnown(ClaudePaths.SubId(sid, aid)) && Str(p, "agent_type") is null or ""
                    && !(Str(p, "agent_transcript_path") is { Length: > 0 } tp && File.Exists(tp)))
                    break;
                list.AddRange(EnsureSub(o, p, Str(p, "agent_transcript_path")));
                if (Str(p, "last_assistant_message") is { Length: > 0 } last) list.AddRange(_mapper.Replies(o, last));
                if (delivered)
                    list.Add(ClaudeEventMapper.Status(o, EventTypes.AgentStatusChanged, AgentStatus.Running,
                        "Message delivered via SubagentStop hook; sub-agent continues"));
                else
                    // Claude Code fires SubagentStop once per stop, and a parent can resume the same agent id
                    // later; so every stop is its own fact (unique key), never deduped against an earlier run.
                    // A duplicate "completed" from the transcript is harmless (Completed→Completed is a no-op).
                    list.Add(ClaudeEventMapper.Status(o, EventTypes.AgentCompleted, AgentStatus.Completed,
                        "Sub-agent finished (SubagentStop)", $"{_ctx.EndKey(agentId, sid)}:stop:{Amc.Core.Events.Ulid.NewId()}"));
                break;
            }
            case "Stop":
                if (Str(p, "last_assistant_message") is { Length: > 0 } lastMain) list.AddRange(_mapper.Replies(main, lastMain));
                list.Add(delivered
                    ? ClaudeEventMapper.Status(main, EventTypes.AgentStatusChanged, AgentStatus.Running, "Message delivered via Stop hook; agent continues")
                    : ClaudeEventMapper.Status(main, EventTypes.AgentStatusChanged, AgentStatus.Waiting, "Turn finished; waiting for input"));
                break;
            case "Notification":
            {
                list.AddRange(EnsureSub(o, p));
                var type = Str(p, "notification_type");
                var message = Str(p, "message") ?? Str(p, "title") ?? type ?? "Notification";
                list.Add(ClaudeEventMapper.New(o, EventTypes.AgentNotification, new AgentTextData(message, type), null));
                if (type == "permission_prompt" || (type is null && message.Contains("permission", StringComparison.OrdinalIgnoreCase)))
                    list.Add(ClaudeEventMapper.Status(o, EventTypes.AgentStatusChanged, AgentStatus.Blocked,
                        "Waiting for permission: " + Core.State.Reducer.Truncate(message, 200)));
                else if (type == "idle_prompt")
                    list.Add(ClaudeEventMapper.Status(o, EventTypes.AgentStatusChanged, AgentStatus.Waiting, "Idle; waiting for input"));
                break;
            }
            case "SessionEnd":
            {
                var reason = Str(p, "reason") ?? "other";
                list.Add(ClaudeEventMapper.Status(main, EventTypes.AgentCompleted, AgentStatus.Completed,
                    $"Session ended ({reason})", _ctx.EndKey(mainId, sid)));
                break;
            }
            default:
                _health.Increment(HealthName, "unmapped");
                break;
        }
        return list;
    }

    /// <summary>Ensures the sub-agent of a hook (agent_id present) exists, matching it to the Agent tool call that spawned it.</summary>
    private List<AgentEvent> EnsureSub(Origin o, JsonObject p, string? transcriptPath = null)
    {
        if (Str(p, "agent_id") is not { Length: > 0 } aid) return [];
        var child = ClaudePaths.SubId(o.SessionId, aid);
        if (_ctx.SpawnResolved.ContainsKey(child) || (_ctx.IsKnown(child) && Str(p, "hook_event_name") != "SubagentStart")) return [];
        var claim = _ctx.ClaimSpawn(o.SessionId, Str(p, "agent_type"), child);
        if (claim is null && _ctx.IsKnown(child)) return [];
        var list = _mapper.Spawned(o with { AgentId = child }, child, claim?.ParentAgentId, Str(p, "agent_type"),
            claim?.Description, claim?.ToolUseId, transcriptPath, claim is not null);
        list.AddRange(_mapper.FlushChildEnds(o.SessionId));
        return list;
    }

    // ---- raw payload log (for verifying field names against real Claude Code) ----------

    private void LogPayload(JsonObject payload, DateTimeOffset now)
    {
        try
        {
            var copy = (JsonObject)payload.DeepClone();
            Cap(copy);
            var line = new JsonObject { ["receivedAt"] = now.ToString("O"), ["payload"] = copy }.ToJsonString() + "\n";
            var path = _options.HookLogPath;
            lock (_logGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length + line.Length > HookLogMaxBytes) File.Move(path, path + ".1", overwrite: true);
                var isNew = !File.Exists(path);
                File.AppendAllText(path, line);
                if (isNew && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _health.Increment(HealthName, "log_errors");
        }
    }

    private static void Cap(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s)) o[key] = Redactor.RedactAndCap(s, LogStringCap);
                    else Cap(o[key]);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s)) a[i] = Redactor.RedactAndCap(s, LogStringCap);
                    else Cap(a[i]);
                }
                break;
        }
    }

    private static string? Str(JsonNode? n, string key) => ClaudeEventMapper.Str(n, key);
    private static bool Bool(JsonNode? n, string key) => ClaudeEventMapper.Bool(n, key);
}
