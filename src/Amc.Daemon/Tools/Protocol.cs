using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Providers;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;

namespace Amc.Daemon.Tools;

/// <summary>
/// Messages for agents that report through the open protocol. A tool (or a hook script) polls
/// POST /api/v1/inbox; while it does, the agent can be messaged from the dashboard.
/// </summary>
public sealed class ProtocolInbox(Mailbox mailbox, IEventSink sink, TimeProvider time)
{
    public const string Transport = "inbox";
    private static readonly TimeSpan ListeningWindow = TimeSpan.FromMinutes(3);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPoll = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters = new();

    public bool IsListening(string agentId) =>
        _waiters.ContainsKey(agentId) || (_lastPoll.TryGetValue(agentId, out var t) && time.GetUtcNow() - t < ListeningWindow);

    public void Enqueue(string agentId, OutgoingMessage m)
    {
        mailbox.Enqueue(new PendingMessage(m.MessageId, agentId, m.Text, time.GetUtcNow()));
        if (_waiters.TryRemove(agentId, out var w)) w.TrySetResult();
    }

    /// <summary>Returns pending messages, waiting up to <paramref name="wait"/> for one to arrive.</summary>
    /// <summary>Marks the agent reachable (it just called us through a channel that can deliver messages).</summary>
    public void Touch(string agentId) => _lastPoll[agentId] = time.GetUtcNow();

    public async Task<List<PendingMessage>> TakeAsync(string agentId, TimeSpan wait, CancellationToken ct, string? detail = null)
    {
        _lastPoll[agentId] = time.GetUtcNow();
        if (!mailbox.HasPending(agentId) && wait > TimeSpan.Zero)
        {
            var tcs = _waiters.GetOrAdd(agentId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            try { await tcs.Task.WaitAsync(wait, time, ct); } catch (TimeoutException) { } finally { _waiters.TryRemove(new(agentId, tcs)); }
        }
        _lastPoll[agentId] = time.GetUtcNow();
        var msgs = mailbox.DrainFor(agentId);
        foreach (var m in msgs)
            await sink.PublishAsync(new AgentEvent
            {
                Type = EventTypes.MessageDelivered, Source = EventSources.Hook, Evidence = Evidence.Observed, AgentId = agentId,
                DedupeKey = $"msg:{m.MessageId}:delivered",
                Data = Json.ToObject(new MessageStatusData(m.MessageId, Transport, detail ?? "Collected by the tool from the protocol inbox")),
            }, ct);
        return msgs;
    }
}

/// <summary>
/// Open protocol (v1) for any AI tool, wrapper or hook script to report what an agent does.
/// Maps reports onto Overhive's normal events (evidence: reported). See docs/PROTOCOL.md.
/// </summary>
public sealed partial class ProtocolReceiver(ToolRegistry tools, IEventSink sink, IWorktreeIndex worktrees, TimeProvider time)
{
    public sealed record Result(bool Ok, string? Error = null, string? AgentId = null);

    public static string? AgentIdFor(string? tool, string? id) =>
        tool is not null && id is not null && AgentTool.ValidId(tool) && IdPattern().IsMatch(id) ? $"{tool}:{id}" : null;

    public async Task<Result> ReportAsync(JsonObject body, CancellationToken ct)
    {
        var tool = Str(body, "tool");
        if (tool is null || !AgentTool.ValidId(tool)) return new(false, "tool: lowercase id like \"codex\" is required");
        if (tools.Get(tool) is { Adapter: not null } native && native.Adapter != "protocol")
            return new(false, $"{native.Name} has a native integration; report other tools here");
        if (body["agent"] is not JsonObject ag || AgentIdFor(tool, Str(ag, "id")) is not { } id)
            return new(false, "agent.id: 1–80 characters [A-Za-z0-9._-] is required");
        var parent = AgentIdFor(tool, Str(ag, "parent"));
        var kind = Str(body, "kind");
        var ts = DateTimeOffset.TryParse(Str(body, "ts"), out var t) ? t : time.GetUtcNow();
        var d = body["data"] as JsonObject ?? new JsonObject();
        var toolName = tools.Get(tool)?.Name ?? tool;

        AgentEvent E(string type, object payload, string? dedupe = null, string? wt = null, string? repo = null, string? file = null) => new()
        {
            Type = type, Source = EventSources.Hook, Evidence = Evidence.Reported, AgentId = id, Ts = ts, DedupeKey = dedupe,
            WorktreeId = wt, RepositoryId = repo, FilePath = file, Data = Json.ToObject(payload),
        };
        var events = new List<AgentEvent>
        {
            // Every report (re)announces the agent; the dedupe key makes this a no-op after the first.
            E(parent is null ? EventTypes.AgentCreated : EventTypes.AgentSpawned,
                new AgentCreatedData(parent is null ? AgentKind.Main : AgentKind.Sub, tool, ParentId: parent,
                    AgentType: Str(ag, "type") ?? toolName, Title: Str(ag, "title"), Cwd: Str(ag, "cwd"),
                    Pid: Num(ag["pid"]) is { } pid and > 0 and < int.MaxValue ? (int)pid : null, Model: Str(ag, "model")),
                parent is null ? $"created:{id}" : $"spawn:{id}"),
        };
        WorktreeHit? where = null;
        if (Str(ag, "cwd") is { } cwd && PathText.IsAbsolute(cwd)) where = await worktrees.ResolveAsync(cwd, ct);

        switch (kind)
        {
            case "start":
                events.Add(E(EventTypes.AgentStatusChanged, new AgentStatusData(AgentStatus.Running, Str(d, "reason") ?? "Started (reported)")));
                if (where is not null)
                    events.Add(E(EventTypes.AgentUpdated, new AgentUpdatedData(Cwd: Str(ag, "cwd")), wt: where.WorktreeId, repo: where.RepositoryId));
                break;
            case "status" when Enum.TryParse<AgentStatus>(Str(d, "status"), true, out var st):
                events.Add(E(EventTypes.AgentStatusChanged, new AgentStatusData(st, Str(d, "reason") ?? $"{st} (reported)")));
                break;
            case "prompt" when Str(d, "text") is { Length: > 0 } p:
                events.Add(E(EventTypes.AgentPrompt, new AgentTextData(p)));
                break;
            case "output" when Str(d, "text") is { Length: > 0 } o:
                events.Add(E(EventTypes.AgentOutput, new AgentTextData(o)));
                break;
            case "tool_start" when Str(d, "id") is { } tid && Str(d, "name") is { } name:
            {
                var (wt, repo, rel) = await Locate(Str(d, "path"), ct);
                // A shell command without a path runs in the agent's working directory.
                wt ??= where?.WorktreeId; repo ??= where?.RepositoryId;
                if (wt is not null) worktrees.NotifyActivity(new WorktreeActivity(wt, id, tid, Str(d, "command") is null ? ActivityKinds.Write : ActivityKinds.Shell, ActivityKinds.Start, ts));
                events.Add(Str(d, "command") is { } cmd
                    ? E(EventTypes.CommandStarted, new CommandData(tid, cmd, Str(d, "summary")), $"tool:{id}:{tid}:start", wt, repo)
                    : E(EventTypes.ToolStarted, new ToolCallData(tid, name, Str(d, "summary"), rel), $"tool:{id}:{tid}:start", wt, repo, rel));
                break;
            }
            case "tool_end" when Str(d, "id") is { } tid:
            {
                var (endWt, _, _) = await Locate(Str(d, "path"), ct);
                if ((endWt ?? where?.WorktreeId) is { } ew)
                    worktrees.NotifyActivity(new WorktreeActivity(ew, id, tid, Str(d, "command") is null ? ActivityKinds.Write : ActivityKinds.Shell, ActivityKinds.End, ts));
                var failed = Str(d, "error") is not null || (Num(d["exitCode"]) is { } ec && ec != 0);
                events.Add(Str(d, "command") is { } cmd
                    ? E(failed ? EventTypes.CommandFailed : EventTypes.CommandCompleted,
                        new CommandData(tid, cmd, ExitCode: Num(d["exitCode"]) is { } code ? (int)code : null,
                            Stdout: Str(d, "output"), Stderr: Str(d, "error")), $"tool:{id}:{tid}:end")
                    : E(failed ? EventTypes.ToolFailed : EventTypes.ToolCompleted,
                        new ToolCallData(tid, Str(d, "name") ?? "tool", Error: Str(d, "error"), Output: Str(d, "output")), $"tool:{id}:{tid}:end"));
                break;
            }
            case "file" when Str(d, "path") is { } path:
            {
                var (wt, repo, rel) = await Locate(path, ct);
                if (wt is null) return new(false, "data.path must be an absolute path inside a Git worktree", id);
                var type = Str(d, "change") switch { "created" => EventTypes.FileCreated, "deleted" => EventTypes.FileDeleted, _ => EventTypes.FileModified };
                worktrees.NotifyActivity(new WorktreeActivity(wt, id, "file:" + path, ActivityKinds.Write, ActivityKinds.End, ts));
                events.Add(E(type, new FileChangeData(Str(d, "toolId"), Str(d, "tool"), AbsolutePath: path), null, wt, repo, rel));
                break;
            }
            case "usage" when Str(d, "id") is { } mid:
            {
                long L(string k) => Num(d[k]) ?? 0;
                var u = new UsageData(mid, Str(d, "model"), L("input"), L("output"), L("cacheWrite"), L("cacheRead"));
                events.Add(E(EventTypes.AgentUsage, u, $"usage:{id}:{mid}:{u.Input}:{u.Output}:{u.CacheWrite}:{u.CacheRead}"));
                break;
            }
            case "end":
                var failedEnd = string.Equals(Str(d, "status"), "failed", StringComparison.OrdinalIgnoreCase);
                events.Add(E(failedEnd ? EventTypes.AgentFailed : EventTypes.AgentCompleted,
                    new AgentStatusData(failedEnd ? AgentStatus.Failed : AgentStatus.Completed, Str(d, "reason") ?? (failedEnd ? "Failed (reported)" : "Finished (reported)")),
                    $"end:{id}:{Ulid.NewId()}"));
                break;
            default:
                return new(false, "kind: one of start, status, prompt, output, tool_start, tool_end, file, usage, end (with its data)", id);
        }
        foreach (var e in events) await sink.PublishAsync(e, ct);
        return new(true, AgentId: id);
    }

    private async Task<(string? Wt, string? Repo, string? Rel)> Locate(string? path, CancellationToken ct)
    {
        if (!PathText.IsAbsolute(path)) return (null, null, null);
        var hit = await worktrees.ResolveAsync(path!, ct);
        return hit is null ? (null, null, null) : (hit.WorktreeId, hit.RepositoryId, hit.RelativePath);
    }

    /// <summary>Any JSON number (or numeric string) as a long; tolerant of how the value was produced.</summary>
    private static long? Num(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var dbl) ? (long)dbl
        : v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : null
        : null;

    private static string? Str(JsonNode? n, string k) => n?[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,80}$")]
    private static partial Regex IdPattern();
}
