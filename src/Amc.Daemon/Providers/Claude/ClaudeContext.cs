using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Pipeline;

namespace Amc.Daemon.Providers.Claude;

/// <summary>A tool call seen at start (from a hook or a transcript), kept so the end can be mapped.</summary>
public sealed record ToolUseInfo(
    string ToolUseId, string Name, JsonObject Input, string AgentId, string SessionId, DateTimeOffset StartedAt,
    WorktreeHit? Hit, bool HitInferred, string? Cwd);

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
public sealed class ClaudeContext(StateStore state)
{
    private const int MaxToolUses = 50_000;

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
    private readonly ConcurrentDictionary<string, string> _ended = new();
    private readonly TaskTracker _tasks = new();

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

    public void RememberToolUse(ToolUseInfo info)
    {
        if (ToolUses.Count > MaxToolUses)
        {
            // Evict the oldest quarter in one pass (low-water mark), so the scan is amortized
            // instead of running on every add once the cap is reached.
            foreach (var kv in ToolUses.OrderBy(kv => kv.Value.StartedAt).Take(MaxToolUses / 4).ToList())
                ToolUses.TryRemove(kv.Key, out _);
        }
        ToolUses.TryAdd(info.ToolUseId, info);
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
            _promptKeys[corr] = new PromptEntry(candidate, promptId is { Length: > 0 } ? promptId : null, [source]);
            return candidate;
        }
    }

    private sealed record PromptEntry(string Key, string? PromptId, HashSet<string> Sources);

    /// <summary>Dedupe key for the end of an agent. Main agents include the process start so a resumed session can end again.</summary>
    public string EndKey(string agentId, string sessionId)
    {
        if (ClaudePaths.IsSubId(agentId, sessionId)) return $"end:{agentId}";
        return LiveSessions.TryGetValue(sessionId, out var live) ? $"end:{agentId}:{live.ProcStart}" : $"end:{agentId}";
    }

    public void AddPendingReply(string agentId, PendingReply r) =>
        PendingReplies.AddOrUpdate(agentId, _ => [r], (_, l) => { lock (l) l.Add(r); return l; });

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
}

/// <summary>
/// Accumulates TaskCreate/TaskUpdate-style tool calls into a full task list per agent
/// (task.updated carries a snapshot). Idempotent per tool_use id so hooks and transcripts
/// can both feed it.
/// </summary>
public sealed class TaskTracker
{
    private readonly ConcurrentDictionary<string, List<TaskItemData>> _lists = new();
    private readonly ConcurrentDictionary<string, byte> _applied = new();

    public IReadOnlyList<TaskItemData>? Apply(string agentId, string toolUseId, string tool, JsonObject input, JsonNode? result)
    {
        if (!_applied.TryAdd(toolUseId, 1)) return null;
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
