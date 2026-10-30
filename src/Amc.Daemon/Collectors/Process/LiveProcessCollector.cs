using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Platform;

namespace Amc.Daemon.Collectors.Processes;

/// <summary>Volatile process overlay (agent pids + descendants). Not persisted.</summary>
public sealed class LiveProcessTable : ILiveProcesses
{
    private sealed record Snap(IReadOnlyList<LiveProcess> List, Dictionary<int, LiveProcess> ByPid, Dictionary<int, List<int>> Children);

    private volatile Snap _snap = new([], [], []);

    public IReadOnlyList<LiveProcess> Snapshot() => _snap.List;
    public LiveProcess? Get(int pid) => _snap.ByPid.GetValueOrDefault(pid);

    /// <summary>pid followed by its live descendants in BFS order (parents before children), from the last full /proc scan.</summary>
    public IReadOnlyList<int> Tree(int pid)
    {
        var children = _snap.Children;
        var result = new List<int> { pid };
        var seen = new HashSet<int> { pid };
        for (var i = 0; i < result.Count && result.Count < 10_000; i++)
            foreach (var c in children.GetValueOrDefault(result[i]) ?? [])
                if (seen.Add(c)) result.Add(c);
        return result;
    }

    public void Update(IReadOnlyList<LiveProcess> list, IReadOnlyDictionary<int, int> ppids)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, ppid) in ppids)
        {
            if (!children.TryGetValue(ppid, out var l)) children[ppid] = l = [];
            l.Add(pid);
        }
        var byPid = new Dictionary<int, LiveProcess>();
        foreach (var p in list) byPid.TryAdd(p.Pid, p);
        _snap = new Snap(list, byPid, children);
    }
}

/// <summary>Live terminals known to the terminal hub (for terminal ↔ agent linking).</summary>
public interface ITerminalDirectory
{
    IReadOnlyList<TerminalRef> ActiveTerminals();
}

/// <summary>
/// Scans /proc every 2 s for main agents (pid + procStart from the registry) and their
/// descendants; maintains <see cref="ILiveProcesses"/>, pushes the live overlay at ≤1 Hz and
/// emits process.started / process.exited / agent.updated{TerminalId} events.
/// </summary>
public sealed class LiveProcessCollector(
    StateStore state, IEventSink sink, Broadcaster broadcaster, HealthRegistry health,
    LiveProcessTable table, IProcSource proc, IServiceProvider services, ILogger<LiveProcessCollector> log) : BackgroundService
{
    public const string HealthName = "processes";
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinLivePush = TimeSpan.FromSeconds(1);

    private readonly ProcessTracker _tracker = new(proc);
    private DateTimeOffset _lastPush = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!(OperatingSystem.IsMacOS() || (OperatingSystem.IsWindows() && WinProc.IsSupported)
              || (OperatingSystem.IsLinux() && Directory.Exists("/proc/self"))))
        {
            health.Disabled(HealthName, "process information not available on this OS yet");
            return;
        }
        health.Register(HealthName, Interval * 5);
        var terminals = services.GetService<ITerminalDirectory>();
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { RunOnce(terminals); }
            catch (Exception e)
            {
                log.LogWarning(e, "Process scan failed");
                health.Increment(HealthName, "errors");
                health.Degraded(HealthName, "scan failed: " + e.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    internal void RunOnce(ITerminalDirectory? terminals)
    {
        var sw = Stopwatch.StartNew();
        var agents = state.Read(s => s.Agents.Values
            .Where(a => a.Kind == AgentKind.Main && a.Pid is > 1 && !a.IsTerminal)
            .Select(a => new AgentProcessRef(a.Id, a.Pid!.Value, a.ProcStart, a.TerminalId))
            .ToList());
        var result = _tracker.Scan(agents, terminals?.ActiveTerminals() ?? [], Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        table.Update(result.Processes, result.Ppids);
        foreach (var e in result.Events)
            if (!sink.TryPublish(e)) health.Increment(HealthName, "dropped");

        var now = DateTimeOffset.UtcNow;
        if (now - _lastPush >= MinLivePush && broadcaster.ClientCount > 0)
        {
            _lastPush = now;
            var msg = new JsonObject
            {
                ["t"] = "live",
                ["processes"] = JsonSerializer.SerializeToNode(result.Processes, Json.Options),
            };
            broadcaster.PublishLive("live", msg.ToJsonString(Json.Options));
        }

        var st = result.Stats;
        health.Increment(HealthName, "scans");
        health.Increment(HealthName, "events", st.Events);
        health.SetCounter(HealthName, "proc_entries", st.ProcCount);
        health.SetCounter(HealthName, "agents", st.Agents);
        health.SetCounter(HealthName, "agents_alive", st.AgentsAlive);
        health.SetCounter(HealthName, "descendants", st.Descendants);
        health.SetCounter(HealthName, "tracked_commands", st.Tracked);
        health.SetCounter(HealthName, "scan_ms", sw.ElapsedMilliseconds);
        health.Ok(HealthName, $"{st.AgentsAlive}/{st.Agents} agent process(es) alive, {st.Descendants} descendant(s)");
    }
}
