using System.Text.Json.Nodes;
using Amc.Core.Providers.Claude;

namespace Amc.Core.Providers;

/// <summary>
/// Hook installation for AI tools other than Claude Code (which has <see cref="ClaudeHooksConfig"/>).
/// Pure JSON manipulation; file IO, backups and confirmation are the CLI's job. Each tool gets a command
/// hook per lifecycle event that POSTs the hook's stdin payload to the daemon's private socket at
/// /hooks/&lt;tool&gt;?e=&lt;event&gt;. Our entries are recognised by <see cref="ClaudeHooksConfig.Marker"/>.
/// Formats follow each tool's official hook documentation.
/// </summary>
public static class ToolHooksConfig
{
    public sealed record Profile(
        string Tool,
        /// <summary>User-level config file, relative to $HOME.</summary>
        string UserFile,
        /// <summary>"claude-like" ({hooks:{Event:[{matcher?,hooks:[{type,command,timeout}]}]}}), "cursor" ({version,hooks:{event:[{command}]}}), "copilot" ({version,hooks:{event:[{type,bash,timeoutSec}]}}).</summary>
        string Shape,
        IReadOnlyList<string> Events,
        /// <summary>Multiplier from seconds to the tool's timeout unit (Gemini uses milliseconds).</summary>
        int TimeoutScale,
        string TimeoutKey,
        /// <summary>Whether the file belongs to us alone (then uninstall may delete it).</summary>
        bool OwnFile,
        string? Note);

    public static readonly IReadOnlyDictionary<string, Profile> Profiles = new Dictionary<string, Profile>
    {
        ["codex"] = new("codex", ".codex/hooks.json", "claude-like",
            ["SessionStart", "SessionEnd", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Stop", "SubagentStart", "SubagentStop"],
            1, "timeout", false, "Codex asks you to review new hooks once: run `/hooks` in Codex and trust them."),
        ["gemini"] = new("gemini", ".gemini/settings.json", "claude-like",
            ["SessionStart", "SessionEnd", "BeforeAgent", "AfterAgent", "BeforeTool", "AfterTool", "AfterModel", "Notification"],
            1000, "timeout", false, "Gemini CLI may ask you to trust new hooks; see `/hooks panel`."),
        ["grok"] = new("grok", ".grok/hooks/agent-mission-control.json", "claude-like",
            ["SessionStart", "SessionEnd", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "Stop", "Notification", "SubagentStart", "SubagentStop"],
            1, "timeout", true, null),
        ["cursor"] = new("cursor", ".cursor/hooks.json", "cursor",
            ["sessionStart", "sessionEnd", "beforeSubmitPrompt", "preToolUse", "postToolUse", "postToolUseFailure", "afterFileEdit", "stop", "subagentStart", "subagentStop"],
            1, "timeout", false, null),
        ["copilot"] = new("copilot", ".copilot/hooks/agent-mission-control.json", "copilot",
            ["sessionStart", "sessionEnd", "userPromptSubmitted", "preToolUse", "postToolUse", "postToolUseFailure", "agentStop", "subagentStart", "subagentStop", "notification", "permissionRequest"],
            1, "timeoutSec", true, null),
    };

    public const int TimeoutSeconds = 5;

    public static string BuildCommand(string socketPath, string tool, string evt) =>
        $"curl -sf -m 3 --unix-socket {ClaudeHooksConfig.ShellQuote(socketPath)} -H 'Content-Type: application/json' " +
        $"--data-binary @- 'http://localhost/hooks/{tool}?e={evt}' 2>/dev/null || true # {ClaudeHooksConfig.Marker}";

    private static string? CommandOf(JsonNode? h) => (string?)h?["command"] ?? (string?)h?["bash"];
    public static bool IsOurs(JsonNode? h) => CommandOf(h)?.Contains(ClaudeHooksConfig.Marker, StringComparison.Ordinal) == true;

    private static IEnumerable<JsonNode?> Entries(Profile p, JsonArray list) =>
        p.Shape == "claude-like" ? list.SelectMany(g => g?["hooks"] as JsonArray ?? []) : list;

    public static bool IsInstalled(Profile p, JsonObject settings) =>
        settings["hooks"] is JsonObject hooks && p.Events.All(ev => hooks[ev] is JsonArray l && Entries(p, l).Any(IsOurs));

    public static JsonObject Install(Profile p, JsonObject settings, string socketPath)
    {
        var result = Uninstall(p, settings);
        if (p.Shape is "cursor" or "copilot") result["version"] ??= 1;
        var hooks = result["hooks"] as JsonObject ?? new JsonObject();
        result["hooks"] = hooks;
        foreach (var ev in p.Events)
        {
            var list = hooks[ev] as JsonArray ?? new JsonArray();
            hooks[ev] = list;
            var cmd = BuildCommand(socketPath, p.Tool, ev);
            list.Add(p.Shape switch
            {
                "claude-like" => new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = cmd, [p.TimeoutKey] = TimeoutSeconds * p.TimeoutScale }),
                },
                "cursor" => new JsonObject { ["type"] = "command", ["command"] = cmd, [p.TimeoutKey] = TimeoutSeconds },
                _ => new JsonObject { ["type"] = "command", ["bash"] = cmd, [p.TimeoutKey] = TimeoutSeconds },
            });
        }
        return result;
    }

    public static JsonObject Uninstall(Profile p, JsonObject settings)
    {
        var result = (JsonObject)settings.DeepClone();
        if (result["hooks"] is not JsonObject hooks) return result;
        foreach (var ev in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[ev] is not JsonArray list) continue;
            if (p.Shape == "claude-like")
                foreach (var g in list.OfType<JsonObject>().ToList())
                {
                    if (g["hooks"] is not JsonArray hs) continue;
                    foreach (var h in hs.Where(IsOurs).ToList()) hs.Remove(h);
                    if (hs.Count == 0) list.Remove(g);
                }
            else
                foreach (var h in list.Where(IsOurs).ToList()) list.Remove(h);
            if (list.Count == 0) hooks.Remove(ev);
        }
        if (hooks.Count == 0) result.Remove("hooks");
        return result;
    }
}
