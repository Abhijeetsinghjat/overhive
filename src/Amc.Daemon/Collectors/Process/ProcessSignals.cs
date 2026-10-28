using System.Globalization;
using System.Runtime.InteropServices;
using Amc.Daemon.Platform;

namespace Amc.Daemon.Collectors.Processes;

/// <summary>Signals by their Linux numbers (x86_64 and arm64); <see cref="ProcessSignals.Native"/> maps them for macOS.</summary>
public enum Signal
{
    Hup = 1, Int = 2, Quit = 3, Kill = 9, Usr1 = 10, Usr2 = 12, Term = 15,
    Cont = 18, Stop = 19, Tstp = 20, Winch = 28,
}

internal static class ProcNative
{
    private const int ScClkTck = 2; // _SC_CLK_TCK on Linux

    [DllImport("libc", SetLastError = true)] internal static extern int kill(int pid, int sig);
    [DllImport("libc", SetLastError = true)] internal static extern int getpgid(int pid);
    [DllImport("libc", SetLastError = true)] private static extern long sysconf(int name);

    /// <summary>USER_HZ. Falls back to 100, which is what every mainstream Linux kernel exports to userspace.</summary>
    internal static long ClockTicks()
    {
        try { var v = sysconf(ScClkTck); return v > 0 ? v : 100; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return 100; }
    }
}

/// <summary>
/// Signal delivery for process control (pause / resume / terminate). Used by agent providers.
///
/// Semantics callers must know:
/// <list type="bullet">
/// <item>SIGSTOP cannot be caught or ignored: the process is frozen immediately, timers keep
/// running, in-flight network requests (e.g. model API calls) may time out on the server side and
/// fail after resume. Stopping only the agent leaves its children (shells, test runners) running,
/// so pause/resume operate on the whole tree: <see cref="PauseTree"/> stops parents before children
/// (a stopped parent cannot spawn or reap meanwhile); <see cref="ResumeTree"/> continues children
/// first, then parents, so a parent never wakes up to half-frozen children.</item>
/// <item>SIGCONT always resumes, even if the process stopped itself (SIGTSTP/^Z).</item>
/// <item>If the agent's parent is an interactive job-control shell (agent started from bash
/// without `agentctl run`), that shell sees the child stop (WUNTRACED), prints "Stopped" and takes
/// the terminal back; after SIGCONT the agent runs in the background and is stopped again by SIGTTIN
/// as soon as it reads the terminal. Under `agentctl run` the parent does not use job control, so
/// pause/resume is transparent.</item>
/// <item>A stopped TUI keeps its terminal in raw mode; the user's keystrokes queue in the PTY.</item>
/// <item>Windows has no signals; <see cref="Send"/> maps them: STOP/TSTP suspend every thread (NtSuspendProcess),
/// CONT resumes (NtResumeProcess; suspends are counted, one resume undoes one suspend), TERM and KILL both
/// TerminateProcess with exit code 128 + signal. There is no graceful stop: a background daemon cannot deliver Ctrl-C
/// to another console without attaching to it, so TERM runs no cleanup code in the target. HUP, INT, QUIT, USR1/2 and
/// WINCH are not delivered (Send returns false); interrupting goes through the terminal instead.</item>
/// </list>
/// Always verify identity (<see cref="SendVerified"/>) before signalling a pid taken from stored state:
/// pids are reused.
/// </summary>
public static class ProcessSignals
{
    private const int Esrch = 3;

    /// <summary>kill(pid, sig). False if the process does not exist or is not ours.</summary>
    public static bool Send(int pid, Signal sig)
    {
        if (pid <= 1) return false; // never signal init or "all processes" (0/-1)
        if (OperatingSystem.IsWindows()) return SendWindows(pid, sig, null);
        return ProcNative.kill(pid, Native(sig)) == 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool SendWindows(int pid, Signal sig, long? start) => sig switch
    {
        Signal.Stop or Signal.Tstp => WinProc.Suspend(pid, start),
        Signal.Cont => WinProc.Resume(pid, start),
        Signal.Term or Signal.Kill => WinProc.Terminate(pid, (uint)(128 + (int)sig), start),
        _ => false,
    };

    /// <summary>The OS signal number. macOS numbers differ for these (STOP is 17 there, 19 means CONT).</summary>
    public static int Native(Signal sig) => !OperatingSystem.IsMacOS() ? (int)sig : sig switch
    {
        Signal.Usr1 => 30, Signal.Usr2 => 31, Signal.Cont => 19, Signal.Stop => 17, Signal.Tstp => 18,
        _ => (int)sig, // HUP INT QUIT KILL TERM WINCH are the same
    };

    /// <summary>Signals a process group (kill(-pgid, sig)).</summary>
    public static bool SendGroup(int pgid, Signal sig)
    {
        if (pgid <= 1 || OperatingSystem.IsWindows()) return false; // no process groups on Windows
        return ProcNative.kill(-pgid, Native(sig)) == 0;
    }

    /// <summary>Signals only if /proc still shows the same starttime (PID-reuse safe; tiny TOCTOU window remains).</summary>
    public static bool SendVerified(int pid, string procStart, Signal sig, IProcSource? proc = null)
    {
        // Windows: the creation time is checked on the same handle the signal goes through, so there is no window at all.
        if (OperatingSystem.IsWindows() && proc is null or WinProcSource)
            return pid > 1 && long.TryParse(procStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) && SendWindows(pid, sig, start);
        var st = ProcParsers.ParseStat((proc ?? DefaultProc).ReadStat(pid));
        return st is not null && st.ProcStart == procStart && Send(pid, sig);
    }

    public static bool IsAlive(int pid) =>
        pid > 0 && (OperatingSystem.IsWindows() ? WinProc.IsAlive(pid)
            : ProcNative.kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() != Esrch);

    public static bool IsAlive(int pid, string? procStart, IProcSource? proc = null)
    {
        var st = ProcParsers.ParseStat((proc ?? DefaultProc).ReadStat(pid));
        return st is not null && st.State != 'Z' && (procStart is null || st.ProcStart == procStart);
    }

    /// <summary>Process group id, or null when the process is gone (always null on Windows, which has none).</summary>
    public static int? GetProcessGroup(int pid)
    {
        if (OperatingSystem.IsWindows()) return null;
        var g = ProcNative.getpgid(pid);
        return g > 0 ? g : null;
    }

    /// <summary>
    /// Signals pid and all live descendants (from <see cref="ILiveProcesses.Tree"/>), in tree order
    /// (parents first) — or reversed when <paramref name="childrenFirst"/>. Returns the count delivered.
    /// </summary>
    public static int SendTree(ILiveProcesses live, int pid, Signal sig, bool childrenFirst = false)
    {
        var tree = live.Tree(pid);
        IEnumerable<int> order = childrenFirst ? tree.Reverse() : tree;
        var n = 0;
        foreach (var p in order) if (Send(p, sig)) n++;
        return n;
    }

    /// <summary>SIGSTOP the tree, parents first.</summary>
    public static int PauseTree(ILiveProcesses live, int pid) => SendTree(live, pid, Signal.Stop);

    /// <summary>SIGCONT the tree, children first.</summary>
    public static int ResumeTree(ILiveProcesses live, int pid) => SendTree(live, pid, Signal.Cont, childrenFirst: true);

    /// <summary>
    /// SIGTERM the tree (children first so a parent's cleanup isn't racing orphaned children).
    /// Escalation to SIGKILL is the caller's decision.
    /// </summary>
    public static int TerminateTree(ILiveProcesses live, int pid) => SendTree(live, pid, Signal.Term, childrenFirst: true);

    private static IProcSource DefaultProc => ProcSources.Default;
}
