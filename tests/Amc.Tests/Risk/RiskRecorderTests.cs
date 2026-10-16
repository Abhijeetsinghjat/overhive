using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Risk;
using Amc.Core.State;
using Amc.Daemon;
using Amc.Daemon.Risk;
using Amc.Tests.Core;

namespace Amc.Tests.Risk;

/// <summary>Recorder detection (pure parts), dedupe keys and the reducer projection of risk.detected.</summary>
public class RiskRecorderTests
{
    private const string Agent = "claude:s1";
    private static readonly RiskEnvironment Env = new("/home/u", RiskOs.Linux, ["/tmp"]);

    private static (WorldState S, EventFactory F) Setup(string branch = "main")
    {
        var f = new EventFactory();
        var s = new WorldState();
        Reducer.ApplyBatch(s, [
            f.Make(EventTypes.AgentCreated, new AgentCreatedData(AgentKind.Main, "claude", Cwd: "/home/u/dev/app", Pid: 100, ProcStart: "1"), Agent),
            f.Make(EventTypes.WorktreeChanged, new WorktreeData("/home/u/dev/app", "repo:1", branch, "abc", Modified: 1), wt: "wt:1", repo: "repo:1", evidence: Evidence.Observed),
        ]);
        return (s, f);
    }

    private static AgentEvent Command(EventFactory f, string cmd, string? cwd = "/home/u/dev/app") =>
        f.Make(EventTypes.CommandStarted, new CommandData("toolu_1", cmd, Cwd: cwd), Agent, wt: "wt:1");

    private static List<AgentEvent> Persist(WorldState s, List<AgentEvent> events, ref long seq)
    {
        var stored = new List<AgentEvent>();
        foreach (var e in events) stored.Add(e with { Seq = ++seq });
        Reducer.ApplyBatch(s, stored);
        return stored;
    }

    [Fact]
    public void Reported_command_becomes_a_risk_event_and_entity()
    {
        var (s, f) = Setup();
        var risks = RiskRecorder.Detect(s, Command(f, "cd /tmp && rm -rf ~/"), Env);
        var r = Assert.Single(risks);
        Assert.Equal(EventTypes.RiskDetected, r.Type);
        Assert.Equal(Evidence.Reported, r.Evidence);
        Assert.StartsWith($"risk:{Agent}:{RiskRules.RecursiveDelete}:reported:", r.DedupeKey);
        var d = r.Payload<RiskData>()!;
        Assert.Equal((RiskSeverity.Critical, "cd /tmp && rm -rf ~/", "hook", EventTypes.CommandStarted), (d.Severity, d.Command, d.Origin, d.SourceType));

        long seq = 100;
        Persist(s, risks, ref seq);
        var entity = Assert.Single(s.Risks.Values);
        Assert.Equal((Agent, RiskSeverity.Critical, Evidence.Reported), (entity.AgentId, entity.Severity, entity.Evidence));
        Assert.Equal(1, s.Agents[Agent].Risks.Critical);
        Assert.Equal(s.Risks[entity.Id], s.Get(new EntityRef(EntityKinds.Risk, entity.Id)));

        // The same command again yields the same dedupe key (the store drops it; the reducer is idempotent too).
        var again = RiskRecorder.Detect(s, Command(f, "cd /tmp && rm -rf ~/"), Env);
        Assert.Equal(r.DedupeKey, Assert.Single(again).DedupeKey);
        Persist(s, again, ref seq);
        Assert.Single(s.Risks);
        Assert.Equal(1, s.Agents[Agent].Risks.Critical);
    }

    [Fact]
    public void Safe_commands_and_non_candidates_produce_nothing()
    {
        var (s, f) = Setup();
        Assert.Empty(RiskRecorder.Detect(s, Command(f, "rm -rf node_modules bin obj && dotnet build"), Env));
        Assert.Empty(RiskRecorder.Detect(s, f.Make(EventTypes.AgentPrompt, new AgentTextData("rm -rf ~/"), Agent), Env));
    }

    [Fact]
    public void Branch_and_dirty_state_come_from_the_worktree()
    {
        var (s, f) = Setup(branch: "main");
        var push = Assert.Single(RiskRecorder.Detect(s, Command(f, "git push -f"), Env)).Payload<RiskData>()!;
        Assert.Equal((RiskRules.GitForcePush, RiskSeverity.Critical), (push.RuleId, push.Severity));
        var discard = Assert.Single(RiskRecorder.Detect(s, Command(f, "git checkout -- ."), Env)).Payload<RiskData>()!;
        Assert.Equal(RiskSeverity.Warn, discard.Severity); // worktree reports 1 modified file
    }

    [Fact]
    public void File_events_use_absolute_paths_and_dedupe_by_target()
    {
        var (s, f) = Setup();
        var read = f.Make(EventTypes.FileRead, new FileChangeData("t1", "Read", AbsolutePath: "/home/u/.ssh/id_rsa"), Agent, file: "/home/u/.ssh/id_rsa");
        var start = f.Make(EventTypes.ToolStarted, new ToolCallData("t1", "Read", FilePath: "/home/u/.ssh/id_rsa"), Agent, file: "/home/u/.ssh/id_rsa");
        var a = Assert.Single(RiskRecorder.Detect(s, read, Env));
        var b = Assert.Single(RiskRecorder.Detect(s, start, Env));
        Assert.Equal(RiskSeverity.Critical, a.Payload<RiskData>()!.Severity);
        Assert.Equal(a.DedupeKey, b.DedupeKey);

        Assert.Empty(RiskRecorder.Detect(s, f.Edit(Agent, "wt:1", "src/a.cs"), Env));
        var outside = f.Make(EventTypes.FileModified, new FileChangeData("t2", "Write", AbsolutePath: "/home/u/.bashrc"), Agent, file: "/home/u/.bashrc");
        Assert.Equal(RiskRules.WriteOutsideWorktree, Assert.Single(RiskRecorder.Detect(s, outside, Env)).Payload<RiskData>()!.RuleId);
    }

    [Fact]
    public void Observed_processes_are_classified_once_and_skip_the_agent_itself()
    {
        var (s, _) = Setup();
        LiveProcess P(int pid, string cmd, string start = "5") =>
            new(pid, 100, start, cmd, "/home/u/dev/app", null, 'R', 0, 0, DateTimeOffset.UnixEpoch.AddDays(1), Agent, []);
        var seen = new HashSet<(int, string)>();
        var procs = new List<LiveProcess>
        {
            P(100, "claude --dangerously-skip-permissions rm -rf ~"), // the agent itself
            P(200, "/bin/bash -c -l 'eval '\\''rm -rf ~/'\\'''"),
            P(201, "rm -rf /home/u/"),
            P(202, "rm -rf node_modules"),
        };
        var found = RiskRecorder.DetectProcesses(s, procs, seen, Env);
        Assert.All(found, e => Assert.Equal(Evidence.Observed, e.Evidence));
        Assert.Equal(2, found.Count);
        Assert.Equal(200, found[0].Payload<RiskData>()!.Pid);
        // Wrapper and child normalize to the same target: one key, recorded once by the store.
        Assert.Single(found.Select(e => e.DedupeKey).Distinct());
        Assert.Contains(":observed:", found[0].DedupeKey);

        Assert.Empty(RiskRecorder.DetectProcesses(s, procs, seen, Env));
        // A new process with a reused pid (different start time) is classified again; gone ones are forgotten.
        Assert.Single(RiskRecorder.DetectProcesses(s, [P(201, "rm -rf /home/u/", start: "9")], seen, Env));
        Assert.Single(seen);
    }

    [Fact]
    public void Findings_are_capped_per_agent_but_counted()
    {
        var (s, f) = Setup();
        long seq = 1000;
        var events = Enumerable.Range(0, WorldState.MaxRisksPerAgent + 5)
            .Select(i => f.Make(EventTypes.RiskDetected, new RiskData(RiskRules.PublishDeploy, RiskSeverity.Warn, "x", "npm publish " + i), Agent) with { DedupeKey = "risk:" + i })
            .ToList();
        Persist(s, events, ref seq);
        Assert.Equal(WorldState.MaxRisksPerAgent, s.Risks.Count);
        Assert.Equal(WorldState.MaxRisksPerAgent + 5, s.Agents[Agent].Risks.Warn);
        Assert.DoesNotContain("risk:0", s.Risks.Keys);
        Assert.Contains("risk:104", s.Risks.Keys);
    }

    [Fact]
    public void Risk_detected_is_not_agent_activity()
    {
        var (s, f) = Setup();
        var before = s.Agents[Agent].LastActivityAt;
        long seq = 10;
        Persist(s, [f.Make(EventTypes.RiskDetected, new RiskData(RiskRules.GitResetHard, RiskSeverity.Warn, "x", "git reset --hard"), Agent)], ref seq);
        Assert.Equal(before, s.Agents[Agent].LastActivityAt);
    }

    [Theory]
    [InlineData("10m", 600)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void Since_parses_relative_ages(string text, int seconds)
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(10);
        Assert.Equal(now.AddSeconds(-seconds), RiskModule.Since(text, now));
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), RiskModule.Since("2026-10-01T00:00:00Z", now));
        Assert.Null(RiskModule.Since(null, now));
    }

    [Fact]
    public void Cli_table_round_trips_api_json()
    {
        var risk = new Amc.Core.Risk.Risk
        {
            Id = "risk:x", AgentId = Agent, RuleId = RiskRules.GitForcePush, Severity = RiskSeverity.Critical,
            Explanation = "Force-push rewrites remote history on main", Span = "git push -f origin main", Command = "git push -f origin main",
            Evidence = Evidence.Reported, At = DateTimeOffset.UnixEpoch,
        };
        var back = Json.Deserialize<List<Amc.Core.Risk.Risk>>(Json.Serialize(new[] { risk }))!;
        var w = new StringWriter();
        Amc.Cli.Commands.RisksCommand.Build(back, new Amc.Cli.Infrastructure.Style(false), DateTimeOffset.UnixEpoch.AddMinutes(5)).Write(w, new Amc.Cli.Infrastructure.Style(false));
        var text = w.ToString();
        Assert.Contains("critical", text);
        Assert.Contains("git-force-push", text);
        Assert.Contains("reported", text);
        Assert.Contains("git push -f origin main", text);
    }
}
