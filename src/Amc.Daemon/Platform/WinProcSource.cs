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
/// and "fd/0" is never reported. <see cref="ListPids"/> takes the system snapshot; per-pid stat/status reads are served
/// from it while it is fresh (<see cref="MaxSnapshotAge"/>) and otherwise take a new one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinProcSource : IProcSource
{
    /// <summary>How long a snapshot answers single-process reads (state checks while pausing poll faster than this matters).</summary>
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromMilliseconds(250);

    private sealed record Snapshot(Dictionary<int, WinProcInfo> ByPid, long TakenAt);

    private readonly Lock _gate = new();
    private byte[]? _buffer;
    private volatile Snapshot? _snap;

    public IEnumerable<int> ListPids() => Take().ByPid.Keys.Where(p => p > 0).ToList();

    public string? ReadStat(int pid)
    {
        if (Info(pid) is not { } p) return null;
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
        Info(pid) is { } p ? string.Create(CultureInfo.InvariantCulture, $"VmRSS:\t{p.WorkingSetBytes / 1024} kB\n") : null;

    /// <summary>"cwd" only (read from the process's PEB; null for processes we may not read).</summary>
    public string? ReadLink(int pid, string name) => name == "cwd" ? WinProc.Cwd(pid) : null;

    /// <summary>Same token user as this process; false for other users' and unreadable processes.</summary>
    public bool? OwnedByCurrentUser(int pid) => WinProc.OwnedByCurrentUser(pid);

    public long ClockTicksPerSecond => TimeSpan.TicksPerSecond;
    public DateTimeOffset BootTime { get; } = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private WinProcInfo? Info(int pid)
    {
        var s = _snap;
        if (s is null || Stopwatch.GetElapsedTime(s.TakenAt) > MaxSnapshotAge) s = Take();
        return s.ByPid.GetValueOrDefault(pid);
    }

    private Snapshot Take()
    {
        lock (_gate)
        {
            var list = WinProc.Processes(ref _buffer);
            var byPid = new Dictionary<int, WinProcInfo>(list.Count);
            foreach (var p in list) byPid.TryAdd(p.Pid, p);
            return _snap = new Snapshot(byPid, Stopwatch.GetTimestamp());
        }
    }

    private static byte[] Join(IReadOnlyList<string> parts) => Encoding.UTF8.GetBytes(string.Join('\0', parts) + "\0");
}
