using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Parsing;
using Amc.Core.Security;

namespace Amc.Daemon.Providers.Claude;

/// <summary>Where and when a fact was reported. <see cref="AgentId"/> is the acting agent.</summary>
public sealed record Origin(string Source, string SessionId, string AgentId, DateTimeOffset Ts, string? Cwd = null);

/// <summary>
/// Maps Claude Code facts (tool calls, prompts, outputs, spawns) to normalized events.
/// Shared by the hook receiver and the transcript tailer so both produce identical
/// dedupe keys (tool:&lt;id&gt;:start|end, file:&lt;id&gt;, test:&lt;id&gt;:start|end, spawn:&lt;agent&gt;, end:&lt;agent&gt;…).
/// </summary>
public sealed partial class ClaudeEventMapper(ClaudeContext ctx, IWorktreeIndex worktrees, TimeProvider time)
{
    public const int MaxToolOutput = 8 * 1024;
    /// <summary>Activity older than this is history (backfill): no NotifyActivity to the git collector.</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> WriteTools = ["Edit", "MultiEdit", "Write", "NotebookEdit"];
    private static readonly HashSet<string> ShellTools = ["Bash"];
    public static readonly HashSet<string> SpawnTools = ["Agent", "Task"];

    public ClaudeContext Context => ctx;

    // ---- envelope ------------------------------------------------------------------

    public static AgentEvent New<T>(Origin o, string type, T payload, string? key, string? agentId = null,
        Evidence evidence = Evidence.Reported, WorktreeHit? hit = null, string? filePath = null) where T : class => new()
    {
        Type = type, Source = o.Source, Evidence = evidence, Ts = o.Ts,
        AgentId = agentId ?? o.AgentId, SessionId = o.SessionId,
        WorktreeId = hit?.WorktreeId, RepositoryId = hit?.RepositoryId, FilePath = filePath,
        DedupeKey = key, Data = Json.ToObject(payload),
    };

    public static AgentEvent Status(Origin o, string type, AgentStatus status, string reason, string? key = null,
        string? agentId = null, Evidence evidence = Evidence.Reported) =>
        New(o, type, new AgentStatusData(status, reason), key, agentId, evidence);

    // ---- agents ----------------------------------------------------------------------

    /// <summary>Ensures the session's main agent exists before any of its events (reducer ignores events of unknown agents).</summary>
    public List<AgentEvent> EnsureMain(Origin o, string? transcriptPath = null, string? version = null)
    {
        var id = ClaudePaths.MainId(o.SessionId);
        if (ctx.IsKnown(id)) return [];
        ctx.MarkAnnounced(id);
        return [New(o, EventTypes.AgentCreated,
            new AgentCreatedData(AgentKind.Main, ClaudePaths.Provider, Cwd: o.Cwd, TranscriptPath: transcriptPath, Version: version),
            $"created:{id}:{o.Source}", id)];
    }

    /// <summary>agent.spawned for a sub-agent. A resolved parent uses the canonical key spawn:&lt;child&gt; (first source wins).</summary>
    public List<AgentEvent> Spawned(Origin o, string childAgentId, string? parentId, string? agentType, string? description,
        string? spawnToolUseId, string? transcriptPath, bool parentResolved)
    {
        var list = new List<AgentEvent>();
        if (parentId is not null && parentId == ClaudePaths.MainId(o.SessionId)) list.AddRange(EnsureMain(o));
        var key = parentResolved ? $"spawn:{childAgentId}" : $"spawn:{childAgentId}:{o.Source}:unresolved";
        if (parentResolved) ctx.SpawnResolved[childAgentId] = 1;
        ctx.MarkAnnounced(childAgentId);
        list.Add(New(o, EventTypes.AgentSpawned,
            new AgentCreatedData(AgentKind.Sub, ClaudePaths.Provider, parentResolved ? parentId : null, agentType,
                Title: description, Description: description, Cwd: o.Cwd, TranscriptPath: transcriptPath, SpawnToolUseId: spawnToolUseId),
            key, childAgentId));
        return list;
    }

    public AgentEvent ChildEnded(Origin o, string childAgentId, AgentStatus status, string reason) =>
        Status(o, status switch
            {
                AgentStatus.Completed => EventTypes.AgentCompleted,
                AgentStatus.Failed => EventTypes.AgentFailed,
                _ => EventTypes.AgentStatusChanged,
            }, status, reason,
            ctx.EndKey(childAgentId, o.SessionId), childAgentId);

    /// <summary>
    /// Records the end of a sub-agent as reported by its parent. Deferred: emitted by
    /// <see cref="FlushChildEnds"/> once the child exists and its own transcript has been read,
    /// so the end is folded after the child's last activity.
    /// </summary>
    public void DeferChildEnd(Origin o, string? spawnToolUseId, string? childAgentId, AgentStatus status, string reason)
    {
        var key = spawnToolUseId ?? "child:" + childAgentId;
        ctx.PendingChildEnds[key] = new PendingChildEnd(o.SessionId, childAgentId, spawnToolUseId, status, reason, o.Ts, o.Source);
    }

    /// <summary>Emits deferred child ends of a session whose child agent is known.</summary>
    public List<AgentEvent> FlushChildEnds(string sessionId)
    {
        var list = new List<AgentEvent>();
        foreach (var (key, end) in ctx.PendingChildEnds.Where(kv => kv.Value.SessionId == sessionId).ToList())
        {
            var child = end.ChildAgentId ??
                        (end.SpawnToolUseId is not null && ctx.Spawns.TryGetValue(end.SpawnToolUseId, out var sp) ? sp.ChildAgentId : null);
            if (child is null || !ctx.IsKnown(child)) continue;
            if (!ctx.PendingChildEnds.TryRemove(key, out _)) continue;
            list.Add(ChildEnded(new Origin(end.Source, sessionId, child, end.At), child, end.Status, end.Reason));
        }
        return list;
    }

    // ---- prompts / outputs -------------------------------------------------------------

    public AgentEvent Prompt(Origin o, string text, string key) => New(o, EventTypes.AgentPrompt, new AgentTextData(text), key);

    /// <summary>agent.output plus agent.message_received for messages delivered to this agent and not yet answered.</summary>
    public List<AgentEvent> Output(Origin o, string text, string? key)
    {
        var list = new List<AgentEvent> { New(o, EventTypes.AgentOutput, new AgentTextData(text), key) };
        list.AddRange(Replies(o, text));
        return list;
    }

    public IEnumerable<AgentEvent> Replies(Origin o, string text) =>
        ctx.TakeReplies(o.AgentId, o.Ts).Select(r =>
            New(o, EventTypes.MessageReceived, new MessageReplyData(r.MessageId, text), $"msg:{r.MessageId}:reply", evidence: Evidence.Inferred));

    // ---- tools -------------------------------------------------------------------------

    public async Task<List<AgentEvent>> ToolStartAsync(Origin o, string name, string toolUseId, JsonObject input, CancellationToken ct = default)
    {
        var list = new List<AgentEvent>();
        var (hit, inferred, abs) = await AttributeAsync(name, input, o.Cwd, ct);
        ctx.RememberToolUse(new ToolUseInfo(toolUseId, name, (JsonObject)input.DeepClone(), o.AgentId, o.SessionId, o.Ts, hit, inferred, o.Cwd));
        var evidence = inferred ? Evidence.Inferred : Evidence.Reported;
        var relOrAbs = hit?.RelativePath is { Length: > 0 } rel ? rel : abs;

        if (ShellTools.Contains(name))
        {
            var command = Str(input, "command") ?? "";
            var background = Bool(input, "run_in_background");
            list.Add(New(o, EventTypes.CommandStarted,
                new CommandData(toolUseId, command, Str(input, "description"), o.Cwd, Background: background),
                $"tool:{toolUseId}:start", evidence: evidence, hit: hit));
            if (!background && TestOutputParser.Detect(command) is { } test)
                list.Add(New(o, EventTypes.TestStarted, new TestRunData(toolUseId, test.Runner, command),
                    $"test:{toolUseId}:start", evidence: Evidence.Inferred, hit: hit));
        }
        else
        {
            list.Add(New(o, EventTypes.ToolStarted, new ToolCallData(toolUseId, name, Summary(name, input), relOrAbs),
                $"tool:{toolUseId}:start", evidence: evidence, hit: hit, filePath: hit is null ? abs : relOrAbs));
        }

        if (SpawnTools.Contains(name))
            ctx.Spawns.TryAdd(toolUseId, new SpawnRequest(toolUseId, o.SessionId, o.AgentId, Str(input, "subagent_type"), Str(input, "description"), o.Ts));

        if (name == "TodoWrite" && input["todos"] is JsonArray todos)
            list.Add(New(o, EventTypes.TasksUpdated, new TaskListData(Todos(todos), name), $"tasks:{toolUseId}"));

        Notify(o, hit, toolUseId, name, ActivityKinds.Start);
        return list;
    }

    /// <param name="result">Structured result (transcript toolUseResult / hook tool_response).</param>
    /// <param name="resultText">Text the model saw (tool_result content), used for errors and as output fallback.</param>
    public async Task<List<AgentEvent>> ToolEndAsync(Origin o, string? name, string toolUseId, JsonObject? input,
        JsonNode? result, string? resultText, bool isError, CancellationToken ct = default)
    {
        var list = new List<AgentEvent>();
        ctx.ToolUses.TryGetValue(toolUseId, out var info);
        name ??= info?.Name ?? (result is JsonObject r0 && r0.ContainsKey("stdout") ? "Bash" : "unknown");
        input ??= info?.Input ?? new JsonObject();
        WorktreeHit? hit; bool inferred; string? abs;
        if (info is not null) { hit = info.Hit; inferred = info.HitInferred; abs = AbsPath(input, info.Cwd ?? o.Cwd); }
        else (hit, inferred, abs) = await AttributeAsync(name, input, o.Cwd, ct);
        var evidence = inferred ? Evidence.Inferred : Evidence.Reported;
        long? duration = info is null ? null : (long)Math.Max(0, (o.Ts - info.StartedAt).TotalMilliseconds);
        var relOrAbs = hit?.RelativePath is { Length: > 0 } rel ? rel : abs;
        var text = resultText ?? (result is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : null);

        if (ShellTools.Contains(name))
        {
            var command = Str(input, "command") ?? "";
            var ro = result as JsonObject;
            var stdout = Str(ro, "stdout");
            var stderr = Str(ro, "stderr");
            if (stdout is null && stderr is null) { if (isError) stderr = text; else stdout = text; }
            var interrupted = Bool(ro, "interrupted");
            var background = Bool(input, "run_in_background") || Str(ro, "backgroundTaskId") is not null;
            int? exit = isError ? (ExitCode().Match(text ?? stderr ?? "") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null)
                                : background ? null : 0;
            list.Add(New(o, isError ? EventTypes.CommandFailed : EventTypes.CommandCompleted,
                new CommandData(toolUseId, command, Str(input, "description"), info?.Cwd ?? o.Cwd, exit,
                    Redactor.RedactAndCap(stdout, MaxToolOutput), Redactor.RedactAndCap(stderr, MaxToolOutput), duration, interrupted, background),
                $"tool:{toolUseId}:end", evidence: evidence, hit: hit));
            if (!background && TestOutputParser.Detect(command) is { } test)
            {
                var summary = TestOutputParser.Parse((stdout ?? "") + "\n" + (stderr ?? ""));
                var failed = summary.Failed is > 0 || (summary.Failed is null && isError) || (summary.Passed is null && isError);
                list.Add(New(o, failed ? EventTypes.TestFailed : EventTypes.TestPassed,
                    new TestRunData(toolUseId, test.Runner, command, summary.Passed, summary.Failed, summary.Skipped, duration, summary.FailedTests),
                    $"test:{toolUseId}:end", evidence: Evidence.Inferred, hit: hit));
            }
        }
        else
        {
            var output = text ?? (result is JsonObject ? null : result?.ToJsonString());
            list.Add(New(o, isError ? EventTypes.ToolFailed : EventTypes.ToolCompleted,
                new ToolCallData(toolUseId, name, Summary(name, input), relOrAbs,
                    isError ? Redactor.RedactAndCap(text ?? "Tool call failed", 2048) : null, duration,
                    isError ? null : Redactor.RedactAndCap(output, MaxToolOutput)),
                $"tool:{toolUseId}:end", evidence: evidence, hit: hit, filePath: hit is null ? abs : relOrAbs));
        }

        if (!isError) list.AddRange(FileEvents(o, name, toolUseId, input, result as JsonObject, hit, abs, relOrAbs));

        if (SpawnTools.Contains(name))
        {
            var ro = result as JsonObject;
            var status = Str(ro, "status");
            var child = Str(ro, "agentId") is { } aid ? ClaudePaths.SubId(o.SessionId, aid) : null;
            if (child is not null && ctx.Spawns.TryGetValue(toolUseId, out var sp)) sp.ChildAgentId ??= child;
            if (isError)
                DeferChildEnd(o, toolUseId, child, AgentStatus.Failed, "Sub-agent failed: " + Short(text));
            else if (status is not ("async_launched" or "launched" or "running"))
                DeferChildEnd(o, toolUseId, child, AgentStatus.Completed, "Sub-agent finished; result returned to parent");
            if (o.Source == EventSources.Hook) list.AddRange(FlushChildEnds(o.SessionId));
        }

        if (name is "TaskCreate" or "TaskUpdate" or "TaskDelete" && !isError &&
            ctx.Tasks.Apply(o.AgentId, toolUseId, name, input, result) is { } tasks)
            list.Add(New(o, EventTypes.TasksUpdated, new TaskListData(tasks, name), $"tasks:{toolUseId}"));

        if (name == "SubagentHandback" && !isError && ClaudePaths.IsSubId(o.AgentId, o.SessionId))
            list.Add(Status(o, EventTypes.AgentCompleted, AgentStatus.Completed, "Handed back to parent", ctx.EndKey(o.AgentId, o.SessionId)));

        Notify(o, hit, toolUseId, name, ActivityKinds.End);
        return list;
    }

    private static string Short(string? text) => Core.State.Reducer.Truncate((text ?? "").Trim(), 200);

    private IEnumerable<AgentEvent> FileEvents(Origin o, string name, string toolUseId, JsonObject input, JsonObject? result,
        WorktreeHit? hit, string? abs, string? relOrAbs)
    {
        if (abs is null) yield break;
        var filePath = hit is null ? abs : relOrAbs;
        if (name == "Read")
        {
            yield return New(o, EventTypes.FileRead, new FileChangeData(toolUseId, name, AbsolutePath: abs), $"file:{toolUseId}", hit: hit, filePath: filePath);
            yield break;
        }
        if (!WriteTools.Contains(name)) yield break;
        var created = name == "Write" && Str(result, "type") == "create";
        var (patch, added, removed) = RenderPatch(result?["structuredPatch"] as JsonArray);
        if (created && patch is null && Str(input, "content") is { } content)
        {
            added = content.Length == 0 ? 0 : content.Split('\n').Length - (content.EndsWith('\n') ? 1 : 0);
            removed = 0;
        }
        yield return New(o, created ? EventTypes.FileCreated : EventTypes.FileModified,
            new FileChangeData(toolUseId, name, null, added, removed, patch is null ? null : Redactor.RedactAndCap(patch), abs),
            $"file:{toolUseId}", hit: hit, filePath: filePath);
    }

    /// <summary>Renders Claude Code's structuredPatch (hunks with oldStart/oldLines/newStart/newLines/lines) as unified hunks.</summary>
    public static (string? Patch, int? Added, int? Removed) RenderPatch(JsonArray? hunks)
    {
        if (hunks is null || hunks.Count == 0) return (null, null, null);
        var sb = new StringBuilder();
        int added = 0, removed = 0;
        foreach (var h in hunks.OfType<JsonObject>())
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"@@ -{Int(h, "oldStart")},{Int(h, "oldLines")} +{Int(h, "newStart")},{Int(h, "newLines")} @@\n");
            foreach (var l in (h["lines"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? ""))
            {
                if (l.StartsWith('+')) added++;
                else if (l.StartsWith('-')) removed++;
                sb.Append(l).Append('\n');
            }
        }
        return (sb.ToString(), added, removed);
    }

    private static IReadOnlyList<TaskItemData> Todos(JsonArray todos) =>
        todos.OfType<JsonObject>().Select((t, i) =>
        {
            var content = Str(t, "content") ?? Str(t, "subject") ?? $"Todo {i + 1}";
            var key = Str(t, "id") ?? ClaudeContext.Hash(content)[..10];
            return new TaskItemData(key, content, TaskTracker.MapState(Str(t, "status")) ?? TaskState.Pending, Str(t, "activeForm"));
        }).ToList();

    private void Notify(Origin o, WorktreeHit? hit, string toolUseId, string name, string phase)
    {
        if (hit is null || time.GetUtcNow() - o.Ts > LiveWindow) return;
        var kind = WriteTools.Contains(name) ? ActivityKinds.Write : ShellTools.Contains(name) ? ActivityKinds.Shell : ActivityKinds.Read;
        try { worktrees.NotifyActivity(new WorktreeActivity(hit.WorktreeId, o.AgentId, toolUseId, kind, phase, o.Ts)); }
        catch (Exception) { /* attribution is best effort; never break ingestion */ }
    }

    // ---- attribution -------------------------------------------------------------------

    /// <summary>
    /// Resolves the worktree a tool acts in. File tools: their path input (reported).
    /// Bash: `cd &lt;dir&gt;`, `git -C &lt;dir&gt;`, absolute paths, else cwd (inferred).
    /// </summary>
    public async Task<(WorktreeHit? Hit, bool Inferred, string? AbsPath)> AttributeAsync(string name, JsonObject input, string? cwd, CancellationToken ct)
    {
        if (ShellTools.Contains(name))
        {
            foreach (var candidate in BashPathCandidates(Str(input, "command") ?? "", cwd))
                if (await Resolve(candidate, ct) is { } h) return (h, true, null);
            return (null, true, null);
        }
        var abs = AbsPath(input, cwd);
        if (abs is null) return (null, false, null);
        return (await Resolve(abs, ct), false, abs);
    }

    private async Task<WorktreeHit?> Resolve(string path, CancellationToken ct)
    {
        try { return await worktrees.ResolveAsync(path, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public static string? AbsPath(JsonObject input, string? cwd)
    {
        var p = Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path");
        return p is null ? null : Absolute(p, cwd);
    }

    private static string? Absolute(string p, string? cwd)
    {
        p = p.Trim();
        if (p.Length == 0) return null;
        if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal)) p = Path.Combine(Infrastructure.AmcOptions.Home, p.TrimStart('~').TrimStart('/'));
        if (!Path.IsPathRooted(p)) { if (cwd is null) return null; p = Path.Combine(cwd, p); }
        try { return Path.GetFullPath(p); } catch (Exception) { return null; }
    }

    public static IEnumerable<string> BashPathCandidates(string command, string? cwd)
    {
        var seen = new HashSet<string>();
        var dir = cwd;
        foreach (Match m in CdOrGitC().Matches(command))
        {
            var raw = m.Groups["q1"].Success ? m.Groups["q1"].Value : m.Groups["q2"].Success ? m.Groups["q2"].Value : m.Groups["w"].Value;
            if (Absolute(raw, dir) is { } a && seen.Add(a)) { yield return a; if (m.Groups["op"].Value == "cd") dir = a; }
        }
        foreach (Match m in AbsolutePathToken().Matches(command))
        {
            var p = m.Groups["p"].Value.TrimEnd('.', ',', ':');
            if (IgnoredPrefix(p)) continue;
            if (Absolute(p, null) is { } a && seen.Add(a)) yield return a;
            if (seen.Count > 12) yield break;
        }
        if (cwd is not null && seen.Add(cwd)) yield return cwd;
    }

    private static bool IgnoredPrefix(string p) =>
        p is "/" || p.StartsWith("/dev/", StringComparison.Ordinal) || p.StartsWith("/proc/", StringComparison.Ordinal) ||
        p.StartsWith("/usr/", StringComparison.Ordinal) || p.StartsWith("/bin/", StringComparison.Ordinal) ||
        p.StartsWith("/etc/", StringComparison.Ordinal) || p.StartsWith("/sys/", StringComparison.Ordinal);

    [GeneratedRegex(@"(?:^|[;&|(\s])(?<op>cd|git\s+-C)\s+(?:""(?<q1>[^""]+)""|'(?<q2>[^']+)'|(?<w>[^\s;&|)]+))")]
    private static partial Regex CdOrGitC();
    [GeneratedRegex(@"(?:^|[\s=:'""(])(?<p>(?:/|~/)[A-Za-z0-9._@%+\-/~]+)")]
    private static partial Regex AbsolutePathToken();
    [GeneratedRegex(@"Exit code (\d+)")]
    private static partial Regex ExitCode();

    // ---- helpers -----------------------------------------------------------------------

    public static string Summary(string name, JsonObject input)
    {
        string? s = name switch
        {
            "Read" or "Write" or "Edit" or "MultiEdit" => Str(input, "file_path"),
            "NotebookEdit" => Str(input, "notebook_path"),
            "Glob" => Str(input, "pattern"),
            "Grep" => Str(input, "pattern") is { } pat ? $"/{pat}/" + (Str(input, "path") is { } gp ? " in " + gp : "") : null,
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            "Agent" or "Task" => Str(input, "description") is { } d ? $"{d} ({Str(input, "subagent_type") ?? "general-purpose"})" : null,
            "TodoWrite" => input["todos"] is JsonArray t ? $"{t.Count} todo(s)" : null,
            "TaskCreate" or "TaskUpdate" => Str(input, "subject") ?? Str(input, "title") ?? Str(input, "taskId"),
            "SendMessage" => Str(input, "to") ?? Str(input, "recipient"),
            _ => null,
        };
        s ??= input.Select(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var str) ? str : null).FirstOrDefault(v => v is not null);
        return Core.State.Reducer.Truncate(Redactor.Redact((s ?? name).ReplaceLineEndings(" ")), 160);
    }

    public static string? Str(JsonNode? n, string key) =>
        n is JsonObject o && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    public static bool Bool(JsonNode? n, string key) =>
        n is JsonObject o && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;

    private static int Int(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
}
