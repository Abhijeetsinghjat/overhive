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
    /// <summary>Process name, or null when unknown.</summary>
    string? Name(int pid) => null;
}

/// <summary><see cref="IProcFs"/> over the platform process source (/proc on Linux, libproc on macOS).</summary>
public sealed class LinuxProcFs(Amc.Daemon.Collectors.Processes.IProcSource? source = null) : IProcFs
{
    private readonly Amc.Daemon.Collectors.Processes.IProcSource _src = source ?? Amc.Daemon.Collectors.Processes.ProcSources.Default;

    private Amc.Daemon.Collectors.Processes.ProcStat? Stat(int pid) =>
        Amc.Daemon.Collectors.Processes.ProcParsers.ParseStat(_src.ReadStat(pid));

    public string? ProcStart(int pid) => Stat(pid)?.ProcStart;
    public char? State(int pid) => Stat(pid)?.State;
    public string? Name(int pid) => Stat(pid)?.Comm;
}

public static class ProcFsExtensions
{
    /// <summary>True only when the pid exists and is the same process (start time matches) — PID-reuse safe.</summary>
    public static bool IsAlive(this IProcFs proc, int? pid, string? procStart)
    {
        if (pid is not > 0 || procStart is null || proc.State(pid.Value) is null or 'Z' or 'X') return false;
        if (proc.ProcStart(pid.Value) == procStart) return true;
        // On Linux Claude Code records the /proc start time, so a mismatch means another process. Elsewhere its
        // stamp may not be comparable with ours: accept a live claude/node process (weaker pid-reuse protection).
        return !OperatingSystem.IsLinux() && proc.Name(pid.Value) is { } n
            && (n.Contains("claude", StringComparison.OrdinalIgnoreCase) || n.Contains("node", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Signal delivery (abstracted for tests), using the running OS's numbers (see <see cref="Signals"/>).</summary>
public interface ISignalSender
{
    /// <summary>Returns 0 on success, otherwise errno.</summary>
    int Send(int pid, int signal);
}

public static class Signals
{
    public const int SigKill = 9, SigTerm = 15;
    /// <summary>CONT and STOP differ on macOS (19 and 17) from Linux (18 and 19).</summary>
    public static readonly int SigCont = OperatingSystem.IsMacOS() ? 19 : 18, SigStop = OperatingSystem.IsMacOS() ? 17 : 19;
}

public sealed class LibcSignalSender : ISignalSender
{
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);

    public int Send(int pid, int signal)
    {
        if (pid <= 1) return 1; // never signal init or process groups
        // Windows: the Linux numbers in Signals are mapped to suspend/resume/terminate (see ProcessSignals).
        if (OperatingSystem.IsWindows()) return Amc.Daemon.Collectors.Processes.ProcessSignals.Send(pid, (Amc.Daemon.Collectors.Processes.Signal)signal) ? 0 : 1;
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
