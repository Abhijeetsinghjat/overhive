using Amc.Core.Events;

namespace Amc.Daemon.Collectors.Processes;

/// <summary>A main agent with a registry-reported pid.</summary>
public sealed record AgentProcessRef(string AgentId, int Pid, string? ProcStart, string? TerminalId = null);

/// <summary>A live `agentctl run` terminal whose child pid may be an ancestor of an agent.</summary>
public sealed record TerminalRef(string TerminalId, int Pid, string? ProcStart = null);

public sealed record ScanStats(int ProcCount, int Agents, int AgentsAlive, int Descendants, int Tracked, int Events);

public sealed record ScanResult(
    IReadOnlyList<LiveProcess> Processes,
    IReadOnlyDictionary<int, int> Ppids,
    IReadOnlyList<AgentEvent> Events,
    ScanStats Stats);

/// <summary>
/// Pure scan logic (no timers, no DI), so it can be tested against a fake /proc.
/// One full /proc pass per scan builds the ppid map; details (cmdline, cwd, RSS, tty, env) are
/// read only for agent processes and their descendants.
/// </summary>
public sealed class ProcessTracker(IProcSource proc, int maxDescendantsPerAgent = 64, int maxTrackedPerAgent = 32)
{
    public const string ExitedUnexpectedly = "Process exited unexpectedly";
    public const string DetachedReason = "Detached from agent process tree (still running)";

    private sealed class Tracked
    {
        public required string AgentId;
        public required ProcessData Data;
        public int Scans;
        public bool Emitted;
        public bool SeenThisScan;
    }

    private readonly Dictionary<(int Pid, long Start), (long Ticks, double At)> _cpu = [];
    private readonly Dictionary<(int Pid, long Start), Tracked> _tracked = [];
    private readonly Dictionary<(int Pid, long Start), IReadOnlyList<string>> _envCache = [];
    private readonly HashSet<string> _emitted = [];

    public ScanResult Scan(IReadOnlyList<AgentProcessRef> agents, IReadOnlyList<TerminalRef> terminals, double monoSeconds)
    {
        var stats = new Dictionary<int, ProcStat>();
        foreach (var pid in proc.ListPids())
            if (ProcParsers.ParseStat(proc.ReadStat(pid)) is { } st) stats[pid] = st;

        var ppids = stats.ToDictionary(kv => kv.Key, kv => kv.Value.Ppid);
        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, ppid) in ppids)
        {
            if (!children.TryGetValue(ppid, out var l)) children[ppid] = l = [];
            l.Add(pid);
        }
        foreach (var l in children.Values) l.Sort();

        var events = new List<AgentEvent>();
        var live = new List<LiveProcess>();
        var cpuSeen = new HashSet<(int, long)>();
        foreach (var t in _tracked.Values) t.SeenThisScan = false;

        // Agents verified alive; their pids bound the other agents' subtrees.
        var aliveAgents = new List<(AgentProcessRef Agent, ProcStat Stat)>();
        foreach (var a in agents)
        {
            stats.TryGetValue(a.Pid, out var st);
            var sameProcess = st is not null && st.State != 'Z' && (a.ProcStart is null || st.ProcStart == a.ProcStart);
            if (!sameProcess)
            {
                // Vanished, zombie, or pid reused by another process (starttime changed).
                var start = a.ProcStart ?? st?.ProcStart ?? "?";
                var key = $"proc:{a.Pid}:{start}:exit";
                if (_emitted.Add(key))
                    events.Add(Event(EventTypes.ProcessExited, a.AgentId, key,
                        new ProcessExitData(a.Pid, start, null, ExitedUnexpectedly)));
                continue;
            }
            aliveAgents.Add((a, st!));
        }
        var agentPids = aliveAgents.Select(x => x.Agent.Pid).ToHashSet();

        var totalDesc = 0;
        foreach (var (a, st) in aliveAgents)
        {
            var env = _envCache.TryGetValue((a.Pid, st.StartTime), out var cached)
                ? cached
                : _envCache[(a.Pid, st.StartTime)] = ProcParsers.EnvSummary(proc.ReadEnviron(a.Pid));
            var self = Build(st, a.AgentId, env, monoSeconds, cpuSeen);
            live.Add(self);

            var startKey = $"proc:{a.Pid}:{st.ProcStart}:start";
            if (_emitted.Add(startKey))
                events.Add(Event(EventTypes.ProcessStarted, a.AgentId, startKey, ToData(self)));

            // Terminal link: agent pid is (a descendant of) a terminal's child pid, or carries AMC_TERMINAL_ID.
            var termId = FindTerminal(a.Pid, ppids, stats, terminals) ?? ProcParsers.TerminalIdFromEnv(env);
            if (termId is not null && termId != a.TerminalId)
            {
                var linkKey = $"termlink:{a.AgentId}:{termId}";
                if (_emitted.Add(linkKey))
                    events.Add(Event(EventTypes.AgentUpdated, a.AgentId, linkKey, new AgentUpdatedData(TerminalId: termId)));
            }

            // Descendants (BFS, parents first), not crossing into another agent's subtree.
            var queue = new Queue<int>(children.GetValueOrDefault(a.Pid) ?? []);
            var count = 0;
            var trackedForAgent = _tracked.Values.Count(t => t.AgentId == a.AgentId && t.Emitted);
            while (queue.Count > 0 && count < maxDescendantsPerAgent)
            {
                var pid = queue.Dequeue();
                if (agentPids.Contains(pid) || !stats.TryGetValue(pid, out var cst) || cst.State == 'Z') continue;
                foreach (var c in children.GetValueOrDefault(pid) ?? []) queue.Enqueue(c);
                var lp = Build(cst, a.AgentId, [], monoSeconds, cpuSeen);
                live.Add(lp);
                count++;

                var key = (pid, cst.StartTime);
                if (!_tracked.TryGetValue(key, out var tr))
                    _tracked[key] = tr = new Tracked { AgentId = a.AgentId, Data = ToData(lp) };
                tr.SeenThisScan = true;
                tr.Scans++;
                // Only processes that live across ≥2 scans (long-running commands) become events.
                if (!tr.Emitted && tr.Scans >= 2 && trackedForAgent < maxTrackedPerAgent)
                {
                    tr.Emitted = true;
                    trackedForAgent++;
                    events.Add(Event(EventTypes.ProcessStarted, a.AgentId, $"proc:{pid}:{cst.ProcStart}:start", tr.Data));
                }
            }
            totalDesc += count;
        }

        // Descendants that disappeared from every agent tree.
        foreach (var (key, tr) in _tracked.Where(kv => !kv.Value.SeenThisScan).ToList())
        {
            _tracked.Remove(key);
            if (!tr.Emitted) continue;
            var stillAlive = stats.TryGetValue(key.Pid, out var st) && st.StartTime == key.Start && st.State != 'Z';
            events.Add(Event(EventTypes.ProcessExited, tr.AgentId, $"proc:{key.Pid}:{key.Start}:exit",
                new ProcessExitData(key.Pid, tr.Data.ProcStart, null, stillAlive ? DetachedReason : null)));
        }

        foreach (var k in _cpu.Keys.Where(k => !cpuSeen.Contains(k)).ToList()) _cpu.Remove(k);
        foreach (var k in _envCache.Keys.Where(k => !stats.TryGetValue(k.Pid, out var s) || s.StartTime != k.Start).ToList())
            _envCache.Remove(k);
        if (_emitted.Count > 50_000) _emitted.Clear(); // persisted dedupe keys still prevent duplicates

        return new ScanResult(live, ppids, events,
            new ScanStats(stats.Count, agents.Count, aliveAgents.Count, totalDesc, _tracked.Values.Count(t => t.Emitted), events.Count));
    }

    /// <summary>CPU% over the interval since the previous observation (100 = one full core).</summary>
    public static double CpuPercent(long prevTicks, double prevAt, long ticks, double at, long clockTicksPerSecond)
    {
        var dt = at - prevAt;
        if (dt <= 0 || ticks < prevTicks || clockTicksPerSecond <= 0) return 0;
        return Math.Round((ticks - prevTicks) / (double)clockTicksPerSecond / dt * 100.0, 1);
    }

    private static string? FindTerminal(int pid, Dictionary<int, int> ppids, Dictionary<int, ProcStat> stats, IReadOnlyList<TerminalRef> terminals)
    {
        if (terminals.Count == 0) return null;
        var cur = pid;
        for (var hops = 0; hops < 64 && cur > 1; hops++)
        {
            foreach (var t in terminals)
                if (t.Pid == cur && (t.ProcStart is null || (stats.TryGetValue(cur, out var s) && s.ProcStart == t.ProcStart)))
                    return t.TerminalId;
            if (!ppids.TryGetValue(cur, out cur)) break;
        }
        return null;
    }

    private LiveProcess Build(ProcStat st, string agentId, IReadOnlyList<string> env, double at, HashSet<(int, long)> cpuSeen)
    {
        var key = (st.Pid, st.StartTime);
        var cpu = 0.0;
        if (_cpu.TryGetValue(key, out var prev))
            cpu = CpuPercent(prev.Ticks, prev.At, st.CpuTicks, at, proc.ClockTicksPerSecond);
        _cpu[key] = (st.CpuTicks, at);
        cpuSeen.Add(key);

        var started = proc.BootTime + TimeSpan.FromSeconds(st.StartTime / (double)proc.ClockTicksPerSecond);
        return new LiveProcess(
            st.Pid, st.Ppid, st.ProcStart,
            ProcParsers.ParseCmdline(proc.ReadCmdline(st.Pid)) ?? "[" + st.Comm + "]",
            proc.ReadLink(st.Pid, "cwd"),
            ProcParsers.ParseTty(proc.ReadLink(st.Pid, "fd/0")),
            st.State, cpu, ProcParsers.ParseVmRssBytes(proc.ReadStatus(st.Pid)), started, agentId, env);
    }

    private static ProcessData ToData(LiveProcess p) => new(p.Pid, p.Ppid, p.ProcStart, p.Command, p.Cwd, p.AgentId, p.Tty);

    private static AgentEvent Event<T>(string type, string agentId, string dedupe, T payload) where T : class =>
        AgentEvent.Create(type, EventSources.Process, payload) with { AgentId = agentId, DedupeKey = dedupe };
}
