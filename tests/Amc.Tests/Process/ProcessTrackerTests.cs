using System.Text;
using Amc.Core.Events;
using Amc.Daemon.Collectors.Processes;

namespace Amc.Tests.Process;

internal sealed class FakeProc : IProcSource
{
    public sealed record P(int Pid, int Ppid, long Start, string Cmd, long Ticks = 0, char State = 'S', string? Env = null, string? Fd0 = null);

    public Dictionary<int, P> Procs { get; } = [];

    public FakeProc Add(int pid, int ppid, long start, string cmd = "cmd", long ticks = 0, string? env = null, char state = 'S')
    {
        Procs[pid] = new P(pid, ppid, start, cmd, ticks, state, env);
        return this;
    }

    public IEnumerable<int> ListPids() => Procs.Keys.ToList();
    public string? ReadStat(int pid) => Procs.TryGetValue(pid, out var p)
        ? $"{pid} ({p.Cmd.Split(' ')[0]}) {p.State} {p.Ppid} {pid} {pid} 0 -1 0 0 0 0 0 {p.Ticks} 0 0 0 20 0 1 0 {p.Start} 0 0"
        : null;
    public byte[]? ReadCmdline(int pid) => Procs.TryGetValue(pid, out var p) ? Encoding.UTF8.GetBytes(p.Cmd.Replace(' ', '\0') + "\0") : null;
    public string? ReadStatus(int pid) => Procs.ContainsKey(pid) ? "VmRSS:\t 100 kB\n" : null;
    public string? ReadLink(int pid, string name) => Procs.TryGetValue(pid, out var p) ? (name == "cwd" ? "/work" : p.Fd0) : null;
    public byte[]? ReadEnviron(int pid) => Procs.TryGetValue(pid, out var p) && p.Env is not null ? Encoding.UTF8.GetBytes(p.Env) : null;
    public long ClockTicksPerSecond => 100;
    public DateTimeOffset BootTime { get; } = DateTimeOffset.UnixEpoch;
}

public class ProcessTrackerTests
{
    private static readonly AgentProcessRef Agent = new("claude:s1", 100, "5000");

    private static FakeProc Tree() => new FakeProc()
        .Add(1, 0, 1, "init")
        .Add(50, 1, 10, "bash")
        .Add(100, 50, 5000, "claude --resume")
        .Add(200, 100, 6000, "bash -c dotnet test")
        .Add(300, 200, 6001, "dotnet test")
        .Add(400, 1, 7000, "unrelated");

    [Fact]
    public void Descendants_AreCollected_WithOwningAgent()
    {
        var t = new ProcessTracker(Tree());
        var r = t.Scan([Agent], [], 1.0);
        Assert.Equal([100, 200, 300], r.Processes.Select(p => p.Pid).Order());
        Assert.All(r.Processes, p => Assert.Equal("claude:s1", p.AgentId));
        Assert.Equal("claude --resume", r.Processes.Single(p => p.Pid == 100).Command);
        Assert.Equal(100 * 1024, r.Processes[0].RssBytes);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(50), r.Processes.Single(p => p.Pid == 100).StartedAt);
        Assert.Equal(EventTypes.ProcessStarted, Assert.Single(r.Events).Type); // agent's own start only
    }

    [Fact]
    public void NestedAgent_SubtreeBelongsToInnerAgent()
    {
        var fake = Tree().Add(500, 300, 8000, "claude").Add(600, 500, 8001, "inner-child");
        var r = new ProcessTracker(fake).Scan([Agent, new AgentProcessRef("claude:s2", 500, "8000")], [], 1.0);
        Assert.Equal("claude:s2", r.Processes.Single(p => p.Pid == 600).AgentId);
        Assert.Equal("claude:s2", r.Processes.Single(p => p.Pid == 500).AgentId);
        Assert.Single(r.Processes, p => p.Pid == 500);
    }

    [Fact]
    public void PidReuse_StarttimeMismatch_EmitsExitOnce()
    {
        var fake = Tree();
        fake.Add(100, 50, 9999, "some other program"); // same pid, different starttime
        var t = new ProcessTracker(fake);
        var r = t.Scan([Agent], [], 1.0);
        var e = Assert.Single(r.Events);
        Assert.Equal(EventTypes.ProcessExited, e.Type);
        Assert.Equal("claude:s1", e.AgentId);
        Assert.Equal("proc:100:5000:exit", e.DedupeKey);
        var p = e.Payload<ProcessExitData>()!;
        Assert.Equal(100, p.Pid);
        Assert.Equal("5000", p.ProcStart);
        Assert.Equal(ProcessTracker.ExitedUnexpectedly, p.Reason);
        Assert.Empty(r.Processes); // the impostor is not shown as the agent

        Assert.Empty(t.Scan([Agent], [], 3.0).Events);
    }

    [Fact]
    public void VanishedPid_EmitsExit_ZombieToo()
    {
        var fake = Tree();
        fake.Procs.Remove(100);
        Assert.Equal("proc:100:5000:exit", Assert.Single(new ProcessTracker(fake).Scan([Agent], [], 1).Events).DedupeKey);

        var zombie = Tree().Add(100, 50, 5000, "claude", state: 'Z');
        Assert.Equal(EventTypes.ProcessExited, Assert.Single(new ProcessTracker(zombie).Scan([Agent], [], 1).Events).Type);
    }

    [Fact]
    public void LongRunningDescendant_StartedAfterTwoScans_ThenExited()
    {
        var fake = Tree();
        var t = new ProcessTracker(fake);
        Assert.DoesNotContain(t.Scan([Agent], [], 0).Events, e => e.Data["pid"]!.GetValue<int>() != 100);
        var second = t.Scan([Agent], [], 2).Events;
        Assert.Equal(["proc:200:6000:start", "proc:300:6001:start"], second.Select(e => e.DedupeKey!).Order());
        Assert.Empty(t.Scan([Agent], [], 4).Events);

        fake.Procs.Remove(300);
        var exit = Assert.Single(t.Scan([Agent], [], 6).Events);
        Assert.Equal(EventTypes.ProcessExited, exit.Type);
        Assert.Equal("proc:300:6001:exit", exit.DedupeKey);
        Assert.Equal("claude:s1", exit.AgentId);
    }

    [Fact]
    public void ShortLivedDescendant_ProducesNoEvents()
    {
        var fake = Tree();
        var t = new ProcessTracker(fake);
        t.Scan([Agent], [], 0);
        fake.Add(700, 100, 9000, "git status");
        t.Scan([Agent], [], 2);
        fake.Procs.Remove(700);
        Assert.DoesNotContain(t.Scan([Agent], [], 4).Events, e => e.Data["pid"]!.GetValue<int>() == 700);
    }

    [Fact]
    public void Descendants_AreCapped()
    {
        var fake = Tree();
        for (var i = 0; i < 100; i++) fake.Add(1000 + i, 100, 20000 + i, "worker");
        var t = new ProcessTracker(fake, maxDescendantsPerAgent: 10, maxTrackedPerAgent: 3);
        var r = t.Scan([Agent], [], 0);
        Assert.Equal(11, r.Processes.Count); // agent + 10
        var r2 = t.Scan([Agent], [], 2);
        Assert.Equal(3, r2.Events.Count(e => e.Type == EventTypes.ProcessStarted));
    }

    [Fact]
    public void Cpu_ComputedFromTickDeltas()
    {
        var fake = Tree();
        var t = new ProcessTracker(fake);
        t.Scan([Agent], [], 10.0);
        fake.Add(100, 50, 5000, "claude", ticks: 150); // 1.5 s CPU over 2 s
        var p = t.Scan([Agent], [], 12.0).Processes.Single(x => x.Pid == 100);
        Assert.Equal(75.0, p.CpuPercent, 1);
    }

    [Fact]
    public void TerminalLink_ByAncestry_AndByEnv()
    {
        var t = new ProcessTracker(Tree());
        var r = t.Scan([Agent], [new TerminalRef("T1", 50, "10")], 0);
        var link = Assert.Single(r.Events, e => e.Type == EventTypes.AgentUpdated);
        Assert.Equal("T1", link.Payload<AgentUpdatedData>()!.TerminalId);
        Assert.Equal("claude:s1", link.AgentId);
        Assert.DoesNotContain(t.Scan([Agent], [new TerminalRef("T1", 50, "10")], 2).Events, e => e.Type == EventTypes.AgentUpdated);

        // Wrong starttime for the terminal pid: no link.
        Assert.DoesNotContain(new ProcessTracker(Tree()).Scan([Agent], [new TerminalRef("T1", 50, "11")], 0).Events,
            e => e.Type == EventTypes.AgentUpdated);

        // Already linked: nothing.
        Assert.DoesNotContain(new ProcessTracker(Tree()).Scan([Agent with { TerminalId = "T1" }], [new TerminalRef("T1", 50)], 0).Events,
            e => e.Type == EventTypes.AgentUpdated);

        var envFake = Tree().Add(100, 50, 5000, "claude", env: "AMC_TERMINAL_ID=T9\0HOME=/h\0SECRET_TOKEN=x\0");
        var r3 = new ProcessTracker(envFake).Scan([Agent], [], 0);
        Assert.Equal("T9", Assert.Single(r3.Events, e => e.Type == EventTypes.AgentUpdated).Payload<AgentUpdatedData>()!.TerminalId);
        var summary = r3.Processes.Single(p => p.Pid == 100).EnvSummary;
        Assert.Contains("SECRET_TOKEN", summary);
        Assert.DoesNotContain(summary, s => s.StartsWith("SECRET_TOKEN="));
    }

    [Fact]
    public void AgentWithoutProcStart_IsAcceptedByPid()
    {
        var r = new ProcessTracker(Tree()).Scan([Agent with { ProcStart = null }], [], 0);
        Assert.Equal("proc:100:5000:start", Assert.Single(r.Events).DedupeKey);
    }

    [Fact]
    public void LiveTable_Tree_ParentsFirst()
    {
        var fake = Tree();
        var r = new ProcessTracker(fake).Scan([Agent], [], 0);
        var table = new LiveProcessTable();
        table.Update(r.Processes, r.Ppids);
        Assert.Equal([100, 200, 300], table.Tree(100));
        Assert.Equal([400], table.Tree(400));
        Assert.Equal("dotnet test", table.Get(300)!.Command);
        Assert.Null(table.Get(400));
    }

    [Fact]
    public void RealProc_SelfAsAgent()
    {
        var self = Environment.ProcessId;
        var st = ProcParsers.ParseStat(ProcSources.Default.ReadStat(self))!;
        var r = new ProcessTracker(ProcSources.Default).Scan([new AgentProcessRef("test:self", self, st.ProcStart)], [], 0);
        var me = r.Processes.Single(p => p.Pid == self);
        Assert.True(me.RssBytes > 0);
        Assert.Equal(Environment.CurrentDirectory, me.Cwd);
        Assert.NotEmpty(me.EnvSummary);
    }
}
