using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Pipeline;

namespace Amc.Daemon.Providers.Claude;

/// <summary>A tool call seen at start (from a hook or a transcript), kept so the end can be mapped.</summary>
/// <remarks><see cref="Input"/> is a compacted copy (<see cref="ClaudePayloadTrim.CompactInput"/>), never the full input.</remarks>
public sealed record ToolUseInfo(
    string ToolUseId, string Name, JsonObject Input, string AgentId, string SessionId, DateTimeOffset StartedAt,
    WorktreeHit? Hit, bool HitInferred, string? Cwd)
{
    /// <summary>Wall-clock time the start was remembered (<see cref="StartedAt"/> is the reported time, old during backfill).</summary>
    public DateTimeOffset SeenAt { get; init; }
    /// <summary>Wall-clock time the first end was mapped; the entry is dropped <see cref="ClaudeContext.ToolEndGrace"/> later.</summary>
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>An Agent/Task tool call: the parent that spawned it and what it asked for.</summary>
public sealed record SpawnRequest(string ToolUseId, string SessionId, string ParentAgentId, string? SubagentType, string? Description, DateTimeOffset At)
{
    public string? ChildAgentId { get; set; }
}

/// <summary>The end of a spawn tool call seen before its child agent was known.</summary>
public sealed record PendingChildEnd(string SessionId, string? ChildAgentId, string? SpawnToolUseId, AgentStatus Status, string Reason, DateTimeOffset At, string Source);

public sealed record PendingReply(string MessageId, DateTimeOffset DeliveredAt);

public sealed record LiveSession(int Pid, string ProcStart, string SessionId);

/// <summary>
/// In-memory correlation shared by the hook receiver and the transcript tailer, so a fact
/// reported by both maps to the same agent ids and dedupe keys. Everything here is
/// reconstructible (restart = cold cache, worst case a missing link, never a wrong fact).
/// </summary>
public sealed class ClaudeContext(StateStore state, TimeProvider? time = null)
{
    /// <summary>Upper bound on remembered tool starts. Ended calls leave after <see cref="ToolEndGrace"/>, so this only
    /// bites when many calls never report an end (killed sessions); the oldest are evicted first.</summary>
    public const int MaxToolUses = 5_000;
    /// <summary>How long an ended tool call stays, so the second source's end (hook vs transcript) maps the same way.</summary>
    public static readonly TimeSpan ToolEndGrace = TimeSpan.FromMinutes(5);
    /// <summary>A tool start with no end for this long is forgotten even while its session lives.</summary>
    public static readonly TimeSpan ToolStartMaxAge = TimeSpan.FromHours(24);
    /// <summary>A prompt dedupe entry older than this no longer pairs the hook and transcript reports of one prompt.</summary>
    public static readonly TimeSpan PromptKeyMaxAge = TimeSpan.FromHours(6);
    /// <summary>A terminal agent idle for this long is treated as gone by <see cref="Sweep"/>.</summary>
    public static readonly TimeSpan EndedAgentRetention = TimeSpan.FromHours(6);
    private const int ToolSweepEvery = 256;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ConcurrentDictionary<string, ToolUseInfo> ToolUses { get; } = new();
    public ConcurrentDictionary<string, SpawnRequest> Spawns { get; } = new();
    /// <summary>Ends of sub-agents reported by their parent (spawn tool result, task notification, handback), keyed by spawn tool id or "child:&lt;id&gt;".</summary>
    public ConcurrentDictionary<string, PendingChildEnd> PendingChildEnds { get; } = new();
    /// <summary>Child agent ids whose agent.spawned (with a resolved parent) was published.</summary>
    public ConcurrentDictionary<string, byte> SpawnResolved { get; } = new();
    public ConcurrentDictionary<string, List<PendingReply>> PendingReplies { get; } = new();
    /// <summary>sessionId → live registry entry (maintained by the registry collector).</summary>
    public ConcurrentDictionary<string, LiveSession> LiveSessions { get; } = new();

    private readonly ConcurrentDictionary<string, byte> _announced = new();
    private readonly Dictionary<string, PromptEntry> _promptKeys = new();
    private readonly ConcurrentDictionary<string, string> _lastValues = new();
    /// <summary>Tool ends seen before their start (wall clock), so a late start is remembered as already ended.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _endedEarly = new();
    private readonly TaskTracker _tasks = new();
    private int _toolAdds;
    private HashSet<string> _goneLastSweep = [];

    public TaskTracker Tasks => _tasks;

    /// <summary>Set once the transcript tailer finished its first full pass (stale-session sweeps wait for it).</summary>
    public volatile bool TranscriptBackfillDone;

    /// <summary>True if the agent exists in state or an event creating it was already published.</summary>
    public bool IsKnown(string agentId) => _announced.ContainsKey(agentId) || state.Read(s => s.Agents.ContainsKey(agentId));

    public void MarkAnnounced(string agentId) => _announced[agentId] = 1;

    /// <summary>Returns true when <paramref name="value"/> differs from the last value recorded for (agent, field).</summary>
    public bool Changed(string agentId, string field, string? value)
    {
        if (value is null) return false;
        var key = agentId + "|" + field;
        if (_lastValues.TryGetValue(key, out var old) && old == value) return false;
        _lastValues[key] = value;
        return true;
    }

    /// <summary>Remembers a tool start so its end can be mapped. The input is compacted (see <see cref="ClaudePayloadTrim"/>).</summary>
    public void RememberToolUse(ToolUseInfo info)
    {
        var now = _time.GetUtcNow();
        info = info with { Input = ClaudePayloadTrim.CompactInput(info.Input), SeenAt = now };
        if (_endedEarly.TryRemove(info.ToolUseId, out var endedAt)) info.EndedAt = endedAt;
        if (Interlocked.Increment(ref _toolAdds) % ToolSweepEvery == 0 || ToolUses.Count >= MaxToolUses) SweepToolUses(now);
        ToolUses.TryAdd(info.ToolUseId, info);
    }

    /// <summary>Marks a tool call ended; it is forgotten after <see cref="ToolEndGrace"/> (a second source may still report the end).</summary>
    public void ToolEnded(string toolUseId)
    {
        var now = _time.GetUtcNow();
        if (ToolUses.TryGetValue(toolUseId, out var info)) info.EndedAt ??= now;
        else _endedEarly.TryAdd(toolUseId, now);
    }

    /// <summary>Drops ended calls past the grace and stale starts; above the cap, evicts the oldest (ended first) down to 3/4.</summary>
    public void SweepToolUses(DateTimeOffset now)
    {
        foreach (var (id, info) in ToolUses)
            if (info.EndedAt is { } e ? now - e > ToolEndGrace : now - info.SeenAt > ToolStartMaxAge)
                ToolUses.TryRemove(id, out _);
        foreach (var (id, at) in _endedEarly)
            if (now - at > ToolEndGrace) _endedEarly.TryRemove(id, out _);
        if (ToolUses.Count >= MaxToolUses)
            foreach (var kv in ToolUses.OrderBy(kv => kv.Value.EndedAt is null).ThenBy(kv => kv.Value.SeenAt)
                         .Take(ToolUses.Count - MaxToolUses * 3 / 4).ToList())
                ToolUses.TryRemove(kv.Key, out _);
        if (_endedEarly.Count > MaxToolUses)
            foreach (var kv in _endedEarly.OrderBy(kv => kv.Value).Take(_endedEarly.Count - MaxToolUses / 2).ToList())
                _endedEarly.TryRemove(kv.Key, out _);
    }

    /// <summary>
    /// Dedupe key for a main-agent prompt. Uses the prompt id when known; a hook without a
    /// prompt id and the transcript record of the same prompt meet through the text hash.
    /// The same text submitted twice gets a fresh key once the same source reports it again.
    /// </summary>
    public string PromptKey(string sessionId, string? promptId, string text, string source)
    {
        var hash = Hash(text.Trim());
        var corr = sessionId + "|" + hash;
        var candidate = promptId is { Length: > 0 } ? $"prompt:{sessionId}:{promptId}" : $"prompt:{sessionId}:t:{hash}:{Ulid.NewId()}";
        lock (_promptKeys)
        {
            if (_promptKeys.TryGetValue(corr, out var entry) && !entry.Sources.Contains(source)
                && (promptId is not { Length: > 0 } || entry.PromptId is null || entry.PromptId == promptId))
            {
                entry.Sources.Add(source);
                return entry.Key;
            }
            _promptKeys[corr] = new PromptEntry(candidate, promptId is { Length: > 0 } ? promptId : null, [source], _time.GetUtcNow());
            return candidate;
        }
    }

    private sealed record PromptEntry(string Key, string? PromptId, HashSet<string> Sources, DateTimeOffset At);

    /// <summary>Dedupe key for the end of an agent. Main agents include the process start so a resumed session can end again.</summary>
    public string EndKey(string agentId, string sessionId)
    {
        if (ClaudePaths.IsSubId(agentId, sessionId)) return $"end:{agentId}";
        return LiveSessions.TryGetValue(sessionId, out var live) ? $"end:{agentId}:{live.ProcStart}" : $"end:{agentId}";
    }

    public void AddPendingReply(string agentId, PendingReply r)
    {
        while (true)
        {
            var l = PendingReplies.GetOrAdd(agentId, _ => []);
            lock (l)
            {
                // The sweep removes an empty list under its lock; never add to a list that is no longer in the map.
                if (!PendingReplies.TryGetValue(agentId, out var cur) || !ReferenceEquals(cur, l)) continue;
                l.Add(r);
                return;
            }
        }
    }

    /// <summary>Takes the pending replies delivered at or before <paramref name="at"/>.</summary>
    public List<PendingReply> TakeReplies(string agentId, DateTimeOffset at)
    {
        if (!PendingReplies.TryGetValue(agentId, out var l)) return [];
        lock (l)
        {
            var due = l.Where(r => r.DeliveredAt <= at.AddSeconds(2)).ToList();
            foreach (var r in due) l.Remove(r);
            return due;
        }
    }

    /// <summary>Finds the oldest unassigned spawn request in a session matching the agent type (hook SubagentStart has no tool_use_id).</summary>
    public SpawnRequest? ClaimSpawn(string sessionId, string? agentType, string childAgentId)
    {
        var existing = Spawns.Values.FirstOrDefault(s => s.ChildAgentId == childAgentId);
        if (existing is not null) return existing;
        lock (Spawns)
        {
            var match = Spawns.Values
                .Where(s => s.SessionId == sessionId && s.ChildAgentId is null &&
                            (agentType is null || string.Equals(s.SubagentType ?? "general-purpose", agentType, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s.At).FirstOrDefault();
            if (match is not null) match.ChildAgentId = childAgentId;
            return match;
        }
    }

    public static string Hash(string s) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    /// <summary>Sizes of the per-session maps (health counters and tests).</summary>
    public IReadOnlyDictionary<string, int> Counts() => new Dictionary<string, int>
    {
        ["tool_uses"] = ToolUses.Count, ["spawns"] = Spawns.Count, ["pending_child_ends"] = PendingChildEnds.Count,
        ["spawn_resolved"] = SpawnResolved.Count, ["pending_replies"] = PendingReplies.Count, ["announced"] = _announced.Count,
        ["last_values"] = _lastValues.Count, ["prompt_keys"] = PromptKeyCount, ["task_lists"] = _tasks.AgentCount,
    };

    private int PromptKeyCount { get { lock (_promptKeys) return _promptKeys.Count; } }

    /// <summary>
    /// Periodic cleanup (see <see cref="ClaudeContextSweeper"/>): forgets everything kept for agents and sessions that are
    /// gone. An agent is gone when it is terminal and idle for <see cref="EndedAgentRetention"/>, or absent from state on
    /// two consecutive sweeps (so an agent whose creating event is still in flight is never touched). A session is gone
    /// when its main agent is gone and the registry does not list it as live. All of this is a cache: losing an entry
    /// costs at worst a duplicate (deduped) event or a missing link, never a wrong fact.
    /// </summary>
    public int Sweep(DateTimeOffset now)
    {
        SweepToolUses(now);
        var agentIds = new HashSet<string>(_announced.Keys);
        foreach (var k in _lastValues.Keys) agentIds.Add(k[..k.IndexOf('|')]);
        foreach (var k in PendingReplies.Keys) agentIds.Add(k);
        foreach (var k in _tasks.AgentIds) agentIds.Add(k);
        foreach (var k in SpawnResolved.Keys) agentIds.Add(k);
        var sessions = new HashSet<string>(Spawns.Values.Select(s => s.SessionId));
        foreach (var e in PendingChildEnds.Values) sessions.Add(e.SessionId);
        foreach (var t in ToolUses.Values) sessions.Add(t.SessionId);
        lock (_promptKeys) foreach (var k in _promptKeys.Keys) sessions.Add(k[..k.IndexOf('|')]);
        foreach (var sid in sessions) agentIds.Add(ClaudePaths.MainId(sid));

        var (absent, ended) = state.Read(s =>
        {
            var a = new HashSet<string>(); var e = new HashSet<string>();
            foreach (var id in agentIds)
            {
                if (!s.Agents.TryGetValue(id, out var ag)) a.Add(id);
                else if (ag.IsTerminal && now - (ag.CompletedAt is { } c && c > ag.LastActivityAt ? c : ag.LastActivityAt) > EndedAgentRetention) e.Add(id);
            }
            return (a, e);
        });
        var gone = new HashSet<string>(ended);
        foreach (var id in absent) if (_goneLastSweep.Contains(id)) gone.Add(id);
        _goneLastSweep = absent;
        var goneSessions = sessions.Where(sid => gone.Contains(ClaudePaths.MainId(sid)) && !LiveSessions.ContainsKey(sid)).ToHashSet();

        var removed = 0;
        foreach (var id in gone)
        {
            if (_announced.TryRemove(id, out _)) removed++;
            if (SpawnResolved.TryRemove(id, out _)) removed++;
            if (PendingReplies.TryRemove(id, out _)) removed++;
            removed += _tasks.Forget(id);
        }
        foreach (var k in _lastValues.Keys)
            if (gone.Contains(k[..k.IndexOf('|')]) && _lastValues.TryRemove(k, out _)) removed++;
        // Announced agents that state already holds need no marker (IsKnown also reads state).
        var inState = state.Read(s => _announced.Keys.Where(s.Agents.ContainsKey).ToList());
        foreach (var id in inState) if (_announced.TryRemove(id, out _)) removed++;
        foreach (var (id, l) in PendingReplies)
            lock (l) if (l.Count == 0 && PendingReplies.TryRemove(new KeyValuePair<string, List<PendingReply>>(id, l))) removed++;
        foreach (var (id, sp) in Spawns)
            if (goneSessions.Contains(sp.SessionId) && Spawns.TryRemove(id, out _)) removed++;
        foreach (var (id, e) in PendingChildEnds)
            if (goneSessions.Contains(e.SessionId) && PendingChildEnds.TryRemove(id, out _)) removed++;
        foreach (var (id, t) in ToolUses)
            if (goneSessions.Contains(t.SessionId) && ToolUses.TryRemove(id, out _)) removed++;
        lock (_promptKeys)
            foreach (var (k, e) in _promptKeys.ToList())
                if ((now - e.At > PromptKeyMaxAge || goneSessions.Contains(k[..k.IndexOf('|')])) && _promptKeys.Remove(k)) removed++;
        return removed;
    }

    /// <summary>True when <see cref="Sweep"/> would treat the agent as gone right now (terminal and idle, or absent from state).</summary>
    public bool IsGone(string agentId, DateTimeOffset now) => state.Read(s =>
        !s.Agents.TryGetValue(agentId, out var a) ||
        (a.IsTerminal && now - (a.CompletedAt is { } c && c > a.LastActivityAt ? c : a.LastActivityAt) > EndedAgentRetention));
}

/// <summary>
/// Accumulates TaskCreate/TaskUpdate-style tool calls into a full task list per agent
/// (task.updated carries a snapshot). Idempotent per tool_use id so hooks and transcripts
/// can both feed it.
/// </summary>
public sealed class TaskTracker
{
    private readonly ConcurrentDictionary<string, List<TaskItemData>> _lists = new();
    /// <summary>tool_use id → agent id, so an agent's entries can be forgotten with it.</summary>
    private readonly ConcurrentDictionary<string, string> _applied = new();

    public int AgentCount => _lists.Count;
    public IEnumerable<string> AgentIds => _lists.Keys;

    /// <summary>Forgets an agent's list and applied tool ids (the agent is gone). Returns the number of entries removed.</summary>
    public int Forget(string agentId)
    {
        var n = _lists.TryRemove(agentId, out _) ? 1 : 0;
        foreach (var (id, a) in _applied)
            if (a == agentId && _applied.TryRemove(id, out _)) n++;
        return n;
    }

    public IReadOnlyList<TaskItemData>? Apply(string agentId, string toolUseId, string tool, JsonObject input, JsonNode? result)
    {
        if (!_applied.TryAdd(toolUseId, agentId)) return null;
        var list = _lists.GetOrAdd(agentId, _ => []);
        lock (list)
        {
            var isCreate = tool.Contains("Create", StringComparison.OrdinalIgnoreCase);
            var id = Str(input, "taskId") ?? Str(input, "task_id") ?? Str(input, "id")
                     ?? Str(result, "taskId") ?? Str(result?["task"], "id") ?? Str(result, "id");
            var title = Str(input, "subject") ?? Str(input, "title") ?? Str(input, "content") ?? Str(input, "description");
            var status = Str(input, "status");
            var active = Str(input, "activeForm");
            if (isCreate)
            {
                id ??= (list.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                list.RemoveAll(t => t.Key == id);
                list.Add(new TaskItemData(id, title ?? "Task " + id, MapState(status) ?? TaskState.Pending, active));
            }
            else
            {
                if (id is null) return null;
                var idx = list.FindIndex(t => t.Key == id);
                if (status is "deleted") { if (idx >= 0) list.RemoveAt(idx); }
                else if (idx >= 0)
                {
                    var t = list[idx];
                    list[idx] = t with { Title = title ?? t.Title, State = MapState(status) ?? t.State, ActiveForm = active ?? t.ActiveForm };
                }
                else list.Add(new TaskItemData(id, title ?? "Task " + id, MapState(status) ?? TaskState.Pending, active));
            }
            return list.ToList();
        }
    }

    public static TaskState? MapState(string? s) => s?.ToLowerInvariant() switch
    {
        "pending" or "todo" or "open" => TaskState.Pending,
        "in_progress" or "in-progress" or "inprogress" or "active" => TaskState.InProgress,
        "completed" or "done" or "complete" => TaskState.Completed,
        "blocked" => TaskState.Blocked,
        "cancelled" or "canceled" => TaskState.Cancelled,
        _ => null,
    };

    private static string? Str(JsonNode? n, string key) =>
        n is JsonObject o && o[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;
}
