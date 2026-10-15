using System.Text;
using Amc.Core.Security;
using Amc.Daemon.Collectors.Processes;

namespace Amc.Tests.Process;

public class ProcParsersTests
{
    private static string Stat(int pid, string comm, char state = 'S', int ppid = 1, long utime = 10, long stime = 5, long start = 123456) =>
        $"{pid} ({comm}) {state} {ppid} {pid} {pid} 34817 {pid} 4194560 100 0 0 0 {utime} {stime} 0 0 20 0 1 0 {start} 1000000 200 18446744073709551615 1 1 0 0 0 0 0 0 0 0 0 0 17 3 0 0 0 0 0";

    [Fact]
    public void ParseStat_SimpleComm()
    {
        var st = ProcParsers.ParseStat(Stat(42, "bash", ppid: 7, utime: 11, stime: 22, start: 999))!;
        Assert.Equal(42, st.Pid);
        Assert.Equal("bash", st.Comm);
        Assert.Equal('S', st.State);
        Assert.Equal(7, st.Ppid);
        Assert.Equal(11, st.UTime);
        Assert.Equal(22, st.STime);
        Assert.Equal(33, st.CpuTicks);
        Assert.Equal("999", st.ProcStart);
    }

    [Theory]
    [InlineData("my proc")]
    [InlineData("a) (b")]
    [InlineData("weird) S 1 2 3 (x")]
    [InlineData(")")]
    [InlineData("")]
    public void ParseStat_CommWithSpacesAndParens(string comm)
    {
        var st = ProcParsers.ParseStat(Stat(1234, comm, 'R', ppid: 99, start: 777))!;
        Assert.Equal(comm, st.Comm);
        Assert.Equal('R', st.State);
        Assert.Equal(99, st.Ppid);
        Assert.Equal(777, st.StartTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("12 (x) S 1 2")]
    public void ParseStat_Malformed_ReturnsNull(string? content) => Assert.Null(ProcParsers.ParseStat(content));

    [Fact]
    public void ParseStat_RealSelf()
    {
        var st = ProcParsers.ParseStat(ProcSources.Default.ReadStat(Environment.ProcessId));
        Assert.NotNull(st);
        Assert.Equal(Environment.ProcessId, st.Pid);
        Assert.True(st.StartTime > 0);
    }

    [Fact]
    public void Cmdline_NulSeparated_QuotedAndRedacted()
    {
        var raw = Encoding.UTF8.GetBytes("curl\0-H\0Authorization: Bearer abcdefghijklmnopqrstuvwxyz\0https://x\0");
        var cmd = ProcParsers.ParseCmdline(raw)!;
        Assert.StartsWith("curl -H '", cmd);
        Assert.DoesNotContain("abcdefghijklmnop", cmd);
        Assert.Contains(Redactor.Mask, cmd);
        Assert.Null(ProcParsers.ParseCmdline([]));
        Assert.Null(ProcParsers.ParseCmdline(null));
    }

    [Fact]
    public void VmRss_And_Tty()
    {
        Assert.Equal(2048L * 1024, ProcParsers.ParseVmRssBytes("Name:\tx\nVmRSS:\t    2048 kB\nThreads: 1\n"));
        Assert.Equal(0, ProcParsers.ParseVmRssBytes("Name:\tkthread\n"));
        Assert.Equal("/dev/pts/3", ProcParsers.ParseTty("/dev/pts/3"));
        Assert.Null(ProcParsers.ParseTty("pipe:[1234]"));
        Assert.Null(ProcParsers.ParseTty("/dev/null"));
    }

    [Fact]
    public void EnvSummary_ValuesOnlyForSafeNames()
    {
        var env = Encoding.UTF8.GetBytes("HOME=/home/u\0ANTHROPIC_API_KEY=sk-ant-supersecretvalue123456789\0MY_PRIVATE=hidden\0AMC_TERMINAL_ID=T1\0TERM=xterm\0");
        var s = ProcParsers.EnvSummary(env);
        Assert.Contains("HOME=/home/u", s);
        Assert.Contains("TERM=xterm", s);
        Assert.Contains("AMC_TERMINAL_ID=T1", s);
        Assert.Contains("ANTHROPIC_API_KEY", s);
        Assert.Contains("MY_PRIVATE", s);
        Assert.DoesNotContain(s, x => x.Contains("supersecret") || x.Contains("hidden"));
        Assert.Equal("T1", ProcParsers.TerminalIdFromEnv(s));
    }

    [Theory]
    [InlineData(0, 0.0, 50, 1.0, 100, 50.0)]     // half a core
    [InlineData(100, 10.0, 500, 12.0, 100, 200.0)] // two cores
    [InlineData(10, 1.0, 10, 3.0, 100, 0.0)]     // idle
    [InlineData(10, 1.0, 5, 3.0, 100, 0.0)]      // counter went backwards (pid reuse) → 0
    [InlineData(0, 1.0, 50, 1.0, 100, 0.0)]      // no elapsed time
    [InlineData(0, 0.0, 250, 1.0, 250, 100.0)]   // other USER_HZ
    public void CpuPercent(long t0, double a0, long t1, double a1, long hz, double expected) =>
        Assert.Equal(expected, ProcessTracker.CpuPercent(t0, a0, t1, a1, hz), 1);
}
