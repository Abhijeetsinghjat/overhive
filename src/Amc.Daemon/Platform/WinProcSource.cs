using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Amc.Daemon.Collectors.Processes;

namespace Amc.Daemon.Platform;

/// <summary>
/// <see cref="IProcSource"/> for Windows, presenting <see cref="WinProc"/> data in the shapes of Linux /proc files like
/// <see cref="MacProcSource"/>. Time unit: 1 tick = 100 ns counted from 1601-01-01 (FILETIME), so a process's start
/// stamp (stat field 22) is its creation FILETIME, and CPU is reported in the same unit. A suspended process shows
/// state 'T'. There are no process groups or controlling terminals: pgrp is the pid, session is the Windows session id,
/// and "fd/0" is never reported. <see cref="ListPids"/> takes one system snapshot, and stat reads made on the same
/// thread while its pids are being enumerated (the scan loop) are served from it; any other stat read takes a fresh
/// snapshot, so liveness and state checks are never stale. Status (RSS only) may come from a snapshot up to
/// <see cref="MaxStatusAge"/> old.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinProcSource : IProcSource
{
    /// <summary>How old a snapshot may be to answer <see cref="ReadStatus"/> (working set only).</summary>
    public static readonly TimeSpan MaxStatusAge = TimeSpan.FromSeconds(2);

    private sealed record Snapshot(Dictionary<int, WinProcInfo> ByPid, long TakenAt);

    [ThreadStatic] private static Snapshot? _scanning;
    private readonly Lock _gate = new();
    private byte[]? _buffer;
    private volatile Snapshot? _last;

    public IEnumerable<int> ListPids()
    {
        var snap = Take();
        var outer = _scanning;
        _scanning = snap;
        try
        {
            foreach (var pid in snap.ByPid.Keys)
                if (pid > 0) yield return pid;
        }
        finally { _scanning = outer; }
    }

    public string? ReadStat(int pid)
    {
        if ((_scanning ?? Take()).ByPid.GetValueOrDefault(pid) is not { } p) return null;
        var state = p.Threads == 0 ? 'Z' : p.Suspended ? 'T' : 'R';
        var comm = WinProcText.Comm(p.Name);
        // Fields 3..22 of proc(5), as MacProcSource: state ppid pgrp session tty_nr … utime … starttime. CPU goes in utime.
        return string.Create(CultureInfo.InvariantCulture,
            $"{pid} ({comm}) {state} {p.Ppid} {pid} {p.SessionId} 0 0 0 0 0 0 0 {p.CpuTime} 0 0 0 0 0 {p.Threads} 0 {p.CreateTime}");
    }

    public byte[]? ReadCmdline(int pid) =>
        WinProc.CommandLine(pid) is { Length: > 0 } c && WinProcText.SplitCommandLine(c) is { Count: > 0 } argv ? Join(argv) : null;

    public byte[]? ReadEnviron(int pid) => WinProc.EnvironmentOf(pid) is { } env ? Join(env) : null;

    public string? ReadStatus(int pid) =>
        (_scanning ?? (_last is { } l && Stopwatch.GetElapsedTime(l.TakenAt) <= MaxStatusAge ? l : Take())).ByPid.GetValueOrDefault(pid) is { } p ? string.Create(CultureInfo.InvariantCulture, $"VmRSS:\t{p.WorkingSetBytes / 1024} kB\n") : null;

    /// <summary>"cwd" only (read from the process's PEB; null for processes we may not read).</summary>
    public string? ReadLink(int pid, string name) => name == "cwd" ? WinProc.Cwd(pid) : null;

    /// <summary>Same token user as this process; false for other users' and unreadable processes.</summary>
    public bool? OwnedByCurrentUser(int pid) => WinProc.OwnedByCurrentUser(pid);

    public long ClockTicksPerSecond => TimeSpan.TicksPerSecond;
    public DateTimeOffset BootTime { get; } = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private Snapshot Take()
    {
        lock (_gate)
        {
            var list = WinProc.Processes(ref _buffer);
            var byPid = new Dictionary<int, WinProcInfo>(list.Count);
            foreach (var p in list) byPid.TryAdd(p.Pid, p);
            return _last = new Snapshot(byPid, Stopwatch.GetTimestamp());
        }
    }

    private static byte[] Join(IReadOnlyList<string> parts) => Encoding.UTF8.GetBytes(string.Join('\0', parts) + "\0");
}
