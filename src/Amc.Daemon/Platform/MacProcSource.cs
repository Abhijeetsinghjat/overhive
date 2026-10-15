using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Amc.Daemon.Collectors.Processes;

namespace Amc.Daemon.Platform;

/// <summary>
/// <see cref="IProcSource"/> for macOS. It presents libproc/sysctl data in the shapes of Linux /proc files
/// (a stat line, NUL-separated argv and environ, a VmRSS line), so the process tracker, tool detection,
/// signals and terminals work unchanged on both systems. Time unit: 1 tick = 1 µs, counted from the Unix
/// epoch, so a process's start stamp (stat field 22) is its start time in microseconds.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacProcSource : IProcSource
{
    public IEnumerable<int> ListPids() => MacProc.ListPids().Where(p => p > 0);

    public string? ReadStat(int pid)
    {
        if (MacProc.Basics(pid) is not { } b) return null;
        var cpuUs = b.CpuTime is { } c ? c.Ticks / 10 : 0;
        var comm = b.Name.Replace('\n', ' ');
        // Fields 3..22 of proc(5): state ppid pgrp session tty_nr tpgid flags minflt cminflt majflt cmajflt
        // utime stime cutime cstime priority nice num_threads itrealvalue starttime. CPU goes in utime.
        return string.Create(CultureInfo.InvariantCulture,
            $"{pid} ({comm}) {b.State} {b.Ppid} {b.Pgid} 0 0 0 0 0 0 0 0 {cpuUs} 0 0 0 0 0 1 0 {b.StartId}");
    }

    public byte[]? ReadCmdline(int pid) => MacProc.Args(pid) is { } a && a.Argv.Count > 0 ? Join(a.Argv) : null;

    public byte[]? ReadEnviron(int pid) => MacProc.Args(pid) is { } a ? Join(a.Env) : null;

    public string? ReadStatus(int pid) =>
        MacProc.Basics(pid) is { } b ? string.Create(CultureInfo.InvariantCulture, $"VmRSS:\t{b.RssBytes / 1024} kB\n") : null;

    /// <summary>"cwd" only; the controlling terminal ("fd/0") is not reported on macOS.</summary>
    public string? ReadLink(int pid, string name) => name == "cwd" ? MacProc.Cwd(pid) : null;

    public long ClockTicksPerSecond => 1_000_000;
    public DateTimeOffset BootTime => DateTimeOffset.UnixEpoch;

    private static byte[] Join(IReadOnlyList<string> parts) => Encoding.UTF8.GetBytes(string.Join('\0', parts) + "\0");
}
