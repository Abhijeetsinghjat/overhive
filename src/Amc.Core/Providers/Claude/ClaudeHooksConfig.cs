using System.Text.Json.Nodes;

namespace Amc.Core.Providers.Claude;

/// <summary>
/// Pure manipulation of Claude Code's settings.json "hooks" section. File IO and backups
/// are the caller's job (agentctl hooks install/uninstall). Our hook entries are recognised
/// by <see cref="Marker"/> inside the command, so foreign hooks are never touched.
/// </summary>
public static class ClaudeHooksConfig
{
    public const string Marker = "agent-mission-control";
    public const int TimeoutSeconds = 5;

    /// <summary>Events we subscribe to. All are documented Claude Code hook events.</summary>
    public static readonly IReadOnlyList<string> Events =
    [
        "SessionStart", "SessionEnd", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure",
        "Stop", "SubagentStart", "SubagentStop", "Notification",
    ];

    /// <summary>File name of the daemon's Unix socket inside its 0700 data directory.</summary>
    public const string SocketFileName = "amcd.sock";

    /// <summary>
    /// The hook command. Talks to the daemon over its Unix socket, which sits in a directory
    /// only this user can access: no token on any command line (visible in ps) and no TCP
    /// port another local user could squat while the daemon is down. Fails open: if the
    /// daemon is down or rejects the request, curl prints nothing (-f) and exits 0 (|| true),
    /// so the agent is never blocked or fed error text.
    /// </summary>
    public static string BuildCommand(string socketPath) =>
        $"curl -sf -m 3 --unix-socket {ShellQuote(socketPath)} -H 'Content-Type: application/json' " +
        $"--data-binary @- http://localhost/hooks/claude 2>/dev/null || true # {Marker}";

    /// <summary>Hook timeout for the asyncRewake waiter (seconds); the daemon releases it before this.</summary>
    public const int RewakeTimeoutSeconds = 3600;

    /// <summary>Events after which a session is idle; each arms one background waiter. Matcher null = all.</summary>
    public static readonly IReadOnlyList<(string Event, string? Matcher)> RewakeEvents =
        [("Stop", null), ("SessionStart", null), ("Notification", "idle_prompt")];

    /// <summary>
    /// Background waiter (async + asyncRewake, both documented): long-polls the daemon while the session
    /// is idle. If a message arrives it is printed to stderr and the hook exits 2, which wakes Claude
    /// with the text as a system reminder; otherwise it exits 0 silently.
    /// </summary>
    public static string BuildRewakeCommand(string socketPath)
    {
        // Each run appends start / curl result / wake to rewake.log next to the socket, so whether
        // Claude Code actually runs the waiter is observable (agentctl doctor, TROUBLESHOOTING.md).
        var log = ShellQuote(Path.Combine(Path.GetDirectoryName(socketPath) ?? ".", RewakeLogFileName));
        return $"echo \"$(date +%FT%T) start pid=$$\" >> {log} 2>/dev/null; " +
               $"m=$(curl -sf -m {RewakeTimeoutSeconds - 60} --unix-socket {ShellQuote(socketPath)} -H 'Content-Type: application/json' " +
               $"--data-binary @- http://localhost/hooks/claude/wait 2>/dev/null); rc=$?; " +
               $"echo \"$(date +%FT%T) curl=$rc bytes=${{#m}}\" >> {log} 2>/dev/null; " +
               $"[ -n \"$m\" ] && {{ printf '%s\\n' \"$m\" >&2; exit 2; }}; exit 0 # {Marker}:rewake";
    }

    public const string RewakeLogFileName = "rewake.log";

    /// <summary>
    /// The hook on Windows: Claude Code's exec form (<c>command</c> = absolute agentctl.exe, <c>args</c> = argv), spawned
    /// directly with no shell, so it behaves the same whether Claude Code would pick Git Bash or PowerShell and needs
    /// no quoting. agentctl hook-forward does what the curl one-liner does (see <see cref="BuildCommand"/>).
    /// </summary>
    public static HookCommand BuildWindowsCommand(HookForwarder forwarder, string socketPath) =>
        new(forwarder.Executable, forwarder.Args("claude", null, socketPath));

    /// <summary>The Windows asyncRewake waiter (see <see cref="BuildRewakeCommand"/>): agentctl hook-forward --wait.</summary>
    public static HookCommand BuildWindowsRewakeCommand(HookForwarder forwarder, string socketPath) =>
        new(forwarder.Executable, forwarder.Args("claude", null, socketPath, wait: true));

    /// <summary>A command hook's <c>command</c>, plus <c>args</c> for the exec form (no shell) when not null.</summary>
    public sealed record HookCommand(string Command, IReadOnlyList<string>? Args = null)
    {
        /// <summary>A shell-form command (the POSIX one-liners).</summary>
        public static implicit operator HookCommand(string command) => new(command);

        /// <summary>True when <paramref name="hook"/> runs exactly this command.</summary>
        public bool Matches(JsonNode? hook) =>
            hook?["command"]?.GetValue<string>() == Command &&
            (hook?["args"] is JsonArray a
                ? Args is not null && a.Select(x => x?.GetValue<string>()).SequenceEqual(Args)
                : Args is null);

        internal JsonObject ToJson()
        {
            var o = new JsonObject { ["type"] = "command", ["command"] = Command };
            if (Args is not null) o["args"] = new JsonArray(Args.Select(a => (JsonNode)JsonValue.Create(a)).ToArray());
            return o;
        }
    }

    public static bool IsRewakeInstalled(JsonObject settings) =>
        settings["hooks"] is JsonObject hooks &&
        RewakeEvents.All(r => hooks[r.Event] is JsonArray groups &&
                              groups.Any(g => g?["hooks"] is JsonArray hs && hs.Any(h => IsOurs(h) && h?["asyncRewake"]?.GetValue<bool>() == true)));

    /// <summary>POSIX single-quote escaping.</summary>
    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Ours when the marker is in the command or, for the exec form, in one of its args.</summary>
    public static bool IsOurs(JsonNode? hook) =>
        hook?["command"]?.GetValue<string>()?.Contains(Marker, StringComparison.Ordinal) == true ||
        (hook?["args"] as JsonArray)?.Any(a => a is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains(Marker, StringComparison.Ordinal)) == true;

    public static bool IsInstalled(JsonObject settings) =>
        settings["hooks"] is JsonObject hooks &&
        Events.All(ev => hooks[ev] is JsonArray groups &&
                         groups.Any(g => g?["hooks"] is JsonArray hs && hs.Any(IsOurs)));

    /// <summary>Returns a copy of <paramref name="settings"/> with our hooks installed (idempotent; replaces old versions of ours).</summary>
    public static JsonObject Install(JsonObject settings, HookCommand command, HookCommand? rewakeCommand = null)
    {
        var result = Uninstall(settings);
        var hooks = result["hooks"] as JsonObject ?? new JsonObject();
        result["hooks"] = hooks;
        foreach (var ev in Events)
        {
            var groups = hooks[ev] as JsonArray ?? new JsonArray();
            hooks[ev] = groups;
            var group = new JsonObject
            {
                ["hooks"] = new JsonArray(With(command.ToJson(), ("timeout", TimeoutSeconds))),
            };
            // Tool events take a matcher; "*" (all tools) is explicit rather than relying on omission.
            if (ev is "PreToolUse" or "PostToolUse" or "PostToolUseFailure") group["matcher"] = "*";
            groups.Add(group);
        }
        if (rewakeCommand is not null)
            foreach (var (ev, matcher) in RewakeEvents)
            {
                var groups = hooks[ev] as JsonArray ?? new JsonArray();
                hooks[ev] = groups;
                var group = new JsonObject
                {
                    ["hooks"] = new JsonArray(With(rewakeCommand.ToJson(), ("async", true), ("asyncRewake", true), ("timeout", RewakeTimeoutSeconds))),
                };
                if (matcher is not null) group["matcher"] = matcher;
                groups.Add(group);
            }
        return result;
    }

    private static JsonObject With(JsonObject o, params (string Key, JsonNode? Value)[] fields)
    {
        foreach (var (k, v) in fields) o[k] = v;
        return o;
    }

    /// <summary>Returns a copy with every hook entry of ours removed; empty groups/events are pruned. Foreign hooks are preserved.</summary>
    public static JsonObject Uninstall(JsonObject settings)
    {
        var result = (JsonObject)settings.DeepClone();
        if (result["hooks"] is not JsonObject hooks) return result;
        foreach (var ev in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[ev] is not JsonArray groups) continue;
            foreach (var g in groups.OfType<JsonObject>().ToList())
            {
                if (g["hooks"] is not JsonArray hs) continue;
                foreach (var h in hs.Where(IsOurs).ToList()) hs.Remove(h);
                if (hs.Count == 0) groups.Remove(g);
            }
            if (groups.Count == 0) hooks.Remove(ev);
        }
        if (hooks.Count == 0) result.Remove("hooks");
        return result;
    }
}
