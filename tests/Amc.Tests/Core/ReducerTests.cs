using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Tests.Core;

public class AgentStateMachineTests
{
    [Theory]
    [InlineData(AgentStatus.Running, AgentStatus.Waiting, false, true)]
    [InlineData(AgentStatus.Completed, AgentStatus.Running, false, false)]   // late events cannot resurrect
    [InlineData(AgentStatus.Completed, AgentStatus.Running, true, true)]     // genuine new activity (resume) can
    [InlineData(AgentStatus.Disconnected, AgentStatus.Completed, false, true)] // provider end event corrects a disconnect
    [InlineData(AgentStatus.Paused, AgentStatus.Waiting, false, false)]      // paused only leaves via resume or ending
    [InlineData(AgentStatus.Paused, AgentStatus.Running, false, true)]
    [InlineData(AgentStatus.Failed, AgentStatus.Unknown, false, false)]
    [InlineData(AgentStatus.Running, AgentStatus.Running, false, false)]
    public void Transitions(AgentStatus from, AgentStatus to, bool reopen, bool allowed) =>
        Assert.Equal(allowed, AgentStateMachine.CanTransition(from, to, reopen));
}

public class ReducerTests
{
    private readonly EventFactory _f = new();
    private readonly WorldState _s = new();

    private HashSet<EntityRef> Apply(params AgentEvent[] events) => Reducer.ApplyBatch(_s, events);

    [Fact]
    public void Builds_arbitrary_depth_hierarchy_even_when_children_arrive_first()
    {
        Apply(_f.Sub("a1", "a"), _f.Sub("a", "main"), _f.Main("main"), _f.Sub("b", "main"));

        Assert.Equal(["a", "b"], _s.Agents["main"].ChildIds.Order());
        Assert.Equal(["a1"], _s.Agents["a"].ChildIds);
        Assert.Equal(AgentStatus.Running, _s.Agents["a1"].Status);
    }

    [Fact]
    public void Process_exit_without_end_event_disconnects_agent_and_active_children()
    {
        Apply(_f.Main("main", pid: 42), _f.Sub("a", "main"), _f.Sub("b", "main"),
            _f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "done"), "b"),
            _f.Status("main", AgentStatus.Running));

        Apply(_f.Make(EventTypes.ProcessExited, new ProcessExitData(42, "1", null, "Process exited unexpectedly"), "main", evidence: Evidence.Observed));

        Assert.Equal(AgentStatus.Disconnected, _s.Agents["main"].Status);
        Assert.Equal("Process exited unexpectedly", _s.Agents["main"].StatusReason);
        Assert.Equal(Evidence.Observed, _s.Agents["main"].StatusEvidence);
        Assert.Equal(AgentStatus.Disconnected, _s.Agents["a"].Status);
        Assert.Equal(AgentStatus.Completed, _s.Agents["b"].Status); // finished children keep their real outcome
    }

    [Fact]
    public void Exit_of_unrelated_pid_does_not_affect_agent()
    {
        Apply(_f.Main("main", pid: 42), _f.Status("main", AgentStatus.Running));
        Apply(_f.Make(EventTypes.ProcessExited, new ProcessExitData(43, "1"), null, evidence: Evidence.Observed));
        Assert.Equal(AgentStatus.Running, _s.Agents["main"].Status);
    }

    [Fact]
    public void Tool_activity_sets_current_action_and_clears_on_completion()
    {
        Apply(_f.Main("main"), _f.Status("main", AgentStatus.Waiting));
        Apply(_f.Make(EventTypes.CommandStarted, new CommandData("tu1", "dotnet test", "Run tests"), "main"));
        var a = _s.Agents["main"];
        Assert.Equal(AgentStatus.Running, a.Status);
        Assert.Equal("Run tests", a.CurrentAction);
        Assert.Equal("dotnet test", a.CurrentCommand);

        Apply(_f.Make(EventTypes.CommandFailed, new CommandData("tu1", "dotnet test", ExitCode: 1), "main"));
        Assert.Null(a.CurrentCommand);
        Assert.Equal(1, a.ToolFailures);
        Assert.Equal(1, a.ToolCount);
    }

    [Fact]
    public void Replaying_already_folded_events_is_a_no_op()
    {
        var e = _f.Main("main");
        Apply(e);
        var changed = Apply(e);
        Assert.Empty(changed);
        Assert.Single(_s.Agents);
    }

    [Fact]
    public void File_attribution_tracks_agents_evidence_and_worktree_ownership()
    {
        Apply(_f.Main("main"), _f.Repo("r1"), _f.Worktree("w1", "/r", "r1"));
        Apply(_f.Edit("main", "w1", "src/A.cs"), _f.Edit("main", "w1", "src/A.cs"), _f.GitStatus("w1", "src/A.cs", "M"));

        var f = _s.Files[Ids.FileKey("w1", "src/A.cs")];
        Assert.Equal("M", f.GitStatus);
        Assert.Equal(2, f.EditCount);
        Assert.Equal(Evidence.Reported, f.Agents["main"].Evidence);
        Assert.Equal(1, _s.Agents["main"].FilesChanged);
        Assert.True(_s.Worktrees["w1"].Agents.ContainsKey("main"));
        Assert.Equal("w1", _s.Agents["main"].CurrentWorktreeId);
    }

    [Fact]
    public void Unattributed_file_that_becomes_clean_is_dropped_but_attributed_one_is_kept()
    {
        Apply(_f.Main("main"), _f.Repo("r1"), _f.Worktree("w1", "/r", "r1"));
        Apply(_f.GitStatus("w1", "noise.txt", "?"), _f.Edit("main", "w1", "kept.cs"), _f.GitStatus("w1", "kept.cs", "M"));
        Apply(_f.GitStatus("w1", "noise.txt", "clean"), _f.GitStatus("w1", "kept.cs", "clean"));

        Assert.False(_s.Files.ContainsKey(Ids.FileKey("w1", "noise.txt")));
        Assert.Equal("clean", _s.Files[Ids.FileKey("w1", "kept.cs")].GitStatus);
    }

    [Fact]
    public void Tasks_keep_start_and_completion_times_across_updates()
    {
        Apply(_f.Main("main"));
        Apply(_f.Make(EventTypes.TasksUpdated, new TaskListData([new("1", "Analyze", TaskState.InProgress), new("2", "Test", TaskState.Pending)]), "main"));
        var started = _s.Agents["main"].Tasks[0].StartedAt;
        Apply(_f.Make(EventTypes.TasksUpdated, new TaskListData([new("1", "Analyze", TaskState.Completed), new("2", "Test", TaskState.InProgress)]), "main"));

        var t = _s.Agents["main"].Tasks;
        Assert.Equal(started, t[0].StartedAt);
        Assert.NotNull(t[0].CompletedAt);
        Assert.NotNull(t[1].StartedAt);
    }

    [Fact]
    public void Message_lifecycle_queued_delivered_answered()
    {
        Apply(_f.Main("main"));
        Apply(_f.Make(EventTypes.MessageSent, new MessageData("m1", "Focus on tests", Transports.HookMailbox), "main"));
        Assert.Equal(MessageStatus.Queued, _s.Messages["m1"].Status);
        Apply(_f.Make(EventTypes.MessageDelivered, new MessageStatusData("m1", Transports.HookMailbox, "PostToolUse"), "main"));
        Assert.Equal(MessageStatus.Delivered, _s.Messages["m1"].Status);
        Apply(_f.Make(EventTypes.MessageReceived, new MessageReplyData("m1", "Understood."), "main", evidence: Evidence.Inferred));
        Assert.Equal(MessageStatus.Answered, _s.Messages["m1"].Status);
        Assert.Equal("Understood.", _s.Messages["m1"].Reply);
    }

    [Fact]
    public void Snapshot_round_trip_preserves_state()
    {
        Apply(_f.Main("main"), _f.Sub("a", "main"), _f.Repo("r1"), _f.Worktree("w1", "/r", "r1"), _f.Edit("a", "w1", "x.cs"));
        var copy = Json.Deserialize<WorldState>(Json.Serialize(_s))!;
        Assert.Equal(Json.Serialize(_s), Json.Serialize(copy));
    }
}

public class ConflictDetectorTests
{
    private readonly EventFactory _f = new();
    private readonly WorldState _s = new();

    private void Setup()
    {
        Reducer.ApplyBatch(_s, [_f.Main("main"), _f.Sub("a", "main"), _f.Sub("b", "main"), _f.Repo("r1"),
            _f.Worktree("w1", "/r", "r1"), _f.Worktree("w2", "/wt/x", "r1")]);
    }

    [Fact]
    public void Parent_and_its_own_sub_agent_in_one_worktree_is_not_a_conflict()
    {
        Setup();
        Reducer.ApplyBatch(_s, [_f.Edit("main", "w1", "A.cs"), _f.Edit("a", "w1", "B.cs")]);
        Assert.DoesNotContain(_s.Conflicts.Values, c => c.Kind == ConflictKind.SameWorktree);

        Reducer.ApplyBatch(_s, [_f.Edit("b", "w1", "C.cs")]);
        Assert.Contains(_s.Conflicts.Values, c => c.Kind == ConflictKind.SameWorktree);
    }

    [Fact]
    public void Two_active_agents_on_same_file_is_a_conflict_until_one_finishes()
    {
        Setup();
        Reducer.ApplyBatch(_s, [_f.Edit("a", "w1", "Svc.cs"), _f.Edit("b", "w1", "Svc.cs"), _f.GitStatus("w1", "Svc.cs", "M")]);
        var c = Assert.Single(_s.Conflicts.Values, c => c.Kind == ConflictKind.SameFile);
        Assert.Equal(["a", "b"], c.AgentIds);
        Assert.Contains(_s.Conflicts.Values, c => c.Kind == ConflictKind.SameWorktree);

        var changed = Reducer.ApplyBatch(_s, [_f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed), "b")]);
        Assert.DoesNotContain(_s.Conflicts.Values, c => c.Kind == ConflictKind.SameFile);
        Assert.Contains(changed, r => r.Kind == EntityKinds.Conflict);
    }

    [Fact]
    public void Same_path_dirty_in_two_worktrees_of_one_repo_is_a_cross_worktree_conflict()
    {
        Setup();
        Reducer.ApplyBatch(_s, [_f.Edit("a", "w1", "Api.cs"), _f.GitStatus("w1", "Api.cs", "M"),
            _f.Edit("b", "w2", "Api.cs"), _f.GitStatus("w2", "Api.cs", "M")]);
        var c = Assert.Single(_s.Conflicts.Values, c => c.Kind == ConflictKind.CrossWorktree);
        Assert.Equal(["w1", "w2"], c.WorktreeIds);
    }

    [Fact]
    public void Git_unmerged_state_is_reported()
    {
        Setup();
        Reducer.ApplyBatch(_s, [_f.Worktree("w1", "/r", "r1", conflicted: 2, op: "merge")]);
        var c = Assert.Single(_s.Conflicts.Values, c => c.Kind == ConflictKind.GitUnmerged);
        Assert.Contains("2 unmerged", c.Description);
    }

    [Fact]
    public void Recompute_is_stable_when_nothing_changes()
    {
        Setup();
        Reducer.ApplyBatch(_s, [_f.Edit("a", "w1", "Svc.cs"), _f.Edit("b", "w1", "Svc.cs"), _f.GitStatus("w1", "Svc.cs", "M")]);
        var changed = new HashSet<EntityRef>();
        ConflictDetector.Recompute(_s, changed);
        Assert.Empty(changed);
    }
}

public class WorktreeResolverTests
{
    [Fact]
    public void Longest_root_wins_and_paths_are_normalized()
    {
        var outer = new Worktree { Id = "o", Root = "/home/u/repo", RepositoryId = "r" };
        var inner = new Worktree { Id = "i", Root = "/home/u/repo/.worktrees/x", RepositoryId = "r" };
        var gone = new Worktree { Id = "g", Root = "/tmp/gone", RepositoryId = "r", Removed = true };
        var r = new WorktreeResolver([outer, inner, gone]);

        Assert.Equal(("i", "src/a.cs"), r.Resolve("/home/u/repo/.worktrees/x/src/a.cs") is { } h ? (h.Worktree.Id, h.RelativePath) : default);
        Assert.Equal(("o", "src/b.cs"), r.Resolve("/home/u/repo/lib/../src//b.cs") is { } h2 ? (h2.Worktree.Id, h2.RelativePath) : default);
        Assert.Null(r.Resolve("/home/u/repository/x"));   // prefix of a name is not a parent dir
        Assert.Null(r.Resolve("/tmp/gone/file"));
        Assert.Null(r.Resolve("relative/path"));
        Assert.Null(r.Resolve("/home/u/repo/../../etc/passwd"));
    }
}

public class ReopenTests
{
    [Fact]
    public void Late_events_older_than_completion_do_not_reopen_but_newer_ones_do()
    {
        var f = new EventFactory();
        var s = new WorldState();
        var main = f.Main("main");
        var lateTool = f.Make(EventTypes.ToolStarted, new ToolCallData("t1", "Read"), "main"); // happened before completion…
        var done = f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed), "main");
        Reducer.ApplyBatch(s, [main, done, lateTool with { Seq = 99 }]);                     // …but arrives after it
        Assert.Equal(AgentStatus.Completed, s.Agents["main"].Status);

        Reducer.ApplyBatch(s, [f.Make(EventTypes.ToolStarted, new ToolCallData("t2", "Read"), "main") with { Seq = 100 }]);
        Assert.Equal(AgentStatus.Running, s.Agents["main"].Status);
    }
}

public class EvidenceOrderingTests
{
    [Fact]
    public void Backfilled_history_does_not_override_newer_live_status_or_activity_age()
    {
        var f = new EventFactory();
        var s = new WorldState();
        var oldPrompt = f.Make(EventTypes.AgentPrompt, new AgentTextData("yesterday's prompt"), "main");
        f.Advance(TimeSpan.FromHours(16));
        var created = f.Main("main");
        var idle = f.Status("main", AgentStatus.Waiting, "Registry: idle");
        Reducer.ApplyBatch(s, [created with { Seq = 1 }, idle with { Seq = 2 }, oldPrompt with { Seq = 3 }]);

        var a = s.Agents["main"];
        Assert.Equal(AgentStatus.Waiting, a.Status);
        Assert.Equal(oldPrompt.Ts, a.LastActivityAt); // registration is not activity; the old prompt is the last real one
    }
}

public class CurrentWorktreeTests
{
    [Fact]
    public void Inferred_shell_location_does_not_displace_reported_worktree()
    {
        var f = new EventFactory();
        var s = new WorldState();
        Reducer.ApplyBatch(s, [f.Main("m"), f.Repo("r"), f.Worktree("main", "/r", "r"), f.Worktree("track", "/wt/track", "r"),
            f.Edit("m", "track", "a.cs"),
            f.Make(EventTypes.CommandStarted, new CommandData("c1", "ls"), "m", wt: "main", evidence: Evidence.Inferred)]);
        Assert.Equal("track", s.Agents["m"].CurrentWorktreeId);
        Assert.True(s.Agents["m"].Worktrees.ContainsKey("main")); // still recorded as touched
    }
}

public class ReplyLinkTests
{
    [Fact]
    public void Earliest_output_after_delivery_is_the_reply_even_if_it_arrives_late()
    {
        var f = new EventFactory();
        var s = new WorldState();
        Reducer.ApplyBatch(s, [f.Main("m"), f.Make(EventTypes.MessageSent, new MessageData("m1", "hi", Transports.HookMailbox), "m"),
            f.Make(EventTypes.MessageDelivered, new MessageStatusData("m1", Transports.HookMailbox), "m")]);
        var ack = f.Make(EventTypes.AgentOutput, new AgentTextData("ACK"), "m");          // written first…
        var later = f.Make(EventTypes.AgentOutput, new AgentTextData("next message"), "m");
        Reducer.ApplyBatch(s, [later with { Seq = 10 }]);                                // …but persisted after
        Reducer.ApplyBatch(s, [ack with { Seq = 11 }]);
        Assert.Equal("ACK", s.Messages["m1"].Reply);
        Assert.Equal(MessageStatus.Answered, s.Messages["m1"].Status);
    }
}

public class ResumeTests
{
    [Fact]
    public void Finished_session_reappearing_in_a_new_process_is_reopened()
    {
        var f = new EventFactory();
        var s = new WorldState();
        Reducer.ApplyBatch(s, [f.Main("m", pid: 100), f.Make(EventTypes.AgentCompleted, new AgentStatusData(AgentStatus.Completed, "exited"), "m")]);
        Reducer.ApplyBatch(s, [f.Make(EventTypes.AgentUpdated, new AgentUpdatedData(Pid: 200, ProcStart: "9"), "m", evidence: Evidence.Observed)]);
        Assert.Equal(AgentStatus.Starting, s.Agents["m"].Status);
        Reducer.ApplyBatch(s, [f.Status("m", AgentStatus.Waiting, "Registry: idle")]);
        Assert.Equal(AgentStatus.Waiting, s.Agents["m"].Status);
        Assert.Equal(200, s.Agents["m"].Pid);
    }
}

public class UsageTests
{
    [Fact]
    public void Repeated_records_count_once_and_streamed_growth_adds_only_the_increase()
    {
        var f = new EventFactory();
        var s = new WorldState();
        AgentEvent U(string msg, long i, long o, long cw = 0, long cr = 0, string model = "claude-opus-5-5") =>
            f.Make(EventTypes.AgentUsage, new UsageData(msg, model, i, o, cw, cr), "m");
        Reducer.ApplyBatch(s, [f.Main("m"), U("msg1", 10, 50, 100, 1000), U("msg1", 10, 50, 100, 1000), U("msg1", 10, 80, 100, 1000),
            U("msg2", 5, 20, model: "claude-haiku-4-5")]);
        var u = s.Agents["m"].Usage;
        Assert.Equal((15L, 100L, 100L, 1000L, 2L), (u.Input, u.Output, u.CacheWrite, u.CacheRead, u.Responses));
        Assert.Equal(80, s.Agents["m"].UsageByModel["claude-opus-5-5"].Output);
        Assert.Equal(20, s.Agents["m"].UsageByModel["claude-haiku-4-5"].Output);
    }
}
