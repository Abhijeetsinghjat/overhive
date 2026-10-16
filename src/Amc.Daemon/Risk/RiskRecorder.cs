using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Risk;
using Amc.Core.State;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;

namespace Amc.Daemon.Risk;

/// <summary>Machine facts the classifier needs (home, OS, temp dir). Injectable for tests.</summary>
public sealed record RiskEnvironment(string? HomeDir, RiskOs Os, IReadOnlyList<string> TempDirs)
{
    public static RiskEnvironment Current() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } h ? h : null,
            RiskContext.CurrentOs, [Path.GetTempPath().TrimEnd('/', '\\')]);
}

/// <summary>
/// Flight recorder for risky actions. Runs <see cref="RiskClassifier"/> on
/// <list type="bullet">
/// <item>shell commands agents report (command.started from hooks, transcripts and the open protocol) — evidence reported;</item>
/// <item>file events (writes, deletes, reads) and tools pointed at absolute paths — evidence of the source event;</item>
/// <item>command lines of agents' descendant processes seen by the process collector — evidence observed.</item>
/// </list>
/// and publishes <c>risk.detected</c>. Dedupe key: agent + rule + evidence + hash of the resolved target path (or the matched command), so a repeated
/// command is recorded once per evidence source. Commands are already redacted when they reach the recorder.
/// Live-only: replaying the log at startup does not re-run it (findings are events, so they replay themselves).
/// </summary>
public sealed class RiskRecorder : BackgroundService
{
    public const string HealthName = "risk";
    private static readonly TimeSpan ProcessInterval = TimeSpan.FromSeconds(2);

    private readonly StateStore _state;
    private readonly IEventSink _sink;
    private readonly HealthRegistry _health;
    private readonly ILiveProcesses _processes;
    private readonly RiskEnvironment _env;
    private readonly ILogger<RiskRecorder> _log;
    private readonly HashSet<(int Pid, string Start)> _seenProcesses = [];

    public RiskRecorder(StateStore state, IEventSink sink, HealthRegistry health, ILiveProcesses processes, ILogger<RiskRecorder> log,
        RiskEnvironment? env = null)
    {
        _state = state; _sink = sink; _health = health; _processes = processes; _log = log;
        _env = env ?? RiskEnvironment.Current();
        _health.Register(HealthName);
        _state.Applied += OnApplied;
    }

    private void OnApplied(IReadOnlyCollection<EntityRef> changed, IReadOnlyList<AgentEvent> events)
    {
        try
        {
            if (!events.Any(IsCandidate)) return;
            var found = _state.Read(s => events.Where(IsCandidate).SelectMany(e => Detect(s, e, _env)).ToList());
            Publish(found);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Risk classification failed");
            _health.Increment(HealthName, "errors");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _health.Ok(HealthName, "watching commands and file events");
        using var timer = new PeriodicTimer(ProcessInterval);
        do
        {
            try
            {
                var procs = _processes.Snapshot();
                if (procs.Count == 0) continue;
                var found = _state.Read(s => DetectProcesses(s, procs, _seenProcesses, _env));
                Publish(found);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Risk scan of processes failed");
                _health.Increment(HealthName, "errors");
            }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private void Publish(List<AgentEvent> found)
    {
        foreach (var e in found)
            if (!_sink.TryPublish(e)) _health.Increment(HealthName, "dropped");
        if (found.Count == 0) return;
        _health.Increment(HealthName, "findings", found.Count);
        _health.Ok(HealthName, $"last finding {DateTimeOffset.UtcNow:HH:mm:ss} UTC");
    }

    // ---- pure detection (unit-tested) -----------------------------------------------------------

    public static bool IsCandidate(AgentEvent e) =>
        e.AgentId is not null && e.Type is EventTypes.CommandStarted or EventTypes.ToolStarted or EventTypes.FileCreated
            or EventTypes.FileModified or EventTypes.FileDeleted or EventTypes.FileRenamed or EventTypes.FileRead;

    /// <summary>Findings for one reported/observed event, as ready-to-publish <c>risk.detected</c> events.</summary>
    public static List<AgentEvent> Detect(WorldState s, AgentEvent e, RiskEnvironment env)
    {
        var result = new List<AgentEvent>();
        if (!IsCandidate(e)) return result;
        IReadOnlyList<RiskFinding> findings;
        string? command, toolUseId = null;
        if (e.Type == EventTypes.CommandStarted)
        {
            if (e.Payload<CommandData>() is not { } c) return result;
            var ctx = Context(s, e.AgentId!, c.Cwd, e.WorktreeId, env);
            findings = RiskClassifier.ClassifyCommand(c.Command, ctx);
            command = c.Command; toolUseId = c.ToolUseId;
        }
        else
        {
            var path = AbsolutePath(s, e);
            if (path is null) return result;
            var kind = e.Type switch
            {
                EventTypes.ToolStarted => "accessed",
                EventTypes.FileRead => "read",
                _ => e.Type["file.".Length..],
            };
            var ctx = Context(s, e.AgentId!, null, e.WorktreeId, env);
            findings = RiskClassifier.ClassifyFileEvent(kind, path, ctx);
            command = $"{kind} {path}";
            toolUseId = e.Type == EventTypes.ToolStarted ? e.Payload<ToolCallData>()?.ToolUseId : e.Payload<FileChangeData>()?.ToolUseId;
        }
        var evidence = e.Evidence == Evidence.Observed ? Evidence.Observed : Evidence.Reported;
        foreach (var f in findings)
            result.Add(ToEvent(e, f, evidence, command, e.Source, toolUseId, null));
        return result;
    }

    /// <summary>Findings for agents' descendant processes not classified before (<paramref name="seen"/> is updated and pruned).</summary>
    public static List<AgentEvent> DetectProcesses(WorldState s, IReadOnlyList<LiveProcess> procs, HashSet<(int, string)> seen, RiskEnvironment env)
    {
        var result = new List<AgentEvent>();
        var live = new HashSet<(int, string)>();
        foreach (var p in procs)
        {
            live.Add((p.Pid, p.ProcStart));
            if (!seen.Add((p.Pid, p.ProcStart))) continue;
            if (p.AgentId is null || !s.Agents.TryGetValue(p.AgentId, out var agent) || agent.Pid == p.Pid) continue;
            if (p.Command.StartsWith('[')) continue; // kernel-style name, no command line
            var ctx = Context(s, agent.Id, p.Cwd, null, env);
            var source = new AgentEvent
            {
                Type = EventTypes.ProcessStarted, Source = EventSources.Process, Evidence = Evidence.Observed,
                AgentId = agent.Id, SessionId = agent.SessionId, WorktreeId = ctx.Worktree?.Id, RepositoryId = ctx.Worktree?.RepositoryId,
                Ts = p.StartedAt, // when the command started running
            };
            foreach (var f in RiskClassifier.ClassifyCommand(p.Command, ctx.Context))
                result.Add(ToEvent(source, f, Evidence.Observed, p.Command, EventSources.Process, null, p.Pid));
        }
        seen.IntersectWith(live);
        return result;
    }

    private static AgentEvent ToEvent(AgentEvent src, RiskFinding f, Evidence evidence, string? command, string origin,
        string? toolUseId, int? pid)
    {
        // The resolved target when there is one (so `rm -rf ~/` reported and `rm -rf /home/u/` observed in its child agree), else the command.
        var key = f.Target ?? f.Span;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        return new AgentEvent
        {
            Type = EventTypes.RiskDetected, Source = EventSources.System, Evidence = evidence,
            AgentId = src.AgentId, SessionId = src.SessionId, WorktreeId = src.WorktreeId, RepositoryId = src.RepositoryId,
            Ts = src.Ts,
            DedupeKey = $"risk:{src.AgentId}:{f.RuleId}:{evidence.ToString().ToLowerInvariant()}:{hash}",
            Data = Json.ToObject(new RiskData(f.RuleId, f.Severity, f.Explanation, f.Span, f.Target, command, origin,
                src.Type, src.Seq > 0 ? src.Seq : null, toolUseId, pid)),
        };
    }

    private sealed record Ctx(RiskContext Context, Worktree? Worktree)
    {
        public static implicit operator RiskContext(Ctx c) => c.Context;
    }

    private static Ctx Context(WorldState s, string agentId, string? cwd, string? worktreeId, RiskEnvironment env)
    {
        s.Agents.TryGetValue(agentId, out var agent);
        // The agent and its ancestors (a sub-agent works in its session's worktrees too).
        var chain = new List<Agent>();
        for (var a = agent; a is not null && chain.Count < 32 && !chain.Contains(a); a = a.ParentId is { } pid ? s.Agents.GetValueOrDefault(pid) : null)
            chain.Add(a);
        cwd ??= chain.Select(a => a.Cwd).FirstOrDefault(c => c is not null);

        var live = s.Worktrees.Values.Where(w => !w.Removed).ToList();
        Worktree? cwdWt = null;
        if (cwd is not null)
            cwdWt = live.Where(w => cwd == w.Root || cwd.StartsWith(w.Root.TrimEnd('/', '\\') + "/") || cwd.StartsWith(w.Root.TrimEnd('/', '\\') + "\\"))
                .OrderByDescending(w => w.Root.Length).FirstOrDefault();
        var eventWt = worktreeId is not null ? s.Worktrees.GetValueOrDefault(worktreeId) : null;
        var wt = cwdWt ?? eventWt ?? (agent?.CurrentWorktreeId is { } cw ? s.Worktrees.GetValueOrDefault(cw) : null);

        var roots = chain.SelectMany(a => a.Worktrees.Keys).Append(cwdWt?.Id).Append(eventWt?.Id)
            .Where(id => id is not null).Distinct()
            .Select(id => s.Worktrees.GetValueOrDefault(id!)).Where(w => w is { Removed: false }).Select(w => w!.Root).ToList();
        return new Ctx(new RiskContext
        {
            Cwd = cwd, HomeDir = env.HomeDir, Os = env.Os, TempDirs = env.TempDirs,
            WorktreeRoots = roots, KnownWorktreeRoots = live.Select(w => w.Root).ToList(),
            Branch = wt?.Branch ?? agent?.GitBranch,
            HasUncommittedChanges = wt?.Dirty,
        }, wt);
    }

    private static string? AbsolutePath(WorldState s, AgentEvent e)
    {
        string? abs = e.Type == EventTypes.ToolStarted ? null : e.Payload<FileChangeData>()?.AbsolutePath;
        if (abs is not null) return abs;
        if (e.FilePath is null) return null;
        if (e.WorktreeId is not null && s.Worktrees.TryGetValue(e.WorktreeId, out var w))
            return w.Root.TrimEnd('/') + "/" + e.FilePath;
        return Path.IsPathRooted(e.FilePath) ? e.FilePath : null;
    }
}
