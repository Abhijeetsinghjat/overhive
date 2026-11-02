using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Risk;
using Amc.Core.State;

namespace Amc.Tests.Core;

public class StateRetentionTests
{
    private readonly EventFactory _f = new();
    private readonly WorldState _s = new() { Retention = new StateRetention() };

    private HashSet<EntityRef> Apply(params AgentEvent[] events) => Reducer.ApplyBatch(_s, events);

    /// <summary>Any event moves the state clock forward and gives the sweep a chance to run.</summary>
    private HashSet<EntityRef> Tick(TimeSpan after)
    {
        _f.Advance(after);
        return Apply(_f.Make(EventTypes.AgentNotification, new AgentTextData("tick")));
    }

    private AgentEvent RemoveWorktree(string wt, string repo) =>
        _f.Make(EventTypes.WorktreeDeleted, new WorktreeData("/r/" + wt, repo), wt: wt, repo: repo, evidence: Evidence.Observed);

    [Fact]
    public void Removed_worktree_drops_files_index_entries_and_edits_after_the_grace_period()
    {
        Apply(_f.Repo("r"), _f.Worktree("w1", "/r/w1", "r"), _f.Worktree("w2", "/r/w2", "r"), _f.Main("m"));
        Apply(_f.Edit("m", "w1", "a.cs"), _f.GitStatus("w1", "a.cs", "M"), _f.GitStatus("w1", "b.cs", "D"),
            _f.Edit("m", "w2", "a.cs"), _f.GitStatus("w2", "a.cs", "M"));
        var idx = _s.Index; // built: the sweep must keep it in step
        Assert.True(idx.WritersByWorktree.ContainsKey("w1"));

        Apply(RemoveWorktree("w1", "r"));
        Assert.Equal(3, _s.Files.Count); // still shown right after the removal
        Tick(TimeSpan.FromHours(23));
        Assert.Equal(3, _s.Files.Count);

        var changed = Tick(TimeSpan.FromHours(2));

        Assert.Equal(["w2|a.cs"], _s.Files.Keys);
        Assert.Contains(new EntityRef(EntityKinds.File, "w1|a.cs"), changed);
        Assert.False(idx.FilesByWorktree.ContainsKey("w1"));
        Assert.False(idx.WritersByWorktree.ContainsKey("w1"));
        Assert.Equal(["w2|a.cs"], idx.FilesByAgent["m"]);
        Assert.Equal(["w2|a.cs"], idx.FilesByGroup.Values.SelectMany(x => x));
        Assert.DoesNotContain(_s.Verification["m"].Edits, e => e.WorktreeId == "w1");
        Assert.True(_s.Worktrees.ContainsKey("w1")); // the agent still refers to it
        Assert.Equal(Json.Serialize(StateIndex.Build(_s)), Json.Serialize(idx));
    }

    [Fact]
    public void Removed_worktree_entity_goes_once_no_agent_refers_to_it()
    {
        Apply(_f.Repo("r"), _f.Worktree("w1", "/r/w1", "r"), _f.GitStatus("w1", "x.cs", "D"));
        Apply(RemoveWorktree("w1", "r"));
        var changed = Tick(TimeSpan.FromDays(2));
        Assert.Empty(_s.Files);
        Assert.False(_s.Worktrees.ContainsKey("w1"));
        Assert.Contains(new EntityRef(EntityKinds.Worktree, "w1"), changed);
    }

    [Fact]
    public void Idle_clean_files_are_dropped_dirty_files_are_kept()
    {
        Apply(_f.Repo("r"), _f.Worktree("w", "/r/w", "r"), _f.Main("m"));
        Apply(_f.Edit("m", "w", "clean.cs"), _f.GitStatus("w", "clean.cs", "M"), _f.GitStatus("w", "clean.cs", "clean"),
            _f.Edit("m", "w", "unseen.cs"), _f.GitStatus("w", "dirty.cs", "M"));
        Assert.Equal(3, _s.Files.Count);

        Tick(TimeSpan.FromDays(6));
        Assert.Equal(3, _s.Files.Count);
        Tick(TimeSpan.FromDays(1.5));

        Assert.Equal(["w|dirty.cs"], _s.Files.Keys);
    }

    [Fact]
    public void Finished_agent_trees_are_dropped_with_their_usage_and_risks_active_trees_are_kept()
    {
        Apply(_f.Main("old"), _f.Sub("old-sub", "old"), _f.Main("live"), _f.Sub("live-sub", "live"));
        Apply(_f.Make(EventTypes.AgentUsage, new UsageData("msg1", "m", 10, 5, 0, 0), "old"),
            _f.Make(EventTypes.RiskDetected, new RiskData("rule", "high", "why", "rm -rf"), "old-sub"),
            _f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "done"), "old-sub"),
            _f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "done"), "old"),
            _f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "done"), "live-sub"),
            _f.Status("live", AgentStatus.Waiting));

        Tick(TimeSpan.FromDays(13));
        Assert.Equal(4, _s.Agents.Count);

        var changed = Tick(TimeSpan.FromDays(2));

        Assert.Equal(["live", "live-sub"], _s.Agents.Keys.Order()); // a waiting root keeps its finished sub-agent
        Assert.Contains(new EntityRef(EntityKinds.Agent, "old"), changed);
        Assert.Empty(_s.UsageMarks);
        Assert.Empty(_s.Risks);
    }

    [Fact]
    public void Old_commits_test_runs_and_usage_marks_are_dropped()
    {
        Apply(_f.Repo("r"), _f.Worktree("w", "/r/w", "r"), _f.Main("m"), _f.Status("m", AgentStatus.Running));
        Apply(_f.Make(EventTypes.GitCommitCreated, new GitCommitData("abc", "old", null, _f.Now, "main"), "m", "w"),
            _f.Make(EventTypes.TestPassed, new TestRunData("tu1", "dotnet", "dotnet test", Passed: 3), "m", "w"),
            _f.Make(EventTypes.AgentUsage, new UsageData("msg1", "m", 10, 5, 0, 0), "m"));
        Tick(TimeSpan.FromDays(8));
        Apply(_f.Make(EventTypes.GitCommitCreated, new GitCommitData("def", "new", null, _f.Now, "main"), "m", "w"));
        Assert.Empty(_s.UsageMarks); // marks: 7 days
        Assert.Equal(2, _s.Commits.Count);

        Tick(TimeSpan.FromDays(7));

        Assert.Equal(["def"], _s.Commits.Keys);
        Assert.Empty(_s.TestRuns);
        Assert.True(_s.Agents["m"].Usage.Output > 0); // totals stay
    }

    [Fact]
    public void Verification_details_of_long_finished_trees_are_dropped_with_their_counts()
    {
        Apply(_f.Repo("r"), _f.Worktree("w", "/r/w", "r"), _f.Main("m"), _f.Status("m", AgentStatus.Running));
        Apply(_f.Make(EventTypes.CommandCompleted, new CommandData("tu1", "dotnet test", ExitCode: 1), "m", "w"),
            _f.Make(EventTypes.AgentOutput, new AgentTextData("All tests pass."), "m"),
            _f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "done"), "m"));
        Assert.NotNull(_s.Agents["m"].Verification);

        Tick(TimeSpan.FromDays(8));

        Assert.False(_s.Verification.ContainsKey("m"));
        Assert.Null(_s.Agents["m"].Verification);
        Assert.True(_s.Agents.ContainsKey("m")); // the agent itself stays for 14 days
    }

    [Fact]
    public void Zero_keeps_a_kind_forever()
    {
        _s.Retention = new StateRetention { HistoryDays = 0 };
        Apply(_f.Repo("r"), _f.Worktree("w", "/r/w", "r"),
            _f.Make(EventTypes.GitCommitCreated, new GitCommitData("abc", "old", null, _f.Now, "main"), wt: "w"));
        Tick(TimeSpan.FromDays(400));
        Assert.Single(_s.Commits);
    }

    /// <summary>Serialized state with object keys sorted (dictionary order depends on insert/remove history, not content).</summary>
    private static string Canonical(WorldState s)
    {
        static System.Text.Json.Nodes.JsonNode? Sort(System.Text.Json.Nodes.JsonNode? n) => n switch
        {
            System.Text.Json.Nodes.JsonObject o => new System.Text.Json.Nodes.JsonObject(
                o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value?.DeepClone())))),
            System.Text.Json.Nodes.JsonArray a => new System.Text.Json.Nodes.JsonArray(a.Select(x => Sort(x?.DeepClone())).ToArray()),
            _ => n?.DeepClone(),
        };
        return Sort(System.Text.Json.Nodes.JsonNode.Parse(Json.Serialize(s)))!.ToJsonString();
    }

    /// <summary>Pruning runs on event time, so a full replay, any batching and snapshot + tail all give the same state.</summary>
    [Fact]
    public void Pruning_is_deterministic_across_batching_and_snapshots()
    {
        var f = new EventFactory();
        var events = new List<AgentEvent> { f.Repo("r"), f.Worktree("w1", "/r/w1", "r"), f.Worktree("w2", "/r/w2", "r") };
        for (var i = 0; i < 40; i++)
        {
            var agent = "a" + i;
            events.Add(f.Main(agent, pid: 1000 + i));
            var wt = i % 2 == 0 || i > 20 ? "w1" : "w2";
            events.Add(f.Edit(agent, wt, $"f{i % 7}.cs"));
            events.Add(f.GitStatus(wt, $"f{i % 7}.cs", i % 3 == 0 ? "clean" : "M", agent));
            events.Add(f.Make(EventTypes.AgentUsage, new UsageData("m" + i, "x", 10, 5, 0, 0), agent));
            events.Add(f.Make(EventTypes.GitCommitCreated, new GitCommitData("c" + i, "s", null, f.Now, "main"), agent, "w1"));
            events.Add(f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "done"), agent));
            if (i == 20) events.Add(f.Make(EventTypes.WorktreeDeleted, new WorktreeData("/r/w2", "r"), wt: "w2", repo: "r"));
            f.Advance(TimeSpan.FromHours(13));
        }

        var whole = new WorldState { Retention = new StateRetention() };
        Reducer.ApplyBatch(whole, events);

        var batched = new WorldState { Retention = new StateRetention() };
        foreach (var chunk in events.Chunk(7)) Reducer.ApplyBatch(batched, chunk);

        var head = new WorldState { Retention = new StateRetention() };
        Reducer.ApplyBatch(head, events.Take(events.Count / 2));
        var restored = Json.Deserialize<WorldState>(Json.Serialize(head))!;
        restored.Retention = new StateRetention();
        foreach (var e in events.Skip(events.Count / 2)) Reducer.Apply(restored, e, []);
        Reducer.ApplyBatch(restored, []);

        var expected = Canonical(whole);
        Assert.Equal(expected, Canonical(batched));
        Assert.Equal(expected, Canonical(restored));
        Assert.True(whole.Agents.Count < 40 && whole.Commits.Count < 40); // something was actually pruned
        Assert.DoesNotContain(whole.Files.Values, x => x.WorktreeId == "w2");
    }
}
