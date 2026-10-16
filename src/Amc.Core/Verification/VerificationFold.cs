using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Core.Verification;

/// <summary>
/// Reducer part of "claimed vs observed": folds the ingredients (claims from agent text, finished
/// test/build commands, file writes, settle events) into <see cref="WorldState.Verification"/>, and
/// keeps each agent's <see cref="Agent.Verification"/> counts current. The checks themselves are a
/// pure function of state (<see cref="Verifier"/>), so they are recomputed, never stored.
/// </summary>
public static class VerificationFold
{
    public static void Apply(WorldState s, AgentEvent e)
    {
        var dirty = s.VerificationDirty;
        switch (e.Type)
        {
            case EventTypes.AgentOutput when e.AgentId is not null && s.Agents.ContainsKey(e.AgentId) && e.Payload<AgentTextData>() is { } o:
            {
                var found = ClaimExtractor.Extract(o.Text);
                if (found.Count == 0) break;
                var log = Log(s, e.AgentId);
                for (var i = 0; i < found.Count; i++)
                {
                    var id = $"{e.Id}#{i}";
                    if (log.Claims.Any(c => c.Id == id)) continue;
                    log.Claims.Add(new Claim { Id = id, Kind = found[i].Kind, Excerpt = found[i].Excerpt, Path = found[i].Path, At = e.Ts });
                }
                Trim(log.Claims, AgentVerification.MaxClaims);
                dirty.Agents.Add(e.AgentId);
                break;
            }

            case EventTypes.CommandStarted when e.AgentId is not null && e.Payload<CommandData>() is { Background: false } c:
            {
                var (test, build) = (VerificationRules.IsTestCommand(c.Command), VerificationRules.IsBuildCommand(c.Command));
                if (!test && !build) break;
                var log = Log(s, e.AgentId);
                log.Pending[c.ToolUseId] = new PendingRun(Reducer.Truncate(c.Command, 300), test, build, e.Ts);
                if (log.Pending.Count > AgentVerification.MaxPending)
                    foreach (var k in log.Pending.OrderBy(kv => kv.Value.StartedAt).Take(log.Pending.Count - AgentVerification.MaxPending).Select(kv => kv.Key).ToList())
                        log.Pending.Remove(k);
                break;
            }

            case EventTypes.CommandCompleted or EventTypes.CommandFailed when e.AgentId is not null && e.Payload<CommandData>() is { } c:
            {
                var log = s.Verification.GetValueOrDefault(e.AgentId);
                PendingRun? pending = null;
                log?.Pending.Remove(c.ToolUseId, out pending);
                if (c.Background) break;
                var command = string.IsNullOrEmpty(c.Command) ? pending?.Command : c.Command;
                if (command is null) break;
                var (test, build) = (VerificationRules.IsTestCommand(command), VerificationRules.IsBuildCommand(command));
                if (!test && !build) break;
                var failed = e.Type == EventTypes.CommandFailed || c.ExitCode is not (null or 0);
                AddRun(s, e, c.ToolUseId, command, test, build, c.ExitCode,
                    c.Interrupted ? "interrupted" : failed ? "failed" : "passed", pending?.StartedAt);
                break;
            }

            // Protocol tools may end a shell command with a plain tool_end (no command repeated).
            case EventTypes.ToolCompleted or EventTypes.ToolFailed when e.AgentId is not null && e.Payload<ToolCallData>() is { } t
                && s.Verification.GetValueOrDefault(e.AgentId) is { } log && log.Pending.Remove(t.ToolUseId, out var p):
                AddRun(s, e, t.ToolUseId, p.Command, p.IsTest, p.IsBuild, null,
                    e.Type == EventTypes.ToolFailed ? "failed" : "passed", p.StartedAt);
                break;

            case EventTypes.FileCreated or EventTypes.FileModified or EventTypes.FileDeleted or EventTypes.FileRenamed
                when e.AgentId is not null && e.WorktreeId is not null && e.FilePath is not null && s.Agents.ContainsKey(e.AgentId):
                AddEdit(s, e.AgentId, new EditMark(e.WorktreeId, e.FilePath, e.Ts, VerificationRules.IsTestFile(e.FilePath), e.Evidence));
                dirty.Worktrees.Add(e.WorktreeId);
                break;

            case EventTypes.GitFileStatus when e.WorktreeId is not null && e.FilePath is not null:
                if (e.Payload<GitFileStatusData>() is { AttributedAgentId: { } attributed, Status: not "clean" } && s.Agents.ContainsKey(attributed))
                    AddEdit(s, attributed, new EditMark(e.WorktreeId, e.FilePath, e.Ts, VerificationRules.IsTestFile(e.FilePath), Evidence.Inferred));
                dirty.Worktrees.Add(e.WorktreeId);
                break;

            case EventTypes.GitCommitCreated when e.WorktreeId is not null:
                dirty.Worktrees.Add(e.WorktreeId);
                if (e.AgentId is not null) MarkWithAncestors(s, e.AgentId);
                break;

            case EventTypes.VerificationSettled when e.AgentId is not null && e.Payload<VerificationSettledData>() is { } st:
                if (s.Verification.GetValueOrDefault(e.AgentId)?.Claims.FirstOrDefault(c => c.Id == st.ClaimId) is { } claim)
                {
                    claim.Settled = true;
                    claim.Exists = st.Exists;
                    dirty.Agents.Add(e.AgentId);
                }
                break;

            case EventTypes.AgentCreated or EventTypes.AgentSpawned when e.AgentId is not null:
                MarkWithAncestors(s, e.AgentId); // a new sub-agent widens its ancestors' evidence
                break;
        }
    }

    /// <summary>Recomputes the counts of agents whose evidence changed. Run after a batch (ApplyBatch / restore do).</summary>
    public static void Recompute(WorldState s, HashSet<EntityRef> changed)
    {
        var dirty = s.VerificationDirty;
        if (dirty.Agents.Count == 0 && dirty.Worktrees.Count == 0) return;
        if (dirty.Worktrees.Count > 0)
            foreach (var (id, log) in s.Verification)
                if (log.Claims.Count > 0 && Verifier.TreeWorktrees(s, id).Overlaps(dirty.Worktrees)) dirty.Agents.Add(id);
        foreach (var id in dirty.Agents)
        {
            if (!s.Agents.TryGetValue(id, out var a)) continue;
            var summary = s.Verification.ContainsKey(id) || a.ChildIds.Count > 0 ? Verifier.Evaluate(s, id).Summary : null;
            if (summary is { Confirmed: 0, Contradicted: 0, Unverified: 0, Flags: 0 }) summary = null;
            if (summary == a.Verification) continue;
            a.Verification = summary;
            changed.Add(new(EntityKinds.Agent, id));
        }
        dirty.Agents.Clear();
        dirty.Worktrees.Clear();
    }

    private static AgentVerification Log(WorldState s, string agentId)
    {
        if (!s.Verification.TryGetValue(agentId, out var log)) s.Verification[agentId] = log = new AgentVerification();
        return log;
    }

    private static void AddRun(WorldState s, AgentEvent e, string toolUseId, string command, bool test, bool build, int? exit, string outcome, DateTimeOffset? startedAt)
    {
        var log = Log(s, e.AgentId!);
        if (log.Runs.Any(r => r.ToolUseId == toolUseId)) return;
        log.Runs.Add(new CheckRun
        {
            ToolUseId = toolUseId, AgentId = e.AgentId!, Command = Reducer.Truncate(command, 300), IsTest = test, IsBuild = build,
            ExitCode = exit, Outcome = outcome, StartedAt = startedAt, EndedAt = e.Ts,
        });
        Trim(log.Runs, AgentVerification.MaxRuns);
        MarkWithAncestors(s, e.AgentId!);
    }

    private static void AddEdit(WorldState s, string agentId, EditMark edit)
    {
        var log = Log(s, agentId);
        log.Edits.Add(edit);
        Trim(log.Edits, AgentVerification.MaxEdits);
        MarkWithAncestors(s, agentId);
    }

    /// <summary>An agent's checks include its sub-agents' evidence, so ancestors are re-evaluated too.</summary>
    private static void MarkWithAncestors(WorldState s, string agentId)
    {
        var dirty = s.VerificationDirty.Agents;
        var depth = 0;
        for (var id = agentId; id is not null && depth++ < 64; id = s.Agents.GetValueOrDefault(id)?.ParentId) dirty.Add(id);
    }

    private static void Trim<T>(List<T> list, int max)
    {
        if (list.Count > max) list.RemoveRange(0, list.Count - max);
    }
}

/// <summary>Agents/worktrees whose verification counts need recomputing (not serialized).</summary>
public sealed class VerificationDirtySet
{
    public HashSet<string> Agents { get; } = [];
    public HashSet<string> Worktrees { get; } = [];
}
