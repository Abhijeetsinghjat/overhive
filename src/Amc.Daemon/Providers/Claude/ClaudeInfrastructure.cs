using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Providers.Claude;
using Amc.Daemon.Storage;

namespace Amc.Daemon.Providers.Claude;

/// <summary>Claude Code file layout and id conventions.</summary>
public static class ClaudePaths
{
    public const string Provider = "claude";

    /// <summary>Claude Code's project directory name for a cwd: every non-alphanumeric char becomes '-'.</summary>
    public static string Slug(string cwd) => new(cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    /// <summary>Hook payloads may say "agent-abc", transcript files say "agent-abc.jsonl", records say "abc".</summary>
    public static string NormalizeAgentId(string id) => id.StartsWith("agent-", StringComparison.Ordinal) ? id["agent-".Length..] : id;

    public static string MainId(string sessionId) => Ids.MainAgent(Provider, sessionId);
    public static string SubId(string sessionId, string agentId) => Ids.SubAgent(Provider, sessionId, NormalizeAgentId(agentId));

    /// <summary>True when the id is not the session's main agent id (ids are built only via <see cref="Ids"/>, never parsed).</summary>
    public static bool IsSubId(string agentId, string sessionId) => agentId != MainId(sessionId);

    /// <summary>Best-known transcript path for a session: projects/&lt;slug(cwd)&gt;/&lt;session&gt;.jsonl, else a search.</summary>
    public static string? FindTranscript(string claudeHome, string sessionId, string? cwd)
    {
        var projects = Path.Combine(claudeHome, "projects");
        if (cwd is not null)
        {
            var p = Path.Combine(projects, Slug(cwd), sessionId + ".jsonl");
            if (File.Exists(p)) return p;
        }
        if (!Directory.Exists(projects)) return null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var p = Path.Combine(dir, sessionId + ".jsonl");
                if (File.Exists(p)) return p;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}

/// <summary>Minimal /proc access (abstracted for tests).</summary>
public interface IProcFs
{
    /// <summary>Field 22 of /proc/&lt;pid&gt;/stat (start time in clock ticks), or null if the process does not exist.</summary>
    string? ProcStart(int pid);
    /// <summary>Field 3 of /proc/&lt;pid&gt;/stat (R, S, T, Z …), or null.</summary>
    char? State(int pid);
}

public sealed class LinuxProcFs : IProcFs
{
    public string? ProcStart(int pid) => Fields(pid) is { Length: > 19 } f ? f[19] : null;
    public char? State(int pid) => Fields(pid) is { Length: > 0 } f && f[0].Length > 0 ? f[0][0] : null;

    /// <summary>Fields after the "(comm)" part: index 0 = field 3 (state).</summary>
    private static string[]? Fields(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var close = stat.LastIndexOf(')');
            return close < 0 ? null : stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public static class ProcFsExtensions
{
    /// <summary>True only when the pid exists and is the same process (start time matches) — PID-reuse safe.</summary>
    public static bool IsAlive(this IProcFs proc, int? pid, string? procStart) =>
        pid is > 0 && procStart is not null && proc.ProcStart(pid.Value) == procStart && proc.State(pid.Value) is not 'Z' and not 'X';
}

/// <summary>Signal delivery (abstracted for tests). Linux numbering.</summary>
public interface ISignalSender
{
    /// <summary>Returns 0 on success, otherwise errno.</summary>
    int Send(int pid, int signal);
}

public static class Signals
{
    public const int SigKill = 9, SigTerm = 15, SigCont = 18, SigStop = 19;
}

public sealed class LibcSignalSender : ISignalSender
{
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);

    public int Send(int pid, int signal)
    {
        if (pid <= 1) return 1; // never signal init or process groups
        return Kill(pid, signal) == 0 ? 0 : Marshal.GetLastWin32Error();
    }
}

/// <summary>Collector checkpoints (abstracted for tests).</summary>
public interface ICursorStore
{
    string? Get(string key);
    void Set(string key, string value);
}

public sealed class EventStoreCursorStore(EventStore store) : ICursorStore
{
    public string? Get(string key) => store.GetCursor(key);
    public void Set(string key, string value) => store.SetCursor(key, value);
}

/// <summary>Whether our hooks are installed in ~/.claude/settings.json; re-read only when its mtime changes.</summary>
public sealed class ClaudeHooksState(string settingsPath)
{
    private readonly object _gate = new();
    private DateTime _mtime = DateTime.MinValue;
    private bool _installed;

    public string SettingsPath { get; } = settingsPath;

    public bool IsInstalled()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(SettingsPath)) { _mtime = DateTime.MinValue; return _installed = false; }
                var m = File.GetLastWriteTimeUtc(SettingsPath);
                if (m == _mtime) return _installed;
                _mtime = m;
                _installed = JsonNode.Parse(File.ReadAllText(SettingsPath)) is JsonObject o && ClaudeHooksConfig.IsInstalled(o);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                _installed = false;
            }
            return _installed;
        }
    }
}
