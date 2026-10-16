using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Providers;
using Amc.Daemon.Conflicts;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Git;

/// <summary>Provider whose agents with "can" in their id accept messages (typed, like the pty transport).</summary>
internal sealed class FakeMessagingProvider : IAgentProvider
{
    public ConcurrentQueue<(string AgentId, string Text)> Sent { get; } = new();
    public string Id => "fake";
    public AgentCapabilities Capabilities(Agent agent) =>
        new(agent.Id.Contains("can") ? [Transports.Pty] : [], new Dictionary<ControlAction, string?>());
    public Task<ControlResult> ControlAsync(Agent agent, ControlAction action, CancellationToken ct) => Task.FromResult(new ControlResult(false, "n/a"));
    public Task<DeliveryResult> SendAsync(Agent agent, OutgoingMessage message, CancellationToken ct)
    {
        Sent.Enqueue((agent.Id, message.Text));
        return Task.FromResult(new DeliveryResult(true, true, Transports.Pty, "typed"));
    }
}

public sealed class ConflictAnalyzerTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static string Lines(int n, Func<int, string>? f = null) => string.Concat(Enumerable.Range(1, n).Select(i => (f?.Invoke(i) ?? $"line {i}") + "\n"));

    private sealed record Setup(GitTestEnv Env, string Main, string X, string Y, string WtX, string WtY, FakeMessagingProvider Provider,
        ConflictWarner Warner, ConflictAnalyzer Analyzer)
    {
        public const string Path = "src/orders.ts";
        public string Key => State.Read(s => s.Conflicts.Keys.Single(k => k.StartsWith("cross-worktree|", StringComparison.Ordinal)));
        public StateStore State => Env.State;

        public async Task RefreshAsync()
        {
            await Env.Collector.RefreshAsync(WtX, None);
            await Env.Collector.RefreshAsync(WtY, None);
            await Env.Applied();
        }

        public ConflictOverlap? Overlap
        {
            get
            {
                var key = Key;
                return State.Read(s => s.Conflicts.GetValueOrDefault(key)?.Insight?.Overlap);
            }
        }
    }

    /// <summary>A repo with two linked worktrees (feat/x, fix/y), one agent attributed in each; x can receive messages, y cannot.</summary>
    private static async Task<Setup> CreateAsync(bool autoWarn = false)
    {
        var env = await GitTestEnv.CreateAsync();
        env.Options.AutoWarnConflicts = autoWarn;
        var main = env.InitRepo("ws/repo", (Setup.Path, Lines(100)));
        var x = System.IO.Path.Combine(env.Dir, "ws", "wt-x");
        var y = System.IO.Path.Combine(env.Dir, "ws", "wt-y");
        Assert.Equal(0, GitTestEnv.RunGit(main, "worktree", "add", "-q", "-b", "feat/x", x).Code);
        Assert.Equal(0, GitTestEnv.RunGit(main, "worktree", "add", "-q", "-b", "fix/y", y).Code);
        var wtX = await env.DiscoverAsync(x);
        var wtY = await env.DiscoverAsync(y);

        var provider = new FakeMessagingProvider();
        var ops = new AgentOperations([provider], env.State, env.Sink);
        var warner = new ConflictWarner(env.State, ops, env.Sink, new ToolRegistry(Options.Create(env.Options)), TimeProvider.System);
        var analyzer = new ConflictAnalyzer(env.Git, env.State, env.Sink, env.Health, Options.Create(env.Options), warner, TimeProvider.System,
            NullLogger<ConflictAnalyzer>.Instance, new ConflictAnalyzerSettings { Debounce = TimeSpan.Zero, UseMergeTree = true });
        return new Setup(env, main, x, y, wtX, wtY, provider, warner, analyzer);
    }

    private static async Task AttributeAsync(Setup t)
    {
        foreach (var (agent, title, wt) in new[] { ("fake:can-x", "Codex", t.WtX), ("fake:y", "Gemini", t.WtY) })
        {
            await t.Env.Sink.PublishAndWaitAsync(new AgentEvent
            {
                Type = EventTypes.AgentCreated, Source = EventSources.Hook, AgentId = agent,
                Data = Json.ToObject(new AgentCreatedData(AgentKind.Main, "fake", Title: title)),
            });
            await t.Env.Sink.PublishAndWaitAsync(new AgentEvent
            {
                Type = EventTypes.FileModified, Source = EventSources.Hook, Evidence = Evidence.Reported, AgentId = agent,
                WorktreeId = wt, FilePath = Setup.Path, Data = Json.ToObject(new FileChangeData(Tool: "Edit")),
            });
        }
        await t.Env.Applied();
    }

    [Fact]
    public async Task Overlapping_uncommitted_and_committed_changes_are_detected_relative_to_the_merge_base()
    {
        var t = await CreateAsync();
        await using var _ = t.Env;
        // x: commits a change to lines 40-45, then edits 46-50 without committing; y: uncommitted edit of line 48.
        GitTestEnv.Write(t.X, Setup.Path, Lines(100, i => i is >= 40 and <= 45 ? $"x {i}" : $"line {i}"));
        GitTestEnv.RunGit(t.X, "commit", "-qam", "x part 1");
        GitTestEnv.Write(t.X, Setup.Path, Lines(100, i => i is >= 40 and <= 50 ? $"x {i}" : $"line {i}"));
        GitTestEnv.Write(t.Y, Setup.Path, Lines(100, i => i == 48 ? "y 48" : $"line {i}"));
        await t.RefreshAsync();
        await AttributeAsync(t);

        Assert.Equal(1, await t.Analyzer.RunOnceAsync(None, ignoreDebounce: true));
        await t.Env.Applied();
        var o = t.Overlap!;
        Assert.Equal(OverlapVerdict.Overlapping, o.Verdict);
        var pair = Assert.Single(o.Pairs);
        var (mine, theirs) = pair.WorktreeA == t.WtX ? (pair.OverlapA, pair.OverlapB) : (pair.OverlapB, pair.OverlapA);
        Assert.Equal([new LineSpan(40, 50)], mine);
        Assert.Equal([new LineSpan(48, 48)], theirs);
        Assert.Equal(GitTestEnv.Head(t.Main), pair.MergeBase);
        Assert.Null(pair.CommittedConflict); // only x has committed changes: nothing for merge-tree to confirm

        // Unchanged inputs are not analysed again.
        Assert.Equal(0, await t.Analyzer.RunOnceAsync(None, ignoreDebounce: true));

        // Once both sides commit the overlapping change, git merge-tree confirms the conflict.
        GitTestEnv.RunGit(t.X, "commit", "-qam", "x part 2");
        GitTestEnv.RunGit(t.Y, "commit", "-qam", "y");
        GitTestEnv.Write(t.X, Setup.Path, Lines(100, i => i is >= 40 and <= 50 ? $"x {i}" : i == 99 ? "x dirty" : $"line {i}"));
        GitTestEnv.Write(t.Y, Setup.Path, Lines(100, i => i == 48 ? "y 48" : i == 1 ? "y dirty" : $"line {i}"));
        await t.RefreshAsync();
        Assert.Equal(1, await t.Analyzer.RunOnceAsync(None, ignoreDebounce: true));
        await t.Env.Applied();
        Assert.True(Assert.Single(t.Overlap!.Pairs).CommittedConflict);
    }

    [Fact]
    public async Task Separate_regions_are_reported_as_likely_clean_and_only_capable_agents_can_receive()
    {
        var t = await CreateAsync();
        await using var _ = t.Env;
        GitTestEnv.Write(t.X, Setup.Path, Lines(100, i => i is 10 or 11 ? $"x {i}" : $"line {i}"));
        GitTestEnv.Write(t.Y, Setup.Path, Lines(100, i => i == 90 ? "y 90" : $"line {i}"));
        await t.RefreshAsync();
        await AttributeAsync(t);
        await t.Analyzer.RunOnceAsync(None, ignoreDebounce: true);
        await t.Env.Applied();
        Assert.Equal(OverlapVerdict.Separate, t.Overlap!.Verdict);
        Assert.Equal("low", t.Overlap.Severity);

        var preview = t.Warner.Preview(t.Key)!;
        var toX = preview.Recipients.Single(r => r.AgentId == "fake:can-x");
        Assert.Contains("Gemini (fake, branch fix/y) is also editing src/orders.ts line 90; your lines 10-11 are in a different region", toX.Text);
        Assert.True(toX.CanReceive);
        Assert.False(preview.Recipients.Single(r => r.AgentId == "fake:y").CanReceive);
    }

    [Fact]
    public async Task Untracked_file_created_in_both_worktrees_overlaps()
    {
        var t = await CreateAsync();
        await using var _ = t.Env;
        const string fresh = "src/new.ts";
        GitTestEnv.Write(t.X, fresh, "a\nb\n");
        GitTestEnv.Write(t.Y, fresh, "c\n");
        await t.RefreshAsync();
        foreach (var (agent, wt) in new[] { ("fake:can-x", t.WtX), ("fake:y", t.WtY) })
        {
            await t.Env.Sink.PublishAndWaitAsync(new AgentEvent
            {
                Type = EventTypes.AgentCreated, Source = EventSources.Hook, AgentId = agent,
                Data = Json.ToObject(new AgentCreatedData(AgentKind.Main, "fake")),
            });
            await t.Env.Sink.PublishAndWaitAsync(new AgentEvent
            {
                Type = EventTypes.FileCreated, Source = EventSources.Hook, AgentId = agent, WorktreeId = wt, FilePath = fresh,
                Data = Json.ToObject(new FileChangeData(Tool: "Write")),
            });
        }
        await t.Analyzer.RunOnceAsync(None, ignoreDebounce: true);
        await t.Env.Applied();
        Assert.Equal(OverlapVerdict.Overlapping, t.Overlap!.Verdict);
    }

    [Fact]
    public async Task Warn_requires_confirmation_reports_per_agent_and_auto_warn_sends_once()
    {
        var t = await CreateAsync(autoWarn: true);
        await using var _ = t.Env;
        GitTestEnv.Write(t.X, Setup.Path, Lines(100, i => i is >= 40 and <= 50 ? $"x {i}" : $"line {i}"));
        GitTestEnv.Write(t.Y, Setup.Path, Lines(100, i => i == 48 ? "y 48" : $"line {i}"));
        await t.RefreshAsync();
        await AttributeAsync(t);

        // Auto-warn: the analysis finds overlap and warns the agent that can receive messages, once.
        await t.Analyzer.RunOnceAsync(None, ignoreDebounce: true);
        await t.Env.Applied();
        var (to, text) = Assert.Single(t.Provider.Sent);
        Assert.Equal("fake:can-x", to);
        Assert.StartsWith("Heads-up from Overhive: Gemini (fake, branch fix/y) is also editing src/orders.ts line 48, overlapping your lines 40-50.", text);
        await t.Analyzer.RunOnceAsync(None, ignoreDebounce: true);
        Assert.Single(t.Provider.Sent);
        var auto = Assert.Single(t.Env.Sink.OfType(EventTypes.ConflictWarned)).Payload<ConflictWarnedData>()!;
        Assert.True(auto.Auto);
        Assert.Equal(("fake:can-x", "delivered"), (auto.Recipients[0].AgentId, auto.Recipients[0].Outcome));

        // Manual warn over HTTP: 428 without confirm; then per-agent outcomes and an audit entry.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(t.State);
        builder.Services.AddSingleton(t.Warner);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy);
        await using var app = builder.Build();
        app.MapConflictEndpoints();
        await app.StartAsync();
        var http = app.GetTestClient();

        var detail = await http.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>($"/api/conflicts/detail?key={Uri.EscapeDataString(t.Key)}");
        Assert.Equal("overlapping", detail!["conflict"]!["insight"]!["overlap"]!["verdict"]!.ToString());
        Assert.Equal(2, detail["warn"]!["recipients"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/conflicts/detail?key=nope")).StatusCode);

        var noConfirm = await http.PostAsJsonAsync("/api/conflicts/warn", new { key = t.Key });
        Assert.Equal((HttpStatusCode)428, noConfirm.StatusCode);
        Assert.Single(t.Provider.Sent);

        var res = await http.PostAsJsonAsync("/api/conflicts/warn", new { key = t.Key, confirm = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = (await res.Content.ReadFromJsonAsync<WarnResult>(Json.Options))!;
        Assert.Equal("delivered", r.Agents.Single(a => a.AgentId == "fake:can-x").Outcome);
        Assert.Equal("unsupported", r.Agents.Single(a => a.AgentId == "fake:y").Outcome);
        Assert.Equal(2, t.Provider.Sent.Count);
        await t.Env.Applied();
        var audits = t.Env.Sink.OfType(EventTypes.AuditControl).Select(e => e.Payload<AuditData>()!).ToList();
        Assert.Contains(audits, a => a.Action == "auto-warn-conflict" && a.Outcome == "ok");
        Assert.Contains(audits, a => a.Action == "warn-conflict" && a.Outcome == "partial");
        var key = t.Key;
        Assert.Equal(2, t.State.Read(s => s.Conflicts[key].Insight!.Warnings.Count));
        // The warning is an ordinary message in the agent's message list.
        Assert.Equal(2, t.State.Read(s => s.Messages.Values.Count(m => m.AgentId == "fake:can-x")));
    }
}
