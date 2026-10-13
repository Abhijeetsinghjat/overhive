using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Amc.Core.Events;
using Amc.Core.Providers;

namespace Amc.Daemon.Tools;

/// <summary>
/// Native hook adapter for AI tools other than Claude Code (Codex, Gemini CLI, Grok, Cursor, Copilot, and any
/// tool whose hooks speak a similar format). Each tool's hook POSTs its payload to /hooks/&lt;tool&gt;?e=&lt;event&gt;.
/// The payload is normalised onto the open protocol (<see cref="ProtocolReceiver"/>), and where the tool
/// supports it the reply carries queued dashboard messages in that tool's own output format.
/// Field names follow each tool's official hook documentation; unknown fields are ignored (fail open).
/// </summary>
public sealed class ToolHookReceiver(ProtocolReceiver protocol, ProtocolInbox inbox, GenericToolCollector processes,
    TimeProvider time, ILogger<ToolHookReceiver> log)
{
    public enum Kind { Start, End, Prompt, ToolStart, ToolEnd, ToolFail, Stop, SubStart, SubStop, Blocked, Notify, Usage, FileEdit }

    private static readonly Dictionary<string, Kind> Events = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SessionStart"] = Kind.Start, ["SessionEnd"] = Kind.End,
        ["UserPromptSubmit"] = Kind.Prompt, ["userPromptSubmitted"] = Kind.Prompt, ["beforeSubmitPrompt"] = Kind.Prompt, ["BeforeAgent"] = Kind.Prompt,
        ["PreToolUse"] = Kind.ToolStart, ["BeforeTool"] = Kind.ToolStart,
        ["PostToolUse"] = Kind.ToolEnd, ["AfterTool"] = Kind.ToolEnd,
        ["PostToolUseFailure"] = Kind.ToolFail,
        ["Stop"] = Kind.Stop, ["agentStop"] = Kind.Stop, ["AfterAgent"] = Kind.Stop,
        ["SubagentStart"] = Kind.SubStart, ["SubagentStop"] = Kind.SubStop,
        ["PermissionRequest"] = Kind.Blocked, ["Notification"] = Kind.Notify,
        ["AfterModel"] = Kind.Usage, ["afterFileEdit"] = Kind.FileEdit,
    };

    /// <summary>Tools whose documented hooks can carry text back into the agent (so dashboard messages work).</summary>
    public static readonly IReadOnlySet<string> CanDeliver = new HashSet<string> { "codex", "gemini", "cursor", "copilot" };

    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _openTools = new(); // tools without call ids

    public static string FrameMessage(string text, DateTimeOffset sentAt) =>
        $"Message from the user via Overhive (sent {sentAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}): {text}";

    public async Task<JsonObject?> HandleAsync(string tool, string? evt, JsonObject p, CancellationToken ct)
    {
        evt ??= S(p, "hook_event_name", "hookEventName");
        if (evt is null || !Events.TryGetValue(evt, out var kind)) return null;
        var session = tool == "cursor" ? S(p, "conversation_id", "session_id") : S(p, "session_id", "sessionId", "conversation_id");
        if (session is null) return null;
        var cwd = S(p, "cwd", "workspaceRoot") ?? (p["workspace_roots"] as JsonArray)?.FirstOrDefault()?.ToString();
        var sub = S(p, "agent_id", "subagent_id");
        var model = S(p, "model", "model_id");
        var agentId = sub is null ? session : $"{session}.{sub}";
        var internalId = ProtocolReceiver.AgentIdFor(tool, agentId);
        if (internalId is null) return null;

        // Link a hook-reported session to its running process (same tool, same working directory).
        if (sub is null && cwd is not null && processes.TryClaim(tool, cwd, internalId) is { } proc)
            await Report(tool, "start", Agent(session, null, cwd, model, proc.Pid), new JsonObject(), ct);

        var agent = Agent(agentId, sub is null ? null : session, cwd, model, null);
        var input = p["tool_input"] ?? p["toolInput"] ?? p["toolArgs"];
        var toolName = S(p, "tool_name", "toolName") ?? "tool";
        var command = input is JsonObject io ? S(io, "command", "cmd") : null;

        switch (kind)
        {
            case Kind.Start:
                await Report(tool, "start", agent, new JsonObject { ["reason"] = $"Session started ({S(p, "source") ?? "reported"})" }, ct);
                break;
            case Kind.End:
                await Report(tool, "end", agent, new JsonObject { ["reason"] = $"Session ended ({S(p, "reason", "final_status") ?? "reported"})" }, ct);
                break;
            case Kind.Prompt when S(p, "prompt", "initialPrompt") is { } text:
                await Report(tool, "prompt", agent, new JsonObject { ["text"] = text }, ct);
                await Report(tool, "status", agent, new JsonObject { ["status"] = "running", ["reason"] = "Prompt submitted (reported)" }, ct);
                break;
            case Kind.ToolStart:
            {
                var id = S(p, "tool_use_id", "toolUseId") ?? OpenTool(internalId, toolName);
                await Report(tool, "tool_start", agent, new JsonObject
                {
                    ["id"] = id, ["name"] = toolName, ["summary"] = command ?? PathOf(input, cwd) ?? toolName,
                    ["command"] = command, ["path"] = PathOf(input, cwd),
                }, ct);
                break;
            }
            case Kind.ToolEnd or Kind.ToolFail:
            {
                var id = S(p, "tool_use_id", "toolUseId") ?? CloseTool(internalId, toolName);
                var output = Text(p["tool_response"] ?? p["tool_output"] ?? (p["toolResult"] as JsonObject)?["textResultForLlm"] ?? p["toolResult"]);
                var error = kind == Kind.ToolFail ? S(p, "error_message", "error", "tool_error") ?? "Tool call failed" : null;
                await Report(tool, "tool_end", agent, new JsonObject
                {
                    ["id"] = id, ["name"] = toolName, ["command"] = command, ["output"] = output, ["error"] = error,
                }, ct);
                if (error is null && IsWriteTool(toolName))
                    foreach (var (path, change) in FilesOf(input, cwd))
                        await Report(tool, "file", agent, new JsonObject { ["path"] = path, ["change"] = change, ["toolId"] = id, ["tool"] = toolName }, ct);
                break;
            }
            case Kind.FileEdit when S(p, "file_path", "filePath", "path") is { } fp:
                await Report(tool, "file", agent, new JsonObject { ["path"] = Abs(fp, cwd), ["change"] = "modified" }, ct);
                break;
            case Kind.Stop:
                await Report(tool, "status", agent, new JsonObject { ["status"] = "waiting", ["reason"] = "Turn finished; waiting for input (reported)" }, ct);
                break;
            case Kind.SubStart when sub is not null:
                await Report(tool, "start", Agent($"{session}.{sub}", session, cwd, S(p, "subagent_model") ?? model, null,
                    S(p, "agent_type", "subagent_type", "agentName", "task")), new JsonObject(), ct);
                break;
            case Kind.SubStop when sub is not null:
                await Report(tool, "end", Agent($"{session}.{sub}", session, cwd, null, null), new JsonObject(), ct);
                break;
            case Kind.Blocked:
                await Report(tool, "status", agent, new JsonObject { ["status"] = "blocked", ["reason"] = $"Waiting for permission: {toolName} (reported)" }, ct);
                break;
            case Kind.Notify:
                if ((S(p, "notification_type") ?? "").Contains("permission", StringComparison.OrdinalIgnoreCase))
                    await Report(tool, "status", agent, new JsonObject { ["status"] = "blocked", ["reason"] = $"{S(p, "message") ?? "Waiting for permission"} (reported)" }, ct);
                break;
            case Kind.Usage when UsageMetadata(p) is { } u:
                await Report(tool, "usage", agent, new JsonObject
                {
                    ["id"] = Ulid.NewId(), ["model"] = model ?? S(p["llm_request"] as JsonObject ?? new JsonObject(), "model"),
                    ["input"] = (long?)u["promptTokenCount"] ?? 0, ["output"] = (long?)u["candidatesTokenCount"] ?? 0,
                    ["cacheRead"] = (long?)u["cachedContentTokenCount"] ?? 0,
                }, ct);
                break;
        }
        return await DeliverAsync(tool, evt, kind, internalId, p, ct);
    }

    /// <summary>Hands queued dashboard messages to the agent in the tool's own hook output format.</summary>
    private async Task<JsonObject?> DeliverAsync(string tool, string evt, Kind kind, string agentId, JsonObject p, CancellationToken ct)
    {
        if (!CanDeliver.Contains(tool)) return null;
        var asContext = kind is Kind.ToolEnd or Kind.ToolFail or Kind.Start || (kind == Kind.Prompt && tool is "codex" or "gemini");
        var asContinue = kind == Kind.Stop && p["stop_hook_active"]?.GetValue<bool>() != true;
        inbox.Touch(agentId); // this agent is reachable through its hooks
        if (!asContext && !asContinue) return null;
        var msgs = await inbox.TakeAsync(agentId, TimeSpan.Zero, ct, $"Delivered through the {tool} {evt} hook");
        if (msgs.Count == 0) return null;
        var text = string.Join("\n\n", msgs.Select(m => FrameMessage(m.Text, m.QueuedAt)));
        return (tool, asContinue) switch
        {
            ("cursor", true) => new JsonObject { ["followup_message"] = text },
            ("cursor", false) => new JsonObject { ["additional_context"] = text },
            ("copilot", false) => new JsonObject { ["additionalContext"] = text },
            ("gemini", true) => new JsonObject { ["decision"] = "deny", ["reason"] = text },
            (_, true) => new JsonObject { ["decision"] = "block", ["reason"] = text },
            _ => new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = evt, ["additionalContext"] = text } },
        };
    }

    private async Task Report(string tool, string kind, JsonObject agent, JsonObject data, CancellationToken ct)
    {
        var r = await protocol.ReportAsync(new JsonObject { ["tool"] = tool, ["kind"] = kind, ["agent"] = agent.DeepClone(), ["data"] = data }, ct);
        if (!r.Ok) log.LogDebug("{Tool} hook report {Kind} ignored: {Error}", tool, kind, r.Error);
    }

    private static JsonObject Agent(string id, string? parent, string? cwd, string? model, int? pid, string? type = null) => new()
    {
        ["id"] = id, ["parent"] = parent, ["cwd"] = cwd, ["model"] = model, ["pid"] = pid, ["type"] = type, ["title"] = type,
    };

    private static JsonObject? UsageMetadata(JsonObject p) =>
        p["llm_response"] is JsonObject r && r["usageMetadata"] is JsonObject u ? u : null;

    private string OpenTool(string agentId, string name)
    {
        var id = "h" + Ulid.NewId();
        _openTools.GetOrAdd(agentId + "|" + name, _ => new()).Enqueue(id);
        return id;
    }

    private string CloseTool(string agentId, string name) =>
        _openTools.TryGetValue(agentId + "|" + name, out var q) && q.TryDequeue(out var id) ? id : "h" + Ulid.NewId();

    private static bool IsWriteTool(string name) =>
        new[] { "write", "edit", "patch", "create", "replace", "apply" }.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static string? PathOf(JsonNode? input, string? cwd) =>
        input is JsonObject o && S(o, "file_path", "path", "filePath", "target_file", "absolute_path") is { } p ? Abs(p, cwd) : null;

    /// <summary>Paths an edit tool touched: a path field, or the file headers of a Codex-style patch.</summary>
    public static List<(string Path, string Change)> FilesOf(JsonNode? input, string? cwd)
    {
        var list = new List<(string, string)>();
        if (PathOf(input, cwd) is { } single) list.Add((single, "modified"));
        var patch = input is JsonObject o ? S(o, "patch", "input", "command") : input is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        foreach (var line in (patch ?? "").Split('\n'))
        {
            foreach (var (prefix, change) in new[] { ("*** Update File: ", "modified"), ("*** Add File: ", "created"), ("*** Delete File: ", "deleted") })
                if (line.StartsWith(prefix, StringComparison.Ordinal)) list.Add((Abs(line[prefix.Length..].Trim(), cwd), change));
        }
        return list.Where(x => x.Item1.StartsWith('/')).Distinct().ToList();
    }

    private static string Abs(string path, string? cwd) =>
        path.StartsWith('/') || cwd is null ? path : Path.GetFullPath(Path.Combine(cwd, path));

    private static string? Text(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString(),
    };

    private static string? S(JsonObject o, params string[] keys)
    {
        foreach (var k in keys)
            if (o[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
        return null;
    }
}
