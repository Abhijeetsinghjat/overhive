using System.Text.Json.Nodes;

namespace Amc.Daemon.Providers.Claude;

/// <summary>
/// Shrinks Claude Code tool payloads before they are held in memory (remembered tool starts,
/// queued hook calls). Keeps exactly what the mapper reads later; drops bulk text it never reads
/// (Write content, Edit old/new strings, MultiEdit edits, Edit's originalFile, ...).
/// </summary>
public static class ClaudePayloadTrim
{
    /// <summary>Remembered tool input strings (paths, descriptions, patterns, task titles): head kept.</summary>
    public const int MaxInputChars = 1024;
    /// <summary>The Bash command (it is the end event's command): head kept.</summary>
    public const int MaxCommandChars = 16 * 1024;
    /// <summary>Strings of a queued hook payload outside tool_input: tail kept, like <see cref="Amc.Core.Security.Redactor.RedactAndCap"/>.
    /// Above the 16K cap every persisted event string gets, so events are unchanged; test summaries sit at the tail.</summary>
    public const int MaxPayloadChars = 64 * 1024;
    /// <summary>Line count of a dropped Write "content", so a created file still reports its added lines.</summary>
    public const string ContentLinesKey = "__amcContentLines";

    private static readonly HashSet<string> KeptArrays = ["todos"];
    private static readonly HashSet<string> DroppedResponseKeys = ["originalFile"];

    /// <summary>
    /// A small copy of a tool input: top-level scalars only (strings capped), plus TodoWrite's todos.
    /// Nested objects and arrays (MultiEdit edits, AskUserQuestion questions, MCP arguments) are never read after the start.
    /// </summary>
    public static JsonObject CompactInput(JsonObject input)
    {
        var o = new JsonObject();
        foreach (var (k, v) in input)
        {
            switch (v)
            {
                case null:
                    o[k] = null;
                    break;
                case JsonValue jv when jv.TryGetValue<string>(out var s):
                    if (k == "content" && !input.ContainsKey(ContentLinesKey)) o[ContentLinesKey] = LineCount(s);
                    o[k] = Head(s, k == "command" ? MaxCommandChars : MaxInputChars);
                    break;
                case JsonValue jv:
                    o[k] = jv.DeepClone();
                    break;
                case JsonArray a when KeptArrays.Contains(k):
                    o[k] = a.DeepClone(); // todo titles become task titles: kept as written
                    break;
            }
        }
        return o;
    }

    /// <summary>
    /// Compacts a hook payload in place before it is queued for mapping: tool_input as <see cref="CompactInput"/>,
    /// Edit's originalFile dropped, every other string tail-capped. Returns the approximate retained size in bytes.
    /// </summary>
    public static long CompactHookPayload(JsonObject payload)
    {
        if (payload["tool_input"] is JsonObject input) payload["tool_input"] = CompactInput(input);
        foreach (var key in new[] { "tool_response", "tool_output", "tool_result" })
            if (payload[key] is JsonObject r)
                foreach (var drop in DroppedResponseKeys) r.Remove(drop);
        CapStrings(payload, MaxPayloadChars, skip: "tool_input");
        return EstimateBytes(payload);
    }

    /// <summary>Lines of a file written with this content (same rule as before the content was dropped).</summary>
    public static int LineCount(string content)
    {
        if (content.Length == 0) return 0;
        var n = content.AsSpan().Count('\n') + 1;
        return content.EndsWith('\n') ? n - 1 : n;
    }

    /// <summary>Rough managed size of a JSON tree (UTF-16 strings plus per-node overhead).</summary>
    public static long EstimateBytes(JsonNode? node) => node switch
    {
        null => 0,
        JsonObject o => 64 + o.Sum(kv => 48 + 2L * kv.Key.Length + EstimateBytes(kv.Value)),
        JsonArray a => 64 + a.Sum(EstimateBytes),
        JsonValue v when v.TryGetValue<string>(out var s) => 48 + 2L * s.Length,
        _ => 48,
    };

    private static string Head(string s, int max)
    {
        if (s.Length <= max) return s;
        if (char.IsHighSurrogate(s[max - 1])) max--;
        return s[..max];
    }

    private static string Tail(string s, int max)
    {
        if (s.Length <= max) return s;
        var start = s.Length - max;
        if (char.IsLowSurrogate(s[start])) start++;
        return s[start..];
    }

    private static void CapStrings(JsonNode? node, int max, string? skip = null)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (key == skip) continue;
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s)) { if (s.Length > max) o[key] = Tail(s, max); }
                    else CapStrings(o[key], max);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s)) { if (s.Length > max) a[i] = Tail(s, max); }
                    else CapStrings(a[i], max);
                }
                break;
        }
    }
}
