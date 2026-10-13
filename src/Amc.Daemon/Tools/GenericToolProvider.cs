using System.Text;
using Amc.Core.Domain;
using Amc.Core.Providers;
using Amc.Daemon.Collectors.Processes;

namespace Amc.Daemon.Tools;

/// <summary>
/// Messaging and control for a tool with no native adapter. Signals work for any detected process;
/// messages and interrupts work for sessions launched inside a Overhive terminal (typed into the PTY).
/// Agents that speak the open protocol also get the protocol inbox.
/// </summary>
public sealed class GenericToolProvider(AgentTool tool, ITerminalHub terminals, ILiveProcesses live, ProtocolInbox inbox) : IAgentProvider
{
    public string Id => tool.Id;

    private bool HasPty(Agent a) => a.TerminalId is not null && terminals.IsAttached(a.TerminalId);
    private static bool Alive(Agent a) => a.Pid is > 1 && ProcessSignals.IsAlive(a.Pid.Value, a.ProcStart);

    public AgentCapabilities Capabilities(Agent agent)
    {
        var transports = new List<string>();
        if (HasPty(agent)) transports.Add(Transports.Pty);
        if (!agent.IsTerminal && inbox.IsListening(agent.Id)) transports.Add(ProtocolInbox.Transport);
        string? NeedsProcess() => Alive(agent) ? null : "No live process for this agent";
        var controls = new Dictionary<ControlAction, string?>
        {
            [ControlAction.Pause] = agent.Status == AgentStatus.Paused ? "Already paused" : NeedsProcess(),
            [ControlAction.Resume] = agent.Status == AgentStatus.Paused ? NeedsProcess() : "Not paused",
            [ControlAction.Interrupt] = HasPty(agent) ? null : "Interrupt types Ctrl-C into the terminal; only sessions started from Overhive have one",
            [ControlAction.Terminate] = NeedsProcess(),
            [ControlAction.Kill] = NeedsProcess(),
        };
        return new AgentCapabilities(transports, controls);
    }

    public async Task<ControlResult> ControlAsync(Agent agent, ControlAction action, CancellationToken ct)
    {
        if (action == ControlAction.Interrupt)
            return await terminals.WriteAsync(agent.TerminalId!, "\x03"u8.ToArray(), ct)
                ? new(true, "Sent Ctrl-C to the terminal") : new(false, "Terminal is gone");
        if (agent.Pid is not > 1 || !Alive(agent)) return new(false, "No live process");
        var pid = agent.Pid.Value;
        var n = action switch
        {
            ControlAction.Pause => ProcessSignals.PauseTree(live, pid),
            ControlAction.Resume => ProcessSignals.ResumeTree(live, pid),
            ControlAction.Terminate => ProcessSignals.TerminateTree(live, pid),
            ControlAction.Kill => ProcessSignals.SendTree(live, pid, Signal.Kill, childrenFirst: true),
            _ => 0,
        };
        return n > 0 ? new(true, $"{action} sent to {n} process(es)") : new(false, $"{action} failed");
    }

    public async Task<DeliveryResult> SendAsync(Agent agent, OutgoingMessage message, CancellationToken ct)
    {
        if (HasPty(agent)
            && await terminals.WriteAsync(agent.TerminalId!, Encoding.UTF8.GetBytes(message.Text), ct)
            && await terminals.WriteAsync(agent.TerminalId!, "\r"u8.ToArray(), ct))
            return new DeliveryResult(true, true, Transports.Pty, "Typed into the agent's terminal");
        if (inbox.IsListening(agent.Id))
        {
            inbox.Enqueue(agent.Id, message);
            return new DeliveryResult(true, false, ProtocolInbox.Transport, ToolHookReceiver.CanDeliver.Contains(tool.Id)
                ? $"Queued; {tool.Name} receives it at its next tool call or when its turn ends (through its hooks)"
                : "Queued in the protocol inbox; delivered when the tool next polls");
        }
        return new DeliveryResult(false, false, Transports.Pty,
            $"{tool.Name} can only be messaged when it was started from Overhive (terminal) or reports through the open protocol");
    }
}
