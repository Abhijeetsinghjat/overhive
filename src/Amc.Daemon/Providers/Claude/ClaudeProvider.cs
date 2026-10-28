using System.Text;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Providers;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Providers.Claude;

/// <summary>
/// Interactive surface for Claude Code agents: PTY (sessions launched via agentctl run)
/// and hook-mailbox messaging; SIGSTOP/SIGCONT/SIGTERM/SIGKILL process control for main
/// sessions with a verified live process. Claude Code has no per-sub-agent control.
/// </summary>
public sealed class ClaudeProvider(
    ITerminalHub terminals, ILiveProcesses processes, IProcFs proc, ISignalSender signals, ClaudeHooksState hooks,
    Mailbox mailbox, ClaudeContext ctx, IEventSink sink, IOptions<AmcOptions> options, TimeProvider time,
    ClaudeRewake? rewake = null) : IAgentProvider
{
    public const string SubAgentReason = "Claude Code exposes no per-sub-agent process control; message it or control the parent session";
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(2);

    public string Id => ClaudePaths.Provider;

    private bool HasPty(Agent a) => a.TerminalId is not null && terminals.IsAttached(a.TerminalId);

    public AgentCapabilities Capabilities(Agent agent)
    {
        var transports = new List<string>();
        if (HasPty(agent)) transports.Add(Transports.Pty);
        if (!agent.IsTerminal && hooks.IsInstalled()) transports.Add(Transports.HookMailbox);

        var controls = new Dictionary<ControlAction, string?>();
        foreach (var action in Enum.GetValues<ControlAction>()) controls[action] = ControlBlocker(agent, action);
        return new AgentCapabilities(transports, controls);
    }

    /// <summary>Null when the action is possible, otherwise the human-readable reason it is not.</summary>
    private string? ControlBlocker(Agent a, ControlAction action)
    {
        if (a.Kind == AgentKind.Sub || (a.SessionId is not null && ClaudePaths.IsSubId(a.Id, a.SessionId))) return SubAgentReason;
        if (!options.Value.EnableControl) return "Process control is disabled in the daemon configuration (EnableControl=false)";
        if (!proc.IsAlive(a.Pid, a.ProcStart)) return "No live process is known for this session";
        var stopped = proc.State(a.Pid!.Value) == 'T';
        return action switch
        {
            ControlAction.Pause when a.Status == AgentStatus.Paused || stopped => "Already paused",
            ControlAction.Resume when a.Status != AgentStatus.Paused && !stopped => "Not paused",
            ControlAction.Interrupt when !HasPty(a) => "Interrupt types ESC into the terminal; only sessions started with `agentctl run` have one",
            _ => null,
        };
    }

    public async Task<ControlResult> ControlAsync(Agent agent, ControlAction action, CancellationToken ct)
    {
        if (ControlBlocker(agent, action) is { } why) return new ControlResult(false, why);
        var pid = agent.Pid!.Value;
        var o = new Origin(EventSources.User, agent.SessionId ?? "", agent.Id, time.GetUtcNow());
        switch (action)
        {
            case ControlAction.Interrupt:
                var ok = await terminals.WriteAsync(agent.TerminalId!, "\x1b"u8.ToArray(), ct);
                return new ControlResult(ok, ok ? "ESC sent to the terminal" : "Terminal is gone");

            case ControlAction.Pause:
            {
                var tree = Tree(pid);
                var failed = tree.Count(p => signals.Send(p, Signals.SigStop) != 0);
                if (!await WaitFor(() => proc.State(pid) == 'T', ct))
                    return new ControlResult(false, $"SIGSTOP sent to {tree.Count} process(es) but pid {pid} is not stopped");
                await sink.PublishAsync(ClaudeEventMapper.Status(o with { Ts = time.GetUtcNow() }, EventTypes.AgentStatusChanged,
                    AgentStatus.Paused, "Paused by user (SIGSTOP)", evidence: Evidence.Observed), ct);
                return new ControlResult(true, $"Paused {tree.Count - failed} process(es)");
            }
            case ControlAction.Resume:
            {
                var tree = Tree(pid);
                var failed = tree.AsEnumerable().Reverse().Count(p => signals.Send(p, Signals.SigCont) != 0);
                if (!await WaitFor(() => proc.State(pid) is not 'T' and not null, ct))
                    return new ControlResult(false, $"SIGCONT sent but pid {pid} is still stopped");
                await sink.PublishAsync(ClaudeEventMapper.Status(o with { Ts = time.GetUtcNow() }, EventTypes.AgentStatusChanged,
                    AgentStatus.Running, "Resumed by user (SIGCONT)", evidence: Evidence.Observed), ct);
                return new ControlResult(true, $"Resumed {tree.Count - failed} process(es)");
            }
            case ControlAction.Terminate:
            case ControlAction.Kill:
            {
                var sig = action == ControlAction.Kill ? Signals.SigKill : Signals.SigTerm;
                // Windows terminates without cleanup in the agent, so its children would be orphaned: take the tree.
                var targets = action == ControlAction.Kill || OperatingSystem.IsWindows() ? Tree(pid) : [pid];
                if (proc.State(pid) == 'T') signals.Send(pid, Signals.SigCont); // a stopped process cannot handle SIGTERM
                var err = signals.Send(pid, sig);
                foreach (var child in targets.Where(p => p != pid)) signals.Send(child, sig);
                if (err != 0) return new ControlResult(false, $"kill({pid}) failed: errno {err}");
                var name = action == ControlAction.Kill ? "SIGKILL" : "SIGTERM";
                if (!await WaitFor(() => !proc.IsAlive(pid, agent.ProcStart), ct))
                    return new ControlResult(true, $"{name} sent; process {pid} is still running");
                await sink.PublishAsync(ClaudeEventMapper.Status(o with { Ts = time.GetUtcNow() }, EventTypes.AgentStatusChanged,
                    AgentStatus.Cancelled, $"{(action == ControlAction.Kill ? "Killed" : "Terminated")} by user ({name})",
                    evidence: Evidence.Observed), ct);
                return new ControlResult(true, $"{name} sent; process {pid} exited");
            }
        }
        return new ControlResult(false, "Unsupported action");
    }

    private IReadOnlyList<int> Tree(int pid)
    {
        var tree = processes.Tree(pid);
        return tree.Contains(pid) ? tree : [pid, .. tree];
    }

    private async Task<bool> WaitFor(Func<bool> condition, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + VerifyTimeout;
        while (true)
        {
            if (condition()) return true;
            if (time.GetUtcNow() >= deadline) return false;
            await Task.Delay(25, ct);
        }
    }

    public async Task<DeliveryResult> SendAsync(Agent agent, OutgoingMessage message, CancellationToken ct)
    {
        if (HasPty(agent))
        {
            var ok = await terminals.WriteAsync(agent.TerminalId!, Encoding.UTF8.GetBytes(message.Text), ct)
                     && await terminals.WriteAsync(agent.TerminalId!, "\r"u8.ToArray(), ct);
            if (ok)
            {
                ctx.AddPendingReply(agent.Id, new PendingReply(message.MessageId, time.GetUtcNow()));
                return new DeliveryResult(true, true, Transports.Pty, "Typed into the agent's terminal");
            }
        }
        if (!agent.IsTerminal && hooks.IsInstalled())
        {
            // Say honestly what will happen: only an idle session with an armed waiter is woken right away.
            var idle = agent.Status is AgentStatus.Waiting or AgentStatus.Blocked;
            var armed = rewake?.WaitingCount(agent.Id) > 0;
            mailbox.Enqueue(new PendingMessage(message.MessageId, agent.Id, message.Text, time.GetUtcNow()));
            var detail = !idle
                ? "Queued; the agent is working and receives it at its next tool call"
                : armed
                    ? "Queued; waking the idle session now (asyncRewake hook)"
                    : agent.Kind == AgentKind.Main
                        ? $"Queued; this idle session has no wake-up hook armed (it was started before the hooks were installed), so it gets the message at its next action. Restart it to enable instant delivery: /exit, then `claude --resume {agent.SessionId}`"
                        : "Queued; idle sub-agents are reached at their next tool call or when they finish";
            return new DeliveryResult(true, false, Transports.HookMailbox, detail);
        }
        return new DeliveryResult(false, false, HasPty(agent) ? Transports.Pty : Transports.HookMailbox,
            "No transport available: start the session with `agentctl run` or install hooks with `agentctl hooks install`");
    }
}
