using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Providers.Claude;

/// <summary>One ~/.claude/sessions/&lt;pid&gt;.json entry (undocumented format; parsed tolerantly).</summary>
public sealed record RegistryEntry(
    int Pid, string SessionId, string? Cwd, DateTimeOffset? StartedAt, string? ProcStart, string? Version,
    string? Kind, string? Entrypoint, string? Name, string? Status, DateTimeOffset? StatusUpdatedAt)
{
    public static RegistryEntry? Parse(string json, int? pidFromFileName = null)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var pid = o["pid"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? p : pidFromFileName ?? 0;
            var sid = ClaudeEventMapper.Str(o, "sessionId");
            if (pid <= 0 || string.IsNullOrEmpty(sid)) return null;
            var procStart = ClaudeEventMapper.Str(o, "procStart") ??
                            (o["procStart"] is JsonValue ps && ps.TryGetValue<long>(out var psl) ? psl.ToString(CultureInfo.InvariantCulture) : null);
            return new RegistryEntry(pid, sid, ClaudeEventMapper.Str(o, "cwd"), Ms(o["startedAt"]), procStart,
                ClaudeEventMapper.Str(o, "version"), ClaudeEventMapper.Str(o, "kind"), ClaudeEventMapper.Str(o, "entrypoint"),
                ClaudeEventMapper.Str(o, "name"), ClaudeEventMapper.Str(o, "status"),
                Ms(o["statusUpdatedAt"]) ?? Ms(o["updatedAt"]));
        }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? Ms(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<long>(out var ms) && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    public (AgentStatus Status, string Reason)? MappedStatus => Status?.ToLowerInvariant() switch
    {
        "busy" => (AgentStatus.Running, "Working (session registry reports busy)"),
        "idle" => (AgentStatus.Waiting, "Idle at prompt (session registry reports idle)"),
        _ => null,
    };
}

/// <summary>
/// Watches Claude Code's live-session registry (~/.claude/sessions/*.json; never the *.key
/// files). Emits agent.created/updated for sessions, busy/idle status for live processes
/// and agent.completed when an entry disappears together with its process. Process crash
/// detection (process.exited → disconnected) is the process collector's job.
/// </summary>
public sealed class ClaudeRegistryCollector : BackgroundService
{
    public const string HealthName = "claude-registry";
    private static readonly TimeSpan ReconcileEvery = TimeSpan.FromSeconds(5);
    /// <summary>A non-terminal session without live evidence for this long becomes "unknown".</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private readonly ClaudeContext _ctx;
    private readonly IEventSink _sink;
    private readonly IProcFs _proc;
    private readonly StateStore _state;
    private readonly HealthRegistry _health;
    private readonly AmcOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ClaudeRegistryCollector> _log;
    private readonly Dictionary<string, RegistryEntry> _entries = new();
    private readonly Dictionary<string, string> _lastStatus = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0);

    public ClaudeRegistryCollector(ClaudeContext ctx, IEventSink sink, IProcFs proc, StateStore state, HealthRegistry health,
        IOptions<AmcOptions> options, TimeProvider time, ILogger<ClaudeRegistryCollector> log)
    {
        _ctx = ctx; _sink = sink; _proc = proc; _state = state; _health = health; _options = options.Value; _time = time; _log = log;
        _health.Register(HealthName, TimeSpan.FromSeconds(30));
    }

    public string SessionsDir => Path.Combine(_options.ClaudeHome, "sessions");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        FileSystemWatcher? watcher = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (watcher is null && Directory.Exists(SessionsDir))
                {
                    watcher = new FileSystemWatcher(SessionsDir, "*.json")
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    FileSystemEventHandler h = (_, _) => _signal.Release();
                    watcher.Changed += h; watcher.Created += h; watcher.Deleted += h;
                    watcher.Renamed += (_, _) => _signal.Release();
                    watcher.EnableRaisingEvents = true;
                }
                await ReconcileAsync(ct);
                await _signal.WaitAsync(ReconcileEvery, ct);
                await Task.Delay(100, ct); // coalesce bursts (Claude rewrites the file on every status flip)
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Claude registry reconcile failed");
                _health.Degraded(HealthName, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        watcher?.Dispose();
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            var current = new Dictionary<string, RegistryEntry>();
            if (Directory.Exists(SessionsDir))
            {
                foreach (var file in Directory.EnumerateFiles(SessionsDir, "*.json"))
                {
                    if (!file.EndsWith(".json", StringComparison.Ordinal)) continue; // never touch *.key
                    string json;
                    try { json = File.ReadAllText(file); }
                    catch (IOException) { continue; } // vanished or being rewritten; next pass
                    var pidFromName = int.TryParse(Path.GetFileNameWithoutExtension(file), CultureInfo.InvariantCulture, out var pn) ? pn : (int?)null;
                    if (RegistryEntry.Parse(json, pidFromName) is { } e) current[file] = e;
                    else _health.Increment(HealthName, "parse_errors");
                }
            }

            var events = new List<AgentEvent>();
            var live = 0;
            foreach (var (file, e) in current)
            {
                var id = ClaudePaths.MainId(e.SessionId);
                var alive = _proc.IsAlive(e.Pid, e.ProcStart);
                var o = new Origin(EventSources.Registry, e.SessionId, id, e.StartedAt ?? now, e.Cwd);
                _entries.TryGetValue(file, out var prev);
                if (prev is null || prev.SessionId != e.SessionId || prev.ProcStart != e.ProcStart)
                {
                    var transcript = ClaudePaths.FindTranscript(_options.ClaudeHome, e.SessionId, e.Cwd);
                    _ctx.MarkAnnounced(id);
                    events.Add(ClaudeEventMapper.New(o, EventTypes.AgentCreated,
                        new AgentCreatedData(AgentKind.Main, ClaudePaths.Provider, Title: e.Name, Cwd: e.Cwd, Pid: e.Pid,
                            ProcStart: e.ProcStart, TranscriptPath: transcript, Version: e.Version),
                        $"created:{id}:pid:{e.Pid}:{e.ProcStart}", evidence: Evidence.Reported));
                    // A resumed session gets a new process: created only fills blanks, so update the link explicitly.
                    // Timestamped at the process start, so the registry's status for this process (always later) applies.
                    events.Add(ClaudeEventMapper.New(o, EventTypes.AgentUpdated,
                        new AgentUpdatedData(Cwd: e.Cwd, Pid: e.Pid, ProcStart: e.ProcStart, TranscriptPath: transcript), null,
                        evidence: Evidence.Observed));
                    _lastStatus.Remove(file);
                }
                else if (prev.Cwd != e.Cwd)
                    events.Add(ClaudeEventMapper.New(o with { Ts = now }, EventTypes.AgentUpdated, new AgentUpdatedData(Cwd: e.Cwd), null));

                if (alive)
                {
                    live++;
                    _ctx.LiveSessions[e.SessionId] = new LiveSession(e.Pid, e.ProcStart!, e.SessionId);
                    if (e.MappedStatus is { } st && _lastStatus.GetValueOrDefault(file) != e.Status)
                    {
                        _lastStatus[file] = e.Status!;
                        events.Add(ClaudeEventMapper.Status(o with { Ts = e.StatusUpdatedAt ?? now }, EventTypes.AgentStatusChanged,
                            st.Status, st.Reason));
                    }
                }
                _entries[file] = e;
            }

            foreach (var file in _entries.Keys.Except(current.Keys).ToList())
            {
                var gone = _entries[file];
                if (_proc.IsAlive(gone.Pid, gone.ProcStart)) continue; // entry rewritten/racing; decide next pass
                _entries.Remove(file);
                _lastStatus.Remove(file);
                // Resumed (`claude --resume`): the same session now lives in another process. The old
                // process's entry going away is not the end of the session.
                if (current.Values.Any(c => c.SessionId == gone.SessionId && _proc.IsAlive(c.Pid, c.ProcStart))) continue;
                var id = ClaudePaths.MainId(gone.SessionId);
                events.Add(ClaudeEventMapper.Status(new Origin(EventSources.Registry, gone.SessionId, id, now, gone.Cwd),
                    EventTypes.AgentCompleted, AgentStatus.Completed, "Session exited (registry entry removed)",
                    $"end:{id}:{gone.ProcStart}"));
                _ctx.LiveSessions.TryRemove(gone.SessionId, out _);
            }

            events.AddRange(SweepStale(now));
            foreach (var ev in events) await _sink.PublishAsync(ev, ct);

            _health.SetCounter(HealthName, "sessions", current.Count);
            _health.SetCounter(HealthName, "live", live);
            _health.Increment(HealthName, "events", events.Count);
            _health.Ok(HealthName, $"{live} live session(s)");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// "running requires live evidence": main sessions (and their sub-agents) that are not in
    /// the live registry, have no live process and showed no activity recently become unknown —
    /// typically backfilled history whose process is long gone without an end event.
    /// </summary>
    public List<AgentEvent> SweepStale(DateTimeOffset now)
    {
        if (!_ctx.TranscriptBackfillDone) return [];
        var stale = _state.Read(s => s.Agents.Values
            .Where(a => a.Provider == ClaudePaths.Provider && a.Kind == AgentKind.Main && a.SessionId is not null
                        && !AgentStateMachine.IsTerminal(a.Status) && a.Status != AgentStatus.Unknown
                        && !_ctx.LiveSessions.ContainsKey(a.SessionId) && now - a.LastActivityAt > StaleAfter
                        && !_proc.IsAlive(a.Pid, a.ProcStart))
            .Select(a => (a.Id, a.SessionId!, a.Pid, Children: Descendants(s, a)))
            .ToList());
        var list = new List<AgentEvent>();
        foreach (var (id, sid, pid, children) in stale)
        {
            var o = new Origin(EventSources.Registry, sid, id, now);
            var reason = pid is null
                ? "No live Claude Code process for this session (historical transcript, no end event)"
                : $"Process {pid} is gone and the session left no end event";
            list.Add(ClaudeEventMapper.Status(o, EventTypes.AgentStatusChanged, AgentStatus.Unknown, reason, evidence: Evidence.Observed));
            foreach (var c in children)
                list.Add(ClaudeEventMapper.Status(o, EventTypes.AgentStatusChanged, AgentStatus.Unknown,
                    "Parent session has no live process", agentId: c, evidence: Evidence.Observed));
        }
        return list;
    }

    private static List<string> Descendants(Core.State.WorldState s, Agent root)
    {
        var result = new List<string>();
        var stack = new Stack<string>(root.ChildIds);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!s.Agents.TryGetValue(id, out var c) || result.Contains(id)) continue;
            if (!AgentStateMachine.IsTerminal(c.Status) && c.Status != AgentStatus.Unknown) result.Add(id);
            foreach (var g in c.ChildIds) stack.Push(g);
        }
        return result;
    }
}
