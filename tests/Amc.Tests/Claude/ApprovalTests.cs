using System.Text.Json.Nodes;
using Amc.Core.Approvals;
using Amc.Core.Attention;
using Amc.Core.Events;
using Amc.Core.Risk;
using Amc.Daemon.Approvals;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Risk;
using Amc.Tests.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amc.Tests.Claude;

/// <summary>Dashboard approvals of Claude Code PreToolUse calls: policy, hold, decisions, fallbacks, session rules.</summary>
public sealed class ApprovalTests : IDisposable
{
    private readonly ClaudeHarness _h = new();
    private readonly StateStore _state = new();
    private readonly ApplyingSink _sink;
    private static readonly RiskEnvironment Env = new("/home/u", RiskOs.Linux, ["/tmp"]);
    private const string Risky = "rm -rf ~/";
    private const string Other = "5e55a0aa-0000-4000-8000-0000000000bb";

    public ApprovalTests() => _sink = new ApplyingSink(_state);
    public void Dispose() => _h.Dispose();

    private ApprovalBroker Broker(string mode = ApprovalModes.Risky, string minSeverity = RiskSeverity.Warn, TimeSpan? hold = null) =>
        new(_state, _sink, _h.Health, Microsoft.Extensions.Options.Options.Create(new AmcOptions
        {
            Approvals = new ApprovalSettings { Mode = mode, MinSeverity = minSeverity },
        }), _h.Time, NullLogger<ApprovalBroker>.Instance, env: Env)
        { HoldOverride = hold ?? TimeSpan.FromSeconds(30) };

    private static JsonObject Bash(string command, string session = ClaudeHarness.SessionId) =>
        ClaudeHarness.Hook("PreToolUse", session, ("tool_name", "Bash"), ("tool_use_id", "toolu_" + Guid.NewGuid().ToString("N")[..8]),
            ("tool_input", new JsonObject { ["command"] = command }));

    private static JsonObject Edit(string path, string oldText, string newText) =>
        ClaudeHarness.Hook("PreToolUse", ClaudeHarness.SessionId, ("tool_name", "Edit"), ("tool_use_id", "toolu_e"),
            ("tool_input", new JsonObject { ["file_path"] = path, ["old_string"] = oldText, ["new_string"] = newText }));

    private List<AgentEvent> Events(string type) => _sink.Events.Where(e => e.Type == type).ToList();

    private async Task<string> PendingId()
    {
        for (var i = 0; i < 300 && Events(EventTypes.ApprovalRequested).Count == 0; i++) await Task.Delay(10);
        return Assert.Single(Events(EventTypes.ApprovalRequested)).Payload<ApprovalRequestedData>()!.ApprovalId;
    }

    // ---- no added latency when off / not needed ----

    [Fact]
    public void Mode_off_never_holds_a_call_and_is_the_default()
    {
        Assert.Equal(ApprovalModes.Off, new ApprovalSettings().EffectiveMode);
        Assert.False(new AmcOptions().Approvals.Enabled);
        var b = Broker(ApprovalModes.Off);
        Assert.Null(b.Prepare(Bash(Risky)));
        Assert.Null(b.Prepare(Edit("/work/repo/a.cs", "a", "b")));
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void Calls_on_the_plain_hook_route_never_wait_even_when_approvals_are_on()
    {
        var b = Broker();
        Assert.Null(b.Prepare(Bash(Risky), holdable: false));
        Assert.NotNull(b.Prepare(Bash(Risky), holdable: true));
    }

    [Fact]
    public void Harmless_calls_and_other_events_do_not_wait()
    {
        var b = Broker();
        Assert.Null(b.Prepare(Bash("ls -la && dotnet test")));
        Assert.Null(b.Prepare(Bash("rm -rf bin obj node_modules")));                        // inside the worktree (cwd)
        Assert.Null(b.Prepare(Edit("/work/repo/src/a.cs", "a", "b")));                        // risky mode: edits inside are fine
        Assert.Null(b.Prepare(ClaudeHarness.Hook("PostToolUse", ClaudeHarness.SessionId, ("tool_name", "Bash"),
            ("tool_input", new JsonObject { ["command"] = Risky }))));
    }

    [Fact]
    public void Min_severity_critical_lets_warnings_through()
    {
        var b = Broker(minSeverity: RiskSeverity.Critical);
        Assert.Null(b.Prepare(Bash("git reset --hard")));     // warn
        Assert.NotNull(b.Prepare(Bash(Risky)));               // critical
        Assert.NotNull(Broker().Prepare(Bash("git reset --hard")));
    }

    // ---- decisions produce the exact hook JSON ----

    [Fact]
    public async Task Allow_answers_the_hook_with_permissionDecision_allow_and_is_recorded()
    {
        var b = Broker();
        var ticket = b.Prepare(Bash(Risky))!;
        var wait = b.WaitAsync(ticket, CancellationToken.None);
        var id = await PendingId();
        Assert.False(wait.IsCompleted);
        Assert.Matches("^[0-9a-f]{32}$", id);

        var pending = _state.Read(s => s.Approvals[id]);
        Assert.Equal("Bash", pending.Tool);
        Assert.Equal(Risky, pending.Command);
        Assert.Equal(RiskSeverity.Critical, pending.Severity);
        Assert.Contains(pending.Findings, f => f.RuleId == RiskRules.RecursiveDelete);
        Assert.Equal("/work/repo", pending.Cwd);
        Assert.Equal("claude:" + ClaudeHarness.SessionId, pending.AgentId);

        var r = await b.ResolveAsync(id, ApprovalDecisions.Allow, null, false, ApprovalActors.Local, "dashboard");
        Assert.True(r.Ok);
        var output = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","permissionDecisionReason":"Approved by the user in Overhive"}}""",
            output!.ToJsonString());

        var resolved = Assert.Single(Events(EventTypes.ApprovalResolved)).Payload<ApprovalResolvedData>()!;
        Assert.Equal((id, ApprovalDecisions.Allow, ApprovalActors.Local, "dashboard"), (resolved.ApprovalId, resolved.Decision, resolved.By, resolved.Client));
        Assert.NotNull(resolved.LatencyMs);
        Assert.Empty(_state.Read(s => s.Approvals));                         // resolved items disappear
        Assert.Equal(0, b.PendingCount);
    }

    [Fact]
    public async Task Deny_passes_the_users_reason_to_the_agent()
    {
        var b = Broker();
        var wait = b.WaitAsync(b.Prepare(Bash(Risky))!, CancellationToken.None);
        var id = await PendingId();
        Assert.True((await b.ResolveAsync(id, ApprovalDecisions.Deny, "  use a temp dir instead ", false, ApprovalActors.Remote, "phone")).Ok);
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Denied by the user in Overhive: use a temp dir instead"}}""",
            (await wait.WaitAsync(TimeSpan.FromSeconds(5)))!.ToJsonString());
        var d = Events(EventTypes.ApprovalResolved).Single().Payload<ApprovalResolvedData>()!;
        Assert.Equal((ApprovalDecisions.Deny, ApprovalActors.Remote, "use a temp dir instead"), (d.Decision, d.By, d.Reason));

        Assert.Equal("Denied by the user in Overhive.", (string?)ApprovalHookOutput.Deny(null)["hookSpecificOutput"]!["permissionDecisionReason"]);
    }

    // ---- fallbacks: never allow silently, never block forever ----

    [Fact]
    public async Task Timeout_answers_nothing_so_the_normal_permission_flow_applies()
    {
        var b = Broker(hold: TimeSpan.FromMilliseconds(150));
        var output = await b.WaitAsync(b.Prepare(Bash(Risky))!, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(output);
        var d = Events(EventTypes.ApprovalResolved).Single().Payload<ApprovalResolvedData>()!;
        Assert.Equal((ApprovalDecisions.Expired, ApprovalActors.System), (d.Decision, d.By));
        Assert.Empty(_state.Read(s => s.Approvals));
        // A late decision finds nothing.
        Assert.Equal(404, (await b.ResolveAsync(d.ApprovalId, ApprovalDecisions.Allow, null, false, ApprovalActors.Local, null)).StatusCode);
    }

    [Fact]
    public async Task Hook_going_away_expires_the_request()
    {
        var b = Broker();
        using var cts = new CancellationTokenSource();
        var wait = b.WaitAsync(b.Prepare(Bash(Risky))!, cts.Token);
        var id = await PendingId();
        await cts.CancelAsync();
        Assert.Null(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ApprovalDecisions.Expired, Events(EventTypes.ApprovalResolved).Single().Payload<ApprovalResolvedData>()!.Decision);
        Assert.False((await b.ResolveAsync(id, ApprovalDecisions.Allow, null, false, ApprovalActors.Local, null)).Ok);
    }

    [Fact]
    public async Task Daemon_stopping_releases_waiters_and_a_restart_expires_what_was_left_pending()
    {
        var b = Broker();
        var wait = b.WaitAsync(b.Prepare(Bash(Risky))!, CancellationToken.None);
        var id = await PendingId();
        b.ReleaseAll("Overhive stopped before a decision");                // ApplicationStopping
        Assert.Null(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ApprovalDecisions.Expired, Events(EventTypes.ApprovalResolved).Single().Payload<ApprovalResolvedData>()!.Decision);

        // A crash leaves the request in the state (no resolved event): the restorer expires it on the next start.
        var crashed = new StateStore();
        var sink = new ApplyingSink(crashed);
        await sink.PublishAsync(_sink.Events.First(e => e.Type == EventTypes.ApprovalRequested));
        Assert.Single(crashed.Read(s => s.Approvals));
        await new ApprovalRestorer(crashed, sink).StartAsync(CancellationToken.None);
        Assert.Empty(crashed.Read(s => s.Approvals));
        var r = sink.Events.Single(e => e.Type == EventTypes.ApprovalResolved).Payload<ApprovalResolvedData>()!;
        Assert.Equal((id, ApprovalDecisions.Expired), (r.ApprovalId, r.Decision));
    }

    // ---- ids ----

    [Fact]
    public async Task Ids_are_unguessable_and_single_use()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => ApprovalBroker.NewId()).ToHashSet();
        Assert.Equal(200, ids.Count);
        Assert.All(ids, i => Assert.True(ApprovalBroker.IsWellFormed(i)));

        var b = Broker();
        var wait = b.WaitAsync(b.Prepare(Bash(Risky))!, CancellationToken.None);
        var id = await PendingId();
        var first = b.ResolveAsync(id, ApprovalDecisions.Deny, null, false, ApprovalActors.Local, null);
        var second = b.ResolveAsync(id, ApprovalDecisions.Allow, null, false, ApprovalActors.Local, null);
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => r.StatusCode == 404);
        Assert.Contains("\"deny\"", (await wait).ToJsonString());               // the first decision won
        Assert.Single(Events(EventTypes.ApprovalResolved));
        Assert.Equal(404, (await b.ResolveAsync(id[..31] + "0", ApprovalDecisions.Allow, null, false, ApprovalActors.Local, null)).StatusCode);
        Assert.Equal(404, (await b.ResolveAsync("../../etc", ApprovalDecisions.Allow, null, false, ApprovalActors.Local, null)).StatusCode);
        Assert.Equal(400, (await b.ResolveAsync(id, "maybe", null, false, ApprovalActors.Local, null)).StatusCode);
    }

    // ---- allow for this session, ask ----

    [Fact]
    public async Task Allow_for_this_session_covers_only_the_exact_command_in_the_same_directory()
    {
        var b = Broker();
        var wait = b.WaitAsync(b.Prepare(Bash("git reset --hard"))!, CancellationToken.None);   // warn
        var id = await PendingId();
        Assert.Equal("this exact command in /work/repo", _state.Read(s => s.Approvals[id].SimilarLabel));
        await b.ResolveAsync(id, ApprovalDecisions.Allow, null, allowSimilar: true, ApprovalActors.Local, null);
        await wait;
        Assert.True(Events(EventTypes.ApprovalResolved).Single().Payload<ApprovalResolvedData>()!.AllowSimilar);

        // The same command (whitespace aside) in the same session and directory: allowed at once, recorded, no new card.
        var again = b.Prepare(Bash("  git reset   --hard "))!;
        Assert.True(again.AutoAllowed);
        Assert.Contains("\"allow\"", (await b.WaitAsync(again, CancellationToken.None))!.ToJsonString());
        var auto = Events(EventTypes.ApprovalResolved).Last().Payload<ApprovalResolvedData>()!;
        Assert.Equal((ApprovalActors.SessionRule, "  git reset   --hard "), (auto.By, auto.Command));
        Assert.Single(Events(EventTypes.ApprovalRequested));

        // Anything else asks again: another variant, another directory, another session.
        Assert.False(b.Prepare(Bash("git reset --hard HEAD~3"))!.AutoAllowed);
        var elsewhere = Bash("git reset --hard");
        elsewhere["cwd"] = "/work/wt-feature";
        Assert.False(b.Prepare(elsewhere)!.AutoAllowed);
        Assert.False(b.Prepare(Bash("git reset --hard", Other))!.AutoAllowed);
        // The session's rules end with it.
        Assert.Equal(1, b.RuleCount(ClaudeHarness.SessionId));
        b.Prepare(ClaudeHarness.Hook("SessionEnd"));
        Assert.Equal(0, b.RuleCount(ClaudeHarness.SessionId));
        Assert.False(b.Prepare(Bash("git reset --hard"))!.AutoAllowed);
    }

    [Fact]
    public async Task Critical_findings_are_never_offered_a_session_wide_allow()
    {
        var b = Broker();
        var ticket = b.Prepare(Bash("git push --force origin main"))!;
        Assert.Null(ticket.Need.SimilarKey);
        var wait = b.WaitAsync(ticket, CancellationToken.None);
        var id = await PendingId();
        Assert.Null(_state.Read(s => s.Approvals[id].SimilarLabel));
        Assert.Equal(400, (await b.ResolveAsync(id, ApprovalDecisions.Allow, null, allowSimilar: true, ApprovalActors.Local, null)).StatusCode);
        Assert.True((await b.ResolveAsync(id, ApprovalDecisions.Allow, null, false, ApprovalActors.Local, null)).Ok);   // still pending: plain allow works
        await wait;
        Assert.Equal(0, b.RuleCount(ClaudeHarness.SessionId));
        Assert.False(b.Prepare(Bash("git push --force origin main"))!.AutoAllowed);
    }

    [Fact]
    public async Task Allow_edits_to_this_file_covers_that_file_only()
    {
        var b = Broker(ApprovalModes.AllEdits);
        var wait = b.WaitAsync(b.Prepare(Edit("/work/repo/src/a.cs", "a", "b"))!, CancellationToken.None);
        var id = await PendingId();
        Assert.Equal("edits to /work/repo/src/a.cs", _state.Read(s => s.Approvals[id].SimilarLabel));
        await b.ResolveAsync(id, ApprovalDecisions.Allow, null, allowSimilar: true, ApprovalActors.Local, null);
        await wait;
        Assert.True(b.Prepare(Edit("/work/repo/src/a.cs", "c", "d"))!.AutoAllowed);
        Assert.False(b.Prepare(Edit("/work/repo/src/b.cs", "c", "d"))!.AutoAllowed);
    }

    [Fact]
    public async Task Ask_in_terminal_hands_the_call_to_claude_codes_prompt_at_once()
    {
        var b = Broker();
        var wait = b.WaitAsync(b.Prepare(Bash(Risky))!, CancellationToken.None);
        var id = await PendingId();
        Assert.True((await b.ResolveAsync(id, ApprovalDecisions.Ask, null, false, ApprovalActors.Local, "dashboard")).Ok);
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"Overhive: the user chose to answer in the terminal"}}""",
            (await wait.WaitAsync(TimeSpan.FromSeconds(5)))!.ToJsonString());
        Assert.Equal(ApprovalDecisions.Ask, Events(EventTypes.ApprovalResolved).Single().Payload<ApprovalResolvedData>()!.Decision);
    }

    [Fact]
    public async Task One_card_per_call_the_risk_item_is_merged_while_pending_and_after_a_decision()
    {
        var b = Broker(hold: TimeSpan.FromMilliseconds(300));
        var ticket = b.Prepare(Bash(Risky))!;
        var wait = b.WaitAsync(ticket, CancellationToken.None);
        var id = await PendingId();
        // The recorder's finding for the same call (reported, same tool_use_id) and for its process (observed, same span).
        var finding = ticket.Need.Findings[0];
        await _sink.PublishAsync(RiskEvent(ticket, finding, ticket.ToolUseId));
        await _sink.PublishAsync(RiskEvent(ticket, finding, null, observed: true));
        IReadOnlyList<AttentionItem> Items() => _state.Read(s => AttentionDeriver.Derive(s, _h.Time.Now));
        Assert.Equal([AttentionKinds.Approval], Items().Select(i => i.Kind));

        await b.ResolveAsync(id, ApprovalDecisions.Deny, null, false, ApprovalActors.Local, null);
        await wait;
        Assert.Empty(Items());                                                    // denied: no second "Needs you" item
        Assert.Equal(2, _state.Read(s => s.Risks.Count));                          // still in the Risks tab

        // A call nobody decided falls back to the normal risk item.
        var t2 = b.Prepare(Bash("git reset --hard"))!;
        Assert.Null(await b.WaitAsync(t2, CancellationToken.None));
        await _sink.PublishAsync(RiskEvent(t2, t2.Need.Findings[0], t2.ToolUseId));
        Assert.Equal([AttentionKinds.Risk], Items().Select(i => i.Kind));
    }

    private AgentEvent RiskEvent(ApprovalBroker.Ticket t, RiskFinding f, string? toolUseId, bool observed = false) => new()
    {
        Type = EventTypes.RiskDetected, Source = observed ? EventSources.Process : EventSources.System,
        Evidence = observed ? Evidence.Observed : Evidence.Reported, AgentId = t.AgentId, SessionId = t.SessionId, Ts = _h.Time.Now,
        DedupeKey = $"risk:{t.AgentId}:{f.RuleId}:{observed}:{toolUseId}",
        Data = Json.ToObject(new RiskData(f.RuleId, f.Severity, f.Explanation, f.Span, f.Target, t.Need.Command, ToolUseId: toolUseId)),
    };

    // ---- policy (pure) ----

    [Fact]
    public void All_edits_mode_holds_every_edit_with_a_truncated_diff_preview()
    {
        var ctx = new RiskContext { Cwd = "/work/repo", HomeDir = "/home/u", Os = RiskOs.Linux, WorktreeRoots = ["/work/repo"] };
        var settings = new ApprovalSettings { Mode = "all-edits" };
        var input = new JsonObject { ["file_path"] = "src/a.cs", ["old_string"] = "int x = 1;", ["new_string"] = new string('y', 4000) };
        var need = ApprovalPolicy.Evaluate(settings, "Edit", input, ctx)!;
        Assert.Equal(("edit", "/work/repo/src/a.cs", null), (need.Reason, need.FilePath, need.Severity));
        Assert.StartsWith("- int x = 1;\n+ yyy", need.Preview);
        Assert.Contains("… [truncated]", need.Preview);
        Assert.True(need.Preview!.Length < ApprovalPolicy.MaxPreview);

        Assert.Null(ApprovalPolicy.Evaluate(new ApprovalSettings { Mode = "risky" }, "Edit", input, ctx));
        Assert.Null(ApprovalPolicy.Evaluate(settings, "Read", new JsonObject { ["file_path"] = "/work/repo/README.md" }, ctx));
        var multi = ApprovalPolicy.Evaluate(settings, "MultiEdit", new JsonObject
        {
            ["file_path"] = "/work/repo/b.cs",
            ["edits"] = new JsonArray(new JsonObject { ["old_string"] = "a", ["new_string"] = "b" }, new JsonObject { ["old_string"] = "c", ["new_string"] = "d" }),
        }, ctx)!;
        Assert.Equal("- a\n+ b\n@@\n- c\n+ d\n", multi.Preview);
    }

    [Fact]
    public void Risky_mode_holds_writes_and_reads_the_classifier_flags()
    {
        var ctx = new RiskContext { Cwd = "/work/repo", HomeDir = "/home/u", Os = RiskOs.Linux, WorktreeRoots = ["/work/repo"] };
        var risky = new ApprovalSettings { Mode = "risky" };
        var bashrc = ApprovalPolicy.Evaluate(risky, "Write", new JsonObject { ["file_path"] = "~/.bashrc", ["content"] = "alias x=y" }, ctx)!;
        Assert.Equal(("risk", "/home/u/.bashrc", RiskSeverity.Warn), (bashrc.Reason, bashrc.FilePath, bashrc.Severity));
        Assert.Equal("+ alias x=y\n", bashrc.Preview);
        var key = ApprovalPolicy.Evaluate(risky, "Read", new JsonObject { ["file_path"] = "/home/u/.ssh/id_ed25519" }, ctx)!;
        Assert.Equal(RiskSeverity.Critical, key.Severity);
        Assert.Contains(key.Findings, f => f.RuleId == RiskRules.CredentialAccess);
        Assert.Equal("git push --force\necho done", ApprovalPolicy.NormalizeCommand("  git  push\t--force \r\necho   done "));
    }

    [Fact]
    public void Pending_approvals_are_urgent_attention_items_carrying_the_card()
    {
        var s = new Amc.Core.State.WorldState();
        s.Approvals["abc"] = new PendingApproval
        {
            Id = "abc", AgentId = "claude:s1", Tool = "Bash", Reason = "risk", Command = Risky, Severity = RiskSeverity.Critical,
            Findings = [new ApprovalFinding(RiskRules.RecursiveDelete, RiskSeverity.Critical, "Recursive delete of the home directory", Risky)],
            RequestedAt = DateTimeOffset.UtcNow,
        };
        var item = Assert.Single(AttentionDeriver.Derive(s, DateTimeOffset.UtcNow));
        Assert.Equal((AttentionKinds.Approval, AttentionPriority.Urgent, AttentionActions.Approve, "abc"),
            (item.Kind, item.Priority, item.Action.Kind, item.Action.Target));
        Assert.Same(s.Approvals["abc"], item.Approval);
        Assert.Contains(Risky, item.Detail);
    }
}
