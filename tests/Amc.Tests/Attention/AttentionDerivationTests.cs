using Amc.Core.Attention;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;
using Amc.Tests.Core;

namespace Amc.Tests.Attention;

public class AttentionDerivationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static Agent Agent(WorldState s, string id, AgentStatus status, TimeSpan ago, string? reason = null,
        AgentKind kind = AgentKind.Main, string provider = "claude", string? parent = null, Evidence ev = Evidence.Reported)
    {
        var a = new Agent
        {
            Id = id, Provider = provider, Kind = kind, Title = "Title " + id, Status = status, StatusReason = reason,
            StatusAt = Now - ago, LastActivityAt = Now - ago, StatusEvidence = ev, ParentId = parent,
        };
        s.Agents[id] = a;
        if (parent is not null) s.Agents[parent].ChildIds.Add(id);
        return a;
    }

    [Fact]
    public void Blocked_agent_is_urgent_with_permission_title_redacted_detail_and_reported_evidence()
    {
        var s = new WorldState();
        Agent(s, "codex:s1", AgentStatus.Blocked, TimeSpan.FromMinutes(3),
            "Waiting for permission: curl -H 'Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123' https://x", provider: "codex");

        var item = Assert.Single(AttentionDeriver.Derive(s, Now));

        Assert.Equal(AttentionKinds.AgentBlocked, item.Kind);
        Assert.Equal(AttentionPriority.Urgent, item.Priority);
        Assert.Equal("Needs your approval: Title codex:s1", item.Title);
        Assert.Equal("codex", item.Tool);
        Assert.Equal(["codex:s1"], item.AgentIds);
        Assert.Equal(Evidence.Reported, item.Evidence);
        Assert.Equal(Now - TimeSpan.FromMinutes(3), item.Since);
        Assert.DoesNotContain("abcdefghijklmnop", item.Detail);
        Assert.Contains("«redacted»", item.Detail);
        Assert.Equal(AttentionActions.OpenAgent, item.Action.Kind);
        Assert.Contains(item.MoreActions, a => a.Kind == AttentionActions.Reply);
        Assert.Equal("agents", item.Source);
    }

    [Fact]
    public void Blocked_agent_with_an_owned_terminal_offers_the_terminal_first()
    {
        var s = new WorldState();
        Agent(s, "claude:s1", AgentStatus.Blocked, TimeSpan.FromSeconds(5), "Notification: confirm").TerminalId = "t1";

        var item = Assert.Single(AttentionDeriver.Derive(s, Now));

        Assert.Equal("Needs your input: Title claude:s1", item.Title);
        Assert.Equal(AttentionActions.OpenTerminal, item.Action.Kind);
    }

    [Fact]
    public void Waiting_session_is_normal_but_not_while_its_sub_agents_work()
    {
        var s = new WorldState();
        Agent(s, "claude:idle", AgentStatus.Waiting, TimeSpan.FromMinutes(1), "Turn finished; waiting for input");
        Agent(s, "claude:busy", AgentStatus.Waiting, TimeSpan.FromMinutes(1));
        Agent(s, "claude:busy:sub", AgentStatus.Running, TimeSpan.Zero, kind: AgentKind.Sub, parent: "claude:busy");
        Agent(s, "claude:paused", AgentStatus.Paused, TimeSpan.FromMinutes(1));

        var item = Assert.Single(AttentionDeriver.Derive(s, Now));

        Assert.Equal("claude:idle", item.AgentIds[0]);
        Assert.Equal(AttentionKinds.AgentWaiting, item.Kind);
        Assert.Equal(AttentionPriority.Normal, item.Priority);
        Assert.Equal(AttentionActions.Reply, item.Action.Kind);
    }

    [Fact]
    public void Failures_finished_sessions_and_time_windows()
    {
        var s = new WorldState();
        Agent(s, "claude:failed", AgentStatus.Failed, TimeSpan.FromHours(2), "exit 1");
        Agent(s, "claude:old-failed", AgentStatus.Failed, TimeSpan.FromHours(30));
        Agent(s, "claude:gone", AgentStatus.Disconnected, TimeSpan.FromMinutes(5), "Process exited unexpectedly");
        Agent(s, "claude:done", AgentStatus.Completed, TimeSpan.FromMinutes(10));
        Agent(s, "claude:done-long-ago", AgentStatus.Completed, TimeSpan.FromHours(3));
        Agent(s, "claude:done:sub", AgentStatus.Completed, TimeSpan.FromMinutes(1), kind: AgentKind.Sub, parent: "claude:done");
        Agent(s, "claude:done:subfail", AgentStatus.Failed, TimeSpan.FromMinutes(1), kind: AgentKind.Sub, parent: "claude:done");
        Agent(s, "claude:cancelled", AgentStatus.Cancelled, TimeSpan.FromMinutes(1));

        var items = AttentionDeriver.Derive(s, Now).ToDictionary(i => i.AgentIds[0]);

        Assert.Equal(["claude:done", "claude:done:subfail", "claude:failed", "claude:gone"], items.Keys.Order().ToArray());
        Assert.Equal(AttentionPriority.Urgent, items["claude:failed"].Priority);
        Assert.Equal(AttentionPriority.Normal, items["claude:done:subfail"].Priority);
        Assert.Equal(AttentionPriority.Normal, items["claude:gone"].Priority);
        Assert.Equal(AttentionPriority.Low, items["claude:done"].Priority);

        var noFinished = AttentionDeriver.Derive(s, Now, settings: new AttentionSettings { FinishedWindow = TimeSpan.Zero });
        Assert.DoesNotContain(noFinished, i => i.Kind == AttentionKinds.AgentFinished);
    }

    [Fact]
    public void Conflicts_become_items_with_git_unmerged_urgent()
    {
        var s = new WorldState();
        Agent(s, "gemini:a", AgentStatus.Running, TimeSpan.Zero, provider: "gemini");
        s.Conflicts["c1"] = new Conflict { Key = "c1", Kind = ConflictKind.SameFile, Description = "src/A.cs edited by 2 agents", Path = "src/A.cs", AgentIds = ["gemini:a"], DetectedAt = Now };
        s.Conflicts["c2"] = new Conflict { Key = "c2", Kind = ConflictKind.GitUnmerged, Description = "merge in progress", DetectedAt = Now };

        var items = AttentionDeriver.Derive(s, Now);

        Assert.Equal(2, items.Count);
        Assert.Equal(AttentionPriority.Urgent, items[0].Priority);
        Assert.Equal(Evidence.Observed, items[0].Evidence);
        Assert.Equal(AttentionKinds.Conflict, items[1].Kind);
        Assert.Equal("Agents editing the same file: src/A.cs", items[1].Title);
        Assert.Equal("gemini", items[1].Tool);
        Assert.Equal(Evidence.Inferred, items[1].Evidence);
        Assert.Equal(new AttentionAction(AttentionActions.OpenConflict, "Details", "c1"), items[1].Action);
    }

    [Fact]
    public void Ordered_by_priority_then_oldest_first()
    {
        var s = new WorldState();
        Agent(s, "claude:w-new", AgentStatus.Waiting, TimeSpan.FromMinutes(1));
        Agent(s, "claude:b-new", AgentStatus.Blocked, TimeSpan.FromMinutes(1));
        Agent(s, "claude:done", AgentStatus.Completed, TimeSpan.FromMinutes(30));
        Agent(s, "claude:b-old", AgentStatus.Blocked, TimeSpan.FromMinutes(20));
        Agent(s, "claude:w-old", AgentStatus.Waiting, TimeSpan.FromMinutes(9));

        var order = AttentionDeriver.Derive(s, Now).Select(i => i.AgentIds[0]).ToArray();

        Assert.Equal(["claude:b-old", "claude:b-new", "claude:w-old", "claude:w-new", "claude:done"], order);
    }

    [Fact]
    public void Ids_are_stable_per_occurrence_through_the_reducer()
    {
        var f = new EventFactory();
        var s = new WorldState();
        Reducer.ApplyBatch(s, [f.Main("claude:s"), f.Status("claude:s", AgentStatus.Running), f.Status("claude:s", AgentStatus.Blocked, "Waiting for permission: Bash")]);
        var first = Assert.Single(AttentionDeriver.Derive(s, f.Now)).Id;

        // Same episode: a repeated notification and a later recompute keep the id.
        Reducer.ApplyBatch(s, [f.Status("claude:s", AgentStatus.Blocked, "Waiting for permission: Bash")]);
        Assert.Equal(first, Assert.Single(AttentionDeriver.Derive(s, f.Now + TimeSpan.FromMinutes(5))).Id);

        // A new episode (answered, then blocked again) is a new item.
        Reducer.ApplyBatch(s, [f.Status("claude:s", AgentStatus.Running), f.Status("claude:s", AgentStatus.Blocked, "Waiting for permission: Edit")]);
        Assert.NotEqual(first, Assert.Single(AttentionDeriver.Derive(s, f.Now)).Id);
    }

    private sealed class FakeProvider(params AttentionItem[] items) : IAttentionProvider
    {
        public string Name => "guard";
        public IEnumerable<AttentionItem> Collect(WorldState state, AttentionContext ctx) => items;
    }

    [Fact]
    public void Plug_in_providers_add_items_that_are_sanitized_and_deduplicated_by_id()
    {
        var s = new WorldState();
        Agent(s, "claude:b", AgentStatus.Blocked, TimeSpan.FromMinutes(1));
        var alert = new AttentionItem
        {
            Id = "guard:1", Kind = "guard.alert", Priority = AttentionPriority.Urgent, Title = "Runaway loop\nin agent",
            Detail = "password=hunter2hunter2", Since = Now - TimeSpan.FromMinutes(10), Evidence = Evidence.Inferred,
            Action = new AttentionAction(AttentionActions.OpenAgent, "Open", "claude:b"),
        };

        var items = AttentionDeriver.Derive(s, Now, [.. AttentionDeriver.BuiltIn, new FakeProvider(alert, alert with { Title = "dup" })]);

        Assert.Equal(2, items.Count);
        var g = items[0];
        Assert.Equal("guard:1", g.Id);
        Assert.Equal("Runaway loop in agent", g.Title);
        Assert.DoesNotContain("hunter2", g.Detail);
        Assert.Equal("guard", g.Source);
    }

    [Fact]
    public void Long_titles_and_details_are_capped()
    {
        var s = new WorldState();
        Agent(s, "claude:x", AgentStatus.Blocked, TimeSpan.Zero, new string('r', 5000)).Title = new string('t', 1000);

        var item = Assert.Single(AttentionDeriver.Derive(s, Now));

        Assert.Equal(AttentionDeriver.MaxTitle, item.Title.Length);
        Assert.Equal(AttentionDeriver.MaxDetail, item.Detail!.Length);
    }

    [Fact]
    public void Serializes_with_lowercase_enums_for_the_ui()
    {
        var s = new WorldState();
        Agent(s, "claude:x", AgentStatus.Blocked, TimeSpan.Zero);
        var json = Json.Serialize(AttentionDeriver.Derive(s, Now));
        Assert.Contains("\"priority\":\"urgent\"", json);
        Assert.Contains("\"evidence\":\"reported\"", json);
        Assert.Contains("\"action\":{\"kind\":\"open-agent\"", json);
    }
}
