using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Amc.Daemon.Collectors.Processes;
using Amc.Daemon.Platform;

namespace Amc.Tests.Platform;

/// <summary>A fact that only runs on Windows (reported as skipped elsewhere).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

public class WinProcTextTests
{
    [Theory]
    [InlineData(@"C:\tools\node.exe C:\x\codex.js --model gpt-5", new[] { @"C:\tools\node.exe", @"C:\x\codex.js", "--model", "gpt-5" })]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" ""a b"" c", new[] { @"C:\Program Files\nodejs\node.exe", "a b", "c" })]
    [InlineData(@"prog a\\\""b ""c\\"" d\e", new[] { "prog", @"a\""b", @"c\", @"d\e" })]
    [InlineData(@"prog ""say """"hi"""""" ""x""y", new[] { "prog", @"say ""hi""", "xy" })]
    [InlineData("prog\t  a   ", new[] { "prog", "a" })]
    [InlineData(@"""C:\a b\x.exe""", new[] { @"C:\a b\x.exe" })]
    public void Command_line_is_split_like_the_C_runtime(string line, string[] expected) =>
        Assert.Equal(expected, WinProcText.SplitCommandLine(line));

    [Fact]
    public void Environment_block_stops_at_the_empty_string()
    {
        var block = Encoding.Unicode.GetBytes("=C:=C:\\work\0Path=C:\\bin\0HOME=C:\\Users\\a\0\0garbage\0");
        Assert.Equal(["=C:=C:\\work", "Path=C:\\bin", "HOME=C:\\Users\\a"], WinProcText.ParseEnvironmentBlock(block));
    }

    [Fact]
    public void Parent_created_after_the_child_is_a_reused_pid()
    {
        var list = new List<WinProcInfo>
        {
            P(10, 0, 100), P(20, 10, 200),   // real parent: older than the child
            P(30, 40, 300), P(40, 0, 400),   // pid 40 was reused after 30's parent exited
            P(50, 99, 500),                  // parent gone entirely: keep the recorded pid
        };
        var byPid = WinProcText.ValidateParents(list).ToDictionary(p => p.Pid, p => p.Ppid);
        Assert.Equal(10, byPid[20]);
        Assert.Equal(0, byPid[30]);
        Assert.Equal(99, byPid[50]);

        static WinProcInfo P(int pid, int ppid, long created) => new(pid, ppid, 1, "x.exe", created, 0, 0, 1, false);
    }

    [Fact]
    public void Comm_drops_the_exe_suffix()
    {
        Assert.Equal("node", WinProcText.Comm("node.EXE"));
        Assert.Equal("System", WinProcText.Comm("System"));
    }
}

/// <summary>Live checks against this test process and children it starts; they only mean something on Windows.</summary>
public class WinProcTests
{
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Reads_this_process()
    {
        var self = Environment.ProcessId;
        var src = new WinProcSource();
        Assert.Contains(self, src.ListPids());

        var st = ProcParsers.ParseStat(src.ReadStat(self))!;
        Assert.Equal(self, st.Pid);
        Assert.Equal('R', st.State);
        Assert.True(st.CpuTicks > 0);
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(me.StartTime.ToFileTimeUtc(), st.StartTime);
        Assert.Equal(st.ProcStart, ProcParsers.ParseStat(src.ReadStat(self))!.ProcStart);
        Assert.Equal(Path.GetFileNameWithoutExtension(Environment.ProcessPath), st.Comm, ignoreCase: true);
        var started = src.BootTime + TimeSpan.FromSeconds(st.StartTime / (double)src.ClockTicksPerSecond);
        Assert.InRange((started.UtcDateTime - me.StartTime.ToUniversalTime()).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        // The parent is alive (the test runner) and older than us.
        var parent = ProcParsers.ParseStat(src.ReadStat(st.Ppid))!;
        Assert.True(parent.StartTime < st.StartTime);

        var argv = Argv(src.ReadCmdline(self));
        Assert.Equal(Path.GetFileNameWithoutExtension(Environment.ProcessPath), Path.GetFileNameWithoutExtension(argv[0]), ignoreCase: true);
        var ours = Environment.GetCommandLineArgs();
        if (ours.Length > 1) Assert.Equal(ours[^1], argv[^1]);

        Assert.Equal(Environment.CurrentDirectory, src.ReadLink(self, "cwd"));
        Assert.Null(src.ReadLink(self, "fd/0"));
        var env = ProcParsers.EnvSummary(src.ReadEnviron(self));
        Assert.Contains(env, e => e.StartsWith("PATH", StringComparison.OrdinalIgnoreCase));
        Assert.True(ProcParsers.ParseVmRssBytes(src.ReadStatus(self)) > 1 << 20);
        Assert.True(src.OwnedByCurrentUser(self));
        Assert.Equal<long?>(me.StartTime.ToFileTimeUtc(), WinProc.CreationTime(self));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Spawned_children_are_descendants_and_can_be_suspended_resumed_and_terminated()
    {
        var src = new WinProcSource();
        using var child = StartPinging();
        try
        {
            var start = ProcParsers.ParseStat(src.ReadStat(child.Id))!.ProcStart;
            Assert.Equal(child.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture), start);
            Assert.Contains("/c", Argv(src.ReadCmdline(child.Id)));
            Assert.Equal(Environment.CurrentDirectory, src.ReadLink(child.Id, "cwd"));

            // cmd.exe → ping.exe both show up below this process (cmd may also own a conhost.exe).
            var self = Environment.ProcessId;
            var selfStart = ProcParsers.ParseStat(src.ReadStat(self))!.ProcStart;
            var tracker = new ProcessTracker(src);
            var table = new LiveProcessTable();
            ScanResult r = null!;
            Assert.True(WaitUntil(() =>
            {
                r = tracker.Scan([new AgentProcessRef("test:self", self, selfStart)], [], Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
                return r.Processes.Any(p => p.Ppid == child.Id && p.Command.Contains("ping", StringComparison.OrdinalIgnoreCase));
            }));
            table.Update(r.Processes, r.Ppids);
            var ping = r.Processes.Single(p => p.Ppid == child.Id && p.Command.Contains("ping", StringComparison.OrdinalIgnoreCase)).Pid;
            var tree = table.Tree(child.Id);
            Assert.Equal(child.Id, tree[0]);
            Assert.Contains(ping, tree);
            Assert.Equal(Environment.CurrentDirectory, r.Processes.Single(p => p.Pid == self).Cwd);

            Assert.Equal(tree.Count, ProcessSignals.PauseTree(table, child.Id));
            Assert.True(WaitUntil(() => State(src, child.Id) == 'T' && State(src, ping) == 'T'));
            Assert.Equal(tree.Count, ProcessSignals.ResumeTree(table, child.Id));
            Assert.True(WaitUntil(() => State(src, child.Id) == 'R' && State(src, ping) == 'R'));

            // No Windows equivalent: reported as not delivered, never pretended.
            Assert.False(ProcessSignals.Send(child.Id, Signal.Int));
            Assert.False(ProcessSignals.SendGroup(child.Id, Signal.Term));
            Assert.Null(ProcessSignals.GetProcessGroup(child.Id));

            Assert.Equal(tree.Count, ProcessSignals.TerminateTree(table, child.Id));
            Assert.True(child.WaitForExit(10_000));
            Assert.Equal(128 + 15, child.ExitCode);
            Assert.True(WaitUntil(() => !ProcessSignals.IsAlive(ping)));
            Assert.False(ProcessSignals.IsAlive(child.Id, start, src));
        }
        finally { Kill(child); }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void A_different_start_time_is_another_process_and_is_never_signalled()
    {
        var src = new WinProcSource();
        using var child = StartPinging();
        try
        {
            var start = ProcParsers.ParseStat(src.ReadStat(child.Id))!.ProcStart;
            var other = (long.Parse(start, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(ProcessSignals.IsAlive(child.Id));
            Assert.True(ProcessSignals.IsAlive(child.Id, start, src));
            Assert.False(ProcessSignals.IsAlive(child.Id, other, src));

            Assert.False(ProcessSignals.SendVerified(child.Id, other, Signal.Kill));
            Assert.False(WinProc.Suspend(child.Id, long.Parse(other, System.Globalization.CultureInfo.InvariantCulture)));
            Assert.False(child.WaitForExit(300));
            Assert.Equal('R', State(src, child.Id));

            Assert.True(ProcessSignals.SendVerified(child.Id, start, Signal.Kill));
            Assert.True(child.WaitForExit(10_000));
            Assert.Equal(128 + 9, child.ExitCode);
            Assert.False(ProcessSignals.IsAlive(child.Id, start, src));
            Assert.False(ProcessSignals.SendVerified(child.Id, start, Signal.Kill));
        }
        finally { Kill(child); }
    }

    private static System.Diagnostics.Process StartPinging() =>
        System.Diagnostics.Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 >nul")
        {
            UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory,
        })!;

    private static void Kill(System.Diagnostics.Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private static char? State(IProcSource src, int pid) => ProcParsers.ParseStat(src.ReadStat(pid))?.State;

    private static string[] Argv(byte[]? raw) => Encoding.UTF8.GetString(raw!).TrimEnd('\0').Split('\0');

    private static bool WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }
}
