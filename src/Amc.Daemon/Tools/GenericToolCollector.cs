using System.Text;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Collectors.Processes;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;

namespace Amc.Daemon.Tools;

/// <summary>
/// Generic integration for any AI coding tool without a native adapter: finds its running processes and
/// reports what the OS and Git can observe (process, working directory, worktree, exit). Whether it is
/// working or idle is inferred from CPU activity of its process tree and labelled as such.
/// </summary>
public sealed class GenericToolCollector(ToolRegistry tools, IEventSink sink, IWorktreeIndex worktrees, HealthRegistry health,
    TimeProvider time, ILogger<GenericToolCollector> log) : BackgroundService
{
    public const string HealthName = "tools";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(12);
    private static readonly string[] ScriptExt = [".js", ".mjs", ".cjs", ".py", ".ts"];
    private static readonly HashSet<string> Interpreters = ["node", "nodejs", "bun", "deno", "python", "python3", "uv", "npx"];

    private sealed class Tracked
    {
        public required string AgentId; public required AgentTool Tool; public required int Pid; public required string ProcStart;
        public string? Cwd; public string? WorktreeId; public long LastCpu; public DateTimeOffset LastBusy; public bool? Busy;
    }

    private readonly ProcFs _proc = new();
    /// <summary>Latest scan: running tool processes (pid → tool, cwd, procStart) and which ones a hook session claimed.</summary>
    private volatile IReadOnlyList<(int Pid, string Tool, string? Cwd, string ProcStart)> _seen = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _claims = new();
    /// <summary>New processes wait this long before becoming generic agents, so a hook session can claim them first.</summary>
    private static readonly TimeSpan ClaimGrace = TimeSpan.FromSeconds(6);
    private readonly Dictionary<string, DateTimeOffset> _firstSeen = [];

    /// <summary>
    /// Links a hook-reported session to its process: the one unclaimed process of that tool running in that
    /// directory. Returns it (so the session gets a pid for pause/stop) or null when none or ambiguous.
    /// </summary>
    public (int Pid, string ProcStart)? TryClaim(string tool, string cwd, string agentId)
    {
        if (_claims.Values.Contains(agentId)) return null; // already linked
        var c = _seen.Where(x => x.Tool == tool && x.Cwd == cwd && !_claims.ContainsKey(x.Pid)).ToList();
        if (c.Count != 1) return null;
        _claims[c[0].Pid] = agentId;
        return (c[0].Pid, c[0].ProcStart);
    }
    private readonly Dictionary<string, Tracked> _tracked = [];
    /// <summary>Our uid as /proc prints it; null on Windows, which has no uids (and no libc to ask).</summary>
    private static readonly string? Uid = OperatingSystem.IsWindows() ? null : GetUid().ToString(System.Globalization.CultureInfo.InvariantCulture);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        health.Register(HealthName, Interval * 10);
        var generic = tools.All.Where(t => t.Adapter is null).ToList();
        if (generic.Count == 0) { health.Disabled(HealthName, "no generic tools configured"); return; }
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try { await ScanAsync(generic, ct); health.Ok(HealthName, $"{_tracked.Count} generic agent(s): {string.Join(", ", generic.Select(t => t.Name))}"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Tool scan failed"); health.Degraded(HealthName, ex.Message); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task ScanAsync(List<AgentTool> generic, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var stats = new Dictionary<int, ProcStat>();
        foreach (var pid in _proc.ListPids())
            if (ProcParsers.ParseStat(_proc.ReadStat(pid)) is { } st) stats[pid] = st;

        // Match tool processes; a match whose ancestor is the same tool is a worker of that instance, not a new agent.
        var matches = new Dictionary<int, AgentTool>();
        foreach (var (pid, st) in stats)
            if (Match(pid, st, generic) is { } tool && OwnedByMe(pid)) matches[pid] = tool;
        foreach (var pid in matches.Keys.ToList())
            for (var p = stats[pid].Ppid; p > 1 && stats.TryGetValue(p, out var anc); p = anc.Ppid)
                if (matches.TryGetValue(p, out var t) && t.Id == matches[pid].Id) { matches.Remove(pid); break; }

        _seen = matches.Select(m => (m.Key, m.Value.Id, _proc.ReadLink(m.Key, "cwd"), stats[m.Key].ProcStart)).ToList();
        foreach (var dead in _claims.Keys.Where(k => !matches.ContainsKey(k)).ToList()) _claims.TryRemove(dead, out _);

        var seen = new HashSet<string>();
        foreach (var (pid, tool) in matches)
        {
            var st = stats[pid];
            var id = $"{tool.Id}:p{pid}-{st.ProcStart}";
            // A process owned by a hook-reported session is that session, not a separate generic agent.
            if (_claims.ContainsKey(pid)) continue;
            if (!_tracked.ContainsKey(id))
            {
                if (!_firstSeen.TryGetValue(id, out var first)) _firstSeen[id] = first = now;
                if (now - first < ClaimGrace) { seen.Add(id); continue; }
            }
            seen.Add(id);
            var cwd = _proc.ReadLink(pid, "cwd");
            var cpu = TreeCpu(pid, stats);
            if (!_tracked.TryGetValue(id, out var t))
            {
                _tracked[id] = t = new Tracked { AgentId = id, Tool = tool, Pid = pid, ProcStart = st.ProcStart, LastCpu = cpu, LastBusy = now };
                await sink.PublishAsync(new AgentEvent
                {
                    Type = EventTypes.AgentCreated, Source = EventSources.Process, Evidence = Evidence.Observed, AgentId = id,
                    DedupeKey = $"created:{id}",
                    Data = Json.ToObject(new AgentCreatedData(AgentKind.Main, tool.Id, AgentType: tool.Name,
                        Title: $"{tool.Name} in {Path.GetFileName(cwd?.TrimEnd('/', Path.DirectorySeparatorChar) ?? "") ?? "?"}", Cwd: cwd, Pid: pid, ProcStart: st.ProcStart)),
                }, ct);
            }
            if (cwd != t.Cwd)
            {
                t.Cwd = cwd;
                var hit = cwd is null ? null : await worktrees.ResolveAsync(cwd, ct);
                t.WorktreeId = hit?.WorktreeId;
                await sink.PublishAsync(new AgentEvent
                {
                    Type = EventTypes.AgentUpdated, Source = EventSources.Process, Evidence = Evidence.Observed, AgentId = id,
                    WorktreeId = hit?.WorktreeId, RepositoryId = hit?.RepositoryId, Data = Json.ToObject(new AgentUpdatedData(Cwd: cwd)),
                }, ct);
            }
            // Busy/idle from CPU of the whole process tree (the tool plus commands it runs).
            var busyNow = cpu - t.LastCpu > _proc.ClockTicksPerSecond * Interval.TotalSeconds * 0.03;
            t.LastCpu = cpu;
            if (busyNow) t.LastBusy = now;
            var busy = busyNow || now - t.LastBusy < IdleAfter;
            if (busy != t.Busy)
            {
                t.Busy = busy;
                if (t.WorktreeId is { } wt)
                    worktrees.NotifyActivity(new WorktreeActivity(wt, id, "proc:" + id, ActivityKinds.Shell, busy ? ActivityKinds.Start : ActivityKinds.End, now));
                await sink.PublishAsync(new AgentEvent
                {
                    Type = EventTypes.AgentStatusChanged, Source = EventSources.Process, Evidence = Evidence.Inferred, AgentId = id,
                    Data = Json.ToObject(new AgentStatusData(busy ? AgentStatus.Running : AgentStatus.Waiting,
                        busy ? "Process tree is using CPU (inferred busy)" : "No process activity for a while (inferred idle)")),
                }, ct);
            }
        }

        foreach (var k in _firstSeen.Keys.Where(k => !seen.Contains(k)).ToList()) _firstSeen.Remove(k);
        foreach (var gone in _tracked.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var t = _tracked[gone];
            _tracked.Remove(gone);
            var linked = _claims.TryGetValue(t.Pid, out var owner) ? owner : null;
            if (t.WorktreeId is { } wt)
                worktrees.NotifyActivity(new WorktreeActivity(wt, gone, "proc:" + gone, ActivityKinds.Shell, ActivityKinds.End, now));
            await sink.PublishAsync(new AgentEvent
            {
                Type = EventTypes.AgentCompleted, Source = EventSources.Process, Evidence = Evidence.Observed, AgentId = gone,
                DedupeKey = $"end:{gone}", Data = Json.ToObject(new AgentStatusData(AgentStatus.Completed,
                    linked is null ? "Process exited" : $"Continued as its hook-reported session ({linked})")),
            }, ct);
        }
    }

    private AgentTool? Match(int pid, ProcStat st, List<AgentTool> generic)
    {
        var raw = _proc.ReadCmdline(pid);
        if (raw is null || raw.Length == 0) return null;
        var argv = Encoding.UTF8.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length == 0) return null;
        var exe = Path.GetFileName(argv[0]);
        // Script tools run as `node …/bin/codex`: use the first non-flag argument after the interpreter.
        var script = Interpreters.Contains(exe) ? argv.Skip(1).Where(a => !a.StartsWith('-')).Select(Path.GetFileName).FirstOrDefault() : null;
        if (script is not null && ScriptExt.FirstOrDefault(x => script.EndsWith(x, StringComparison.Ordinal)) is { } ext) script = script[..^ext.Length];
        foreach (var t in generic)
            if (t.ProcessNames.Any(n => n == exe || n == st.Comm || n == script)) return t;
        return null;
    }

    private static long TreeCpu(int root, Dictionary<int, ProcStat> stats)
    {
        long sum = 0;
        var children = stats.Values.ToLookup(s => s.Ppid);
        var stack = new Stack<int>([root]);
        var guard = 0;
        while (stack.Count > 0 && guard++ < 2000)
        {
            var p = stack.Pop();
            if (stats.TryGetValue(p, out var s)) sum += s.CpuTicks;
            foreach (var c in children[p]) stack.Push(c.Pid);
        }
        return sum;
    }

    /// <summary>Only our own processes are agents. Ownership comes from the /proc status Uid line, so without one it is false.</summary>
    private bool OwnedByMe(int pid)
    {
        if (Uid is null) return false;
        var status = _proc.ReadStatus(pid);
        var line = status?.Split('\n').FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal));
        return line is not null && line.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) == Uid;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}
