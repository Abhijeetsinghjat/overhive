using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Providers;
using Amc.Daemon.Messaging;
using Amc.Daemon.Providers.Claude;
using Microsoft.Extensions.DependencyInjection;

namespace Amc.Tests.Claude;

public sealed class ClaudeProviderTests : IDisposable
{
    private readonly ClaudeHarness _h = new();
    private const string S = ClaudeHarness.SessionId;

    public void Dispose() => _h.Dispose();

    private Agent MainAgent(int? pid = 4242, string? terminal = null, AgentStatus status = AgentStatus.Running) => new()
    {
        Id = Ids.MainAgent("claude", S), Provider = "claude", Kind = AgentKind.Main, SessionId = S,
        Pid = pid, ProcStart = "987654", TerminalId = terminal, Status = status,
    };

    private Agent SubAgent() => new() { Id = Ids.SubAgent("claude", S, "a1"), Provider = "claude", Kind = AgentKind.Sub, SessionId = S, Status = AgentStatus.Running };

    [Fact]
    public void Capabilities_without_hooks_or_pty_or_process()
    {
        var caps = _h.Provider().Capabilities(MainAgent());
        Assert.Empty(caps.MessageTransports);
        Assert.All(caps.Controls.Values, why => Assert.Equal("No live process is known for this session", why));
    }

    [Fact]
    public void Capabilities_with_hooks_and_live_process()
    {
        _h.InstallHooks();
        _h.Proc.Procs[4242] = ("987654", 'S');
        var caps = _h.Provider().Capabilities(MainAgent());
        Assert.Equal([Transports.HookMailbox], caps.MessageTransports);
        Assert.True(caps.Supports(ControlAction.Pause));
        Assert.True(caps.Supports(ControlAction.Terminate));
        Assert.True(caps.Supports(ControlAction.Kill));
        Assert.False(caps.Supports(ControlAction.Resume));
        Assert.Contains("agentctl run", caps.Controls[ControlAction.Interrupt]);
        // Completed agents take no mailbox messages.
        Assert.Empty(_h.Provider().Capabilities(MainAgent(status: AgentStatus.Completed)).MessageTransports);
    }

    [Fact]
    public void Pid_reuse_is_not_a_live_process()
    {
        _h.Proc.Procs[4242] = ("111", 'S'); // same pid, different start time
        Assert.False(_h.Provider().Capabilities(MainAgent()).Supports(ControlAction.Pause));
    }

    [Fact]
    public void Pty_transport_first_and_interrupt_supported()
    {
        _h.InstallHooks();
        _h.Terminals.Attached.Add("pty-1");
        _h.Proc.Procs[4242] = ("987654", 'S');
        var caps = _h.Provider().Capabilities(MainAgent(terminal: "pty-1"));
        Assert.Equal([Transports.Pty, Transports.HookMailbox], caps.MessageTransports);
        Assert.True(caps.Supports(ControlAction.Interrupt));
    }

    [Fact]
    public void Sub_agents_have_no_process_controls()
    {
        _h.InstallHooks();
        var caps = _h.Provider().Capabilities(SubAgent());
        Assert.Equal([Transports.HookMailbox], caps.MessageTransports);
        Assert.All(caps.Controls.Values, why => Assert.Equal(ClaudeProvider.SubAgentReason, why));
    }

    [Fact]
    public async Task Pause_and_resume_signal_the_tree_and_verify()
    {
        _h.Proc.Procs[4242] = ("987654", 'S');
        _h.Proc.Procs[4300] = ("999", 'S');
        _h.Live.Children[4242] = [4300];
        var p = _h.Provider();
        var r = await p.ControlAsync(MainAgent(), ControlAction.Pause, CancellationToken.None);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal([(4242, Signals.SigStop), (4300, Signals.SigStop)], _h.Signals.Sent);
        var paused = Assert.Single(_h.Sink.OfType(EventTypes.AgentStatusChanged));
        Assert.Equal((AgentStatus.Paused, Evidence.Observed), (paused.Payload<AgentStatusData>()!.Status, paused.Evidence));

        Assert.True(p.Capabilities(MainAgent(status: AgentStatus.Paused)).Supports(ControlAction.Resume));
        r = await p.ControlAsync(MainAgent(status: AgentStatus.Paused), ControlAction.Resume, CancellationToken.None);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal(AgentStatus.Running, _h.Sink.OfType(EventTypes.AgentStatusChanged).Last().Payload<AgentStatusData>()!.Status);
    }

    [Fact]
    public async Task Terminate_marks_cancelled_when_process_exits()
    {
        _h.Proc.Procs[4242] = ("987654", 'S');
        var r = await _h.Provider().ControlAsync(MainAgent(), ControlAction.Terminate, CancellationToken.None);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal([(4242, Signals.SigTerm)], _h.Signals.Sent);
        Assert.Equal(AgentStatus.Cancelled, Assert.Single(_h.Sink.OfType(EventTypes.AgentStatusChanged)).Payload<AgentStatusData>()!.Status);
    }

    [Fact]
    public async Task Sub_agent_control_is_refused()
    {
        var r = await _h.Provider().ControlAsync(SubAgent(), ControlAction.Terminate, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Empty(_h.Signals.Sent);
    }

    [Fact]
    public async Task Send_via_pty_is_delivered_and_via_mailbox_is_accepted()
    {
        _h.InstallHooks();
        _h.Terminals.Attached.Add("pty-1");
        var p = _h.Provider();
        var d = await p.SendAsync(MainAgent(terminal: "pty-1"), new OutgoingMessage("m1", "hello"), CancellationToken.None);
        Assert.Equal((true, true, Transports.Pty), (d.Accepted, d.Delivered, d.Transport));
        Assert.Equal([("pty-1", "hello"), ("pty-1", "\r")], _h.Terminals.Writes);

        d = await p.SendAsync(MainAgent(), new OutgoingMessage("m2", "queued text"), CancellationToken.None);
        Assert.Equal((true, false, Transports.HookMailbox), (d.Accepted, d.Delivered, d.Transport));
        Assert.Equal("queued text", Assert.Single(_h.Mailbox.DrainFor(MainAgent().Id)).Text);
    }

    [Fact]
    public async Task Interrupt_writes_escape_to_pty()
    {
        _h.Terminals.Attached.Add("pty-1");
        _h.Proc.Procs[4242] = ("987654", 'S');
        var r = await _h.Provider().ControlAsync(MainAgent(terminal: "pty-1"), ControlAction.Interrupt, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal([("pty-1", "\x1b")], _h.Terminals.Writes);
    }

    [Fact]
    public void Hooks_state_tracks_settings_changes()
    {
        Assert.False(_h.Hooks.IsInstalled());
        _h.InstallHooks();
        Assert.True(_h.Hooks.IsInstalled());
        File.WriteAllText(_h.Hooks.SettingsPath, "{}");
        File.SetLastWriteTimeUtc(_h.Hooks.SettingsPath, DateTime.UtcNow.AddSeconds(5));
        Assert.False(_h.Hooks.IsInstalled());
    }
}

public sealed class ClaudeRegistryTests : IDisposable
{
    private readonly ClaudeHarness _h = new();
    private const string S = ClaudeHarness.SessionId;
    private static readonly string Main = Ids.MainAgent("claude", S);

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Parses_registry_entry()
    {
        var e = RegistryEntry.Parse(File.ReadAllText(Path.Combine(ClaudeHarness.FixtureRoot(), "sessions", "4242.json")))!;
        Assert.Equal((4242, S, "/work/repo", "987654", "2.1.292", "busy"), (e.Pid, e.SessionId, e.Cwd!, e.ProcStart!, e.Version!, e.Status!));
        Assert.Equal(AgentStatus.Running, e.MappedStatus!.Value.Status);
        Assert.Null(RegistryEntry.Parse("{not json"));
        Assert.Null(RegistryEntry.Parse("""{"pid":1}"""));
    }

    [Fact]
    public async Task Live_session_is_created_with_status_then_completed_when_entry_and_process_vanish()
    {
        _h.CopyFixtures("sessions");
        _h.CopyFixtures("projects");
        _h.Proc.Procs[4242] = ("987654", 'S');
        var reg = _h.Registry();
        await reg.ReconcileAsync(CancellationToken.None);

        var created = Assert.Single(_h.Sink.OfType(EventTypes.AgentCreated));
        var d = created.Payload<AgentCreatedData>()!;
        Assert.Equal((AgentKind.Main, 4242, "987654"), (d.Kind, d.Pid!.Value, d.ProcStart!));
        Assert.EndsWith($"-work-repo/{S}.jsonl", d.TranscriptPath);
        var st = Assert.Single(_h.Sink.OfType(EventTypes.AgentStatusChanged));
        Assert.Equal((AgentStatus.Running, Evidence.Reported), (st.Payload<AgentStatusData>()!.Status, st.Evidence));

        // idle → waiting
        var path = Path.Combine(_h.Options.ClaudeHome, "sessions", "4242.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"busy\"", "\"idle\""));
        await reg.ReconcileAsync(CancellationToken.None);
        Assert.Equal(AgentStatus.Waiting, _h.Sink.OfType(EventTypes.AgentStatusChanged).Last().Payload<AgentStatusData>()!.Status);

        // entry removed while the process still runs → nothing yet; process gone → completed
        File.Delete(path);
        await reg.ReconcileAsync(CancellationToken.None);
        Assert.Empty(_h.Sink.OfType(EventTypes.AgentCompleted));
        _h.Proc.Procs.Clear();
        await reg.ReconcileAsync(CancellationToken.None);
        var done = Assert.Single(_h.Sink.OfType(EventTypes.AgentCompleted));
        Assert.Equal("Session exited (registry entry removed)", done.Payload<AgentStatusData>()!.Reason);
        Assert.Equal($"end:{Main}:987654", done.DedupeKey);
    }

    [Fact]
    public async Task Dead_pid_is_never_reported_running()
    {
        _h.CopyFixtures("sessions");
        await _h.Registry().ReconcileAsync(CancellationToken.None);
        Assert.Single(_h.Sink.OfType(EventTypes.AgentCreated));
        Assert.Empty(_h.Sink.OfType(EventTypes.AgentStatusChanged));
    }

    [Fact]
    public async Task Hook_SessionEnd_and_registry_removal_share_the_end_key()
    {
        _h.CopyFixtures("sessions");
        _h.Proc.Procs[4242] = ("987654", 'S');
        await _h.Registry().ReconcileAsync(CancellationToken.None);
        await _h.HookReceiver().HandleAndProcessAsync(ClaudeHarness.Hook("SessionEnd", S, ("reason", "logout")));
        Assert.Equal($"end:{Main}:987654", Assert.Single(_h.Sink.OfType(EventTypes.AgentCompleted)).DedupeKey);
    }
}

public sealed class ClaudeModuleTests
{
    [Fact]
    public void AddClaude_resolves_provider_and_hosted_services()
    {
        using var h = new ClaudeHarness();
        var s = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        s.AddLogging();
        s.AddSingleton(Microsoft.Extensions.Options.Options.Create(h.Options));
        s.AddSingleton(new Amc.Daemon.Storage.EventStore(Path.Combine(h.Root, "amc.db")));
        s.AddSingleton<Amc.Daemon.Pipeline.StateStore>();
        s.AddSingleton<Amc.Daemon.Infrastructure.HealthRegistry>();
        s.AddSingleton<Amc.Daemon.Pipeline.IEventSink>(h.Sink);
        s.AddSingleton<Mailbox>();
        s.AddSingleton<Amc.Daemon.IWorktreeIndex>(h.Worktrees);
        s.AddSingleton<Amc.Daemon.ILiveProcesses>(h.Live);
        s.AddSingleton<Amc.Daemon.ITerminalHub>(h.Terminals);
        s.AddClaude();
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(s,
            new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions { ValidateOnBuild = true });
        Assert.Equal("claude", Assert.Single(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<IAgentProvider>(sp)).Id);
        Assert.Equal(4, Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<Microsoft.Extensions.Hosting.IHostedService>(sp).Count());
    }
}
