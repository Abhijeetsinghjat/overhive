using Amc.Core.Domain;
using Amc.Core.Events;

namespace Amc.Core.State;

/// <summary>
/// Pure fold: (WorldState, AgentEvent) → WorldState. No IO, no wall clock.
/// Returns the set of entities that changed so the daemon can push patches.
/// </summary>
public static class Reducer
{
    /// <summary>Bump whenever folding semantics change: snapshots from another version are discarded and the log is replayed.</summary>
    public const int Version = 8;

    public static HashSet<EntityRef> ApplyBatch(WorldState s, IEnumerable<AgentEvent> events)
    {
        var changed = new HashSet<EntityRef>();
        foreach (var e in events) Apply(s, e, changed);
        ConflictDetector.Recompute(s, changed);
        return changed;
    }

    /// <summary>Applies one event. Callers must run <see cref="ConflictDetector.Recompute"/> afterwards (ApplyBatch does).</summary>
    public static void Apply(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.Seq > 0)
        {
            if (e.Seq <= s.Seq) return; // already folded
            s.Seq = e.Seq;
        }
        if (e.Ts > s.Clock) s.Clock = e.Ts;

        var agent = e.AgentId is null ? null : s.Agents.GetValueOrDefault(e.AgentId);
        if (agent is not null && e.Ts > agent.LastActivityAt && IsActivity(e.Type))
        {
            agent.LastActivityAt = e.Ts;
            changed.Add(new(EntityKinds.Agent, agent.Id));
        }

        switch (e.Type)
        {
            case EventTypes.AgentCreated:
            case EventTypes.AgentSpawned:
                OnAgentCreated(s, e, changed);
                break;
            case EventTypes.AgentStarted:
            case EventTypes.AgentStatusChanged:
            case EventTypes.AgentCompleted:
            case EventTypes.AgentFailed:
                if (agent is not null && e.Payload<AgentStatusData>() is { } st)
                    SetStatus(s, agent, st.Status, st.Reason, e, changed, reopen: false);
                break;
            case EventTypes.AgentUpdated:
                if (agent is not null && e.Payload<AgentUpdatedData>() is { } up)
                {
                    var newProcess = up.Pid is not null && agent.Pid is not null && up.Pid != agent.Pid;
                    OnAgentUpdated(agent, up, changed);
                    if (e.WorktreeId is not null) TouchWorktree(s, agent, e, changed); // e.g. a generic tool's working directory
                    // A finished session that shows up in a new process was resumed: it is alive again.
                    if (newProcess && agent.IsTerminal)
                        SetStatus(s, agent, AgentStatus.Starting, $"Session resumed in a new process (pid {up.Pid})", e, changed, reopen: true);
                    if (up.TerminalId is not null && s.Terminals.TryGetValue(up.TerminalId, out var term) && term.AgentId != agent.Id)
                    {
                        term.AgentId = agent.Id;
                        changed.Add(new(EntityKinds.Terminal, term.Id));
                    }
                }
                break;
            case EventTypes.AgentPrompt:
                if (agent is not null && e.Payload<AgentTextData>() is { } p)
                {
                    agent.LastPrompt = Truncate(p.Text, 500);
                    agent.Title ??= Truncate(p.Text, 120);
                    SetStatus(s, agent, AgentStatus.Running, "Prompt submitted", e, changed, reopen: true);
                    changed.Add(new(EntityKinds.Agent, agent.Id));
                }
                break;
            case EventTypes.AgentOutput:
                if (agent is not null && e.Payload<AgentTextData>() is { } o)
                {
                    agent.LastOutput = Truncate(o.Text, 500);
                    changed.Add(new(EntityKinds.Agent, agent.Id));
                    LinkReply(s, agent.Id, o.Text, e.Ts, changed);
                }
                break;
            case EventTypes.AgentUsage:
                if (agent is not null && e.Payload<UsageData>() is { } u) OnUsage(s, agent, u, e.Ts, changed);
                break;
            case EventTypes.AgentNotification:
                break; // timeline only; status is set by an explicit status event from the provider

            case EventTypes.MessageSent:
            case EventTypes.MessageDelivered:
            case EventTypes.MessageFailed:
            case EventTypes.MessageReceived:
                OnMessage(s, e, changed);
                break;

            case EventTypes.ToolStarted:
            case EventTypes.CommandStarted:
                if (agent is not null) OnToolStarted(s, agent, e, changed);
                break;
            case EventTypes.ToolCompleted:
            case EventTypes.ToolFailed:
            case EventTypes.CommandCompleted:
            case EventTypes.CommandFailed:
                if (agent is not null) OnToolEnded(agent, e, changed);
                break;

            case EventTypes.FileCreated:
            case EventTypes.FileModified:
            case EventTypes.FileDeleted:
            case EventTypes.FileRenamed:
                OnFileChanged(s, agent, e, changed);
                break;
            case EventTypes.FileRead:
                if (agent is not null && e.FilePath is not null) { agent.CurrentFile = e.FilePath; changed.Add(new(EntityKinds.Agent, agent.Id)); }
                break;

            case EventTypes.GitFileStatus:
                OnGitFileStatus(s, e, changed);
                break;
            case EventTypes.RepositoryDiscovered:
                OnRepository(s, e, changed);
                break;
            case EventTypes.WorktreeCreated:
            case EventTypes.WorktreeChanged:
                OnWorktree(s, e, changed);
                break;
            case EventTypes.WorktreeDeleted:
                if (e.WorktreeId is not null && s.Worktrees.TryGetValue(e.WorktreeId, out var gone))
                {
                    if (!gone.Removed) s.ConflictDirtyWorktrees.Add(gone.Id);
                    gone.Removed = true; gone.UpdatedAt = e.Ts;
                    changed.Add(new(EntityKinds.Worktree, gone.Id));
                }
                break;
            case EventTypes.GitBranchChanged:
            case EventTypes.GitCheckout:
                if (e.WorktreeId is not null && s.Worktrees.TryGetValue(e.WorktreeId, out var wtb) && e.Payload<GitRefData>() is { } r)
                {
                    wtb.Branch = r.Branch; wtb.Head = r.Head ?? wtb.Head; wtb.Detached = r.Branch is null; wtb.UpdatedAt = e.Ts;
                    changed.Add(new(EntityKinds.Worktree, wtb.Id));
                }
                break;
            case EventTypes.GitCommitCreated:
                OnCommit(s, e, changed);
                break;
            case EventTypes.GitMerge:
            case EventTypes.GitRebase:
            case EventTypes.GitConflict:
                break; // reflected via worktree.changed (operation/conflicted) — timeline only

            case EventTypes.TestStarted:
            case EventTypes.TestPassed:
            case EventTypes.TestFailed:
                OnTest(s, e, changed);
                break;

            case EventTypes.TasksUpdated:
                if (agent is not null && e.Payload<TaskListData>() is { } tl) OnTasks(agent, tl, e.Ts, changed);
                break;

            case EventTypes.ProcessStarted:
                break; // live process data is a volatile overlay; event kept for the timeline
            case EventTypes.ProcessExited:
                OnProcessExited(s, e, changed);
                break;

            case EventTypes.TerminalStarted:
            case EventTypes.TerminalExited:
                OnTerminal(s, e, changed);
                break;

            case EventTypes.RiskDetected:
                Risk.RiskReducer.Apply(s, agent, e, changed);
                break;
        }
    }

    private static bool IsActivity(string type) =>
        type is not (EventTypes.AgentCreated or EventTypes.ProcessExited or EventTypes.AuditControl or EventTypes.GitFileStatus
            or EventTypes.WorktreeChanged or EventTypes.AgentStatusChanged or EventTypes.MessageSent
            or EventTypes.MessageFailed or EventTypes.AgentUpdated or EventTypes.RiskDetected);

    // ---- agents ---------------------------------------------------------------

    private static void OnAgentCreated(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.AgentId is null || e.Payload<AgentCreatedData>() is not { } d) return;
        if (!s.Agents.TryGetValue(e.AgentId, out var a))
        {
            // A "sub-agent" with neither a parent nor a type is not evidence of a sub-agent
            // (Claude Code emits such stop events for internal helpers); don't materialize it.
            if (d.Kind == AgentKind.Sub && d.ParentId is null && string.IsNullOrEmpty(d.AgentType)) return;
            a = new Agent
            {
                Id = e.AgentId,
                Provider = d.Provider,
                Kind = d.Kind,
                SessionId = e.SessionId,
                CreatedAt = e.Ts,
                // Registration is not activity; a spawn is (the parent just acted).
                LastActivityAt = e.Type == EventTypes.AgentSpawned ? e.Ts : DateTimeOffset.MinValue,
                Status = e.Type == EventTypes.AgentSpawned ? AgentStatus.Running : AgentStatus.Created,
                StatusReason = e.Type == EventTypes.AgentSpawned ? "Spawned by parent" : null,
                StatusEvidence = e.Evidence,
            };
            a.StatusAt = e.Ts;
            if (a.Status == AgentStatus.Running) a.StartedAt = e.Ts;
            s.Agents[a.Id] = a;
        }
        a.ParentId ??= d.ParentId;
        a.AgentType ??= d.AgentType;
        a.Title ??= d.Title;
        a.Description ??= d.Description;
        a.Cwd ??= d.Cwd;
        a.Pid ??= d.Pid;
        a.ProcStart ??= d.ProcStart;
        a.TranscriptPath ??= d.TranscriptPath;
        a.SpawnToolUseId ??= d.SpawnToolUseId;
        a.Model ??= d.Model;
        a.Version ??= d.Version;
        if (e.Ts < a.CreatedAt) a.CreatedAt = e.Ts;
        changed.Add(new(EntityKinds.Agent, a.Id));

        if (a.ParentId is not null && s.Agents.TryGetValue(a.ParentId, out var parent) && !parent.ChildIds.Contains(a.Id))
        {
            parent.ChildIds.Add(a.Id);
            changed.Add(new(EntityKinds.Agent, parent.Id));
        }
        // Adopt children that arrived before their parent (out-of-order collectors).
        foreach (var orphan in s.Agents.Values.Where(x => x.ParentId == a.Id && !a.ChildIds.Contains(x.Id)))
            a.ChildIds.Add(orphan.Id);
    }

    private static void OnAgentUpdated(Agent a, AgentUpdatedData u, HashSet<EntityRef> changed)
    {
        if (u.Title is not null) a.Title = u.Title;
        if (u.Cwd is not null) a.Cwd = u.Cwd;
        if (u.Model is not null) a.Model = u.Model;
        if (u.GitBranch is not null) a.GitBranch = u.GitBranch;
        if (u.Pid is not null) a.Pid = u.Pid;
        if (u.ProcStart is not null) a.ProcStart = u.ProcStart;
        if (u.TerminalId is not null) a.TerminalId = u.TerminalId;
        if (u.TranscriptPath is not null) a.TranscriptPath = u.TranscriptPath;
        if (u.Transports is not null) a.Transports = [.. u.Transports];
        changed.Add(new(EntityKinds.Agent, a.Id));
    }

    public static void SetStatus(WorldState s, Agent a, AgentStatus to, string? reason, AgentEvent e,
        HashSet<EntityRef> changed, bool reopen)
    {
        // Evidence older than what the current status is based on never overrides it (e.g. backfilled
        // history arriving after a live registry status, or late tool events after completion).
        if (e.Ts < a.StatusAt) return;
        if (!AgentStateMachine.CanTransition(a.Status, to, reopen)) return;
        if (AgentStateMachine.IsTerminal(a.Status) != AgentStateMachine.IsTerminal(to)) s.ConflictDirtyAgents.Add(a.Id);
        a.Status = to;
        a.StatusAt = e.Ts;
        a.StatusReason = reason;
        a.StatusEvidence = e.Evidence;
        if (to == AgentStatus.Running) a.StartedAt ??= e.Ts;
        if (AgentStateMachine.IsTerminal(to))
        {
            a.CompletedAt = e.Ts;
            a.CurrentTool = a.CurrentToolUseId = a.CurrentAction = a.CurrentCommand = null;
        }
        else a.CompletedAt = null;
        changed.Add(new(EntityKinds.Agent, a.Id));

        // A parent that ends while sub-agents are still active cannot vouch for them anymore.
        if (to is AgentStatus.Disconnected or AgentStatus.Failed or AgentStatus.Cancelled or AgentStatus.Completed && a.Kind == AgentKind.Main)
        {
            foreach (var childId in Descendants(s, a))
            {
                var c = s.Agents[childId];
                if (!AgentStateMachine.IsActive(c.Status) && c.Status != AgentStatus.Created) continue;
                var childReason = to == AgentStatus.Disconnected
                    ? "Parent session process exited while sub-agent was active"
                    : "Parent session ended while sub-agent was active";
                SetStatus(s, c, AgentStatus.Disconnected, childReason, e, changed, reopen: false);
            }
        }
    }

    private static IEnumerable<string> Descendants(WorldState s, Agent a)
    {
        var stack = new Stack<string>(a.ChildIds);
        var seen = new HashSet<string>();
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id) || !s.Agents.TryGetValue(id, out var c)) continue;
            yield return id;
            foreach (var g in c.ChildIds) stack.Push(g);
        }
    }

    private static void OnToolStarted(WorldState s, Agent a, AgentEvent e, HashSet<EntityRef> changed)
    {
        a.ToolCount++;
        if (e.Type == EventTypes.CommandStarted && e.Payload<CommandData>() is { } c)
        {
            a.CurrentTool = "Bash"; a.CurrentToolUseId = c.ToolUseId;
            a.CurrentCommand = Truncate(c.Command, 300);
            a.CurrentAction = c.Description ?? Truncate(c.Command, 120);
        }
        else if (e.Payload<ToolCallData>() is { } t)
        {
            a.CurrentTool = t.Tool; a.CurrentToolUseId = t.ToolUseId;
            a.CurrentAction = t.Summary ?? t.Tool;
            if (t.FilePath is not null) a.CurrentFile = t.FilePath;
        }
        TouchWorktree(s, a, e, changed);
        SetStatus(s, a, AgentStatus.Running, "Tool call in progress", e, changed, reopen: true);
        changed.Add(new(EntityKinds.Agent, a.Id));
    }

    private static void OnToolEnded(Agent a, AgentEvent e, HashSet<EntityRef> changed)
    {
        var failed = e.Type is EventTypes.ToolFailed or EventTypes.CommandFailed;
        var toolUseId = e.Type.StartsWith("command.")
            ? e.Payload<CommandData>()?.ToolUseId
            : e.Payload<ToolCallData>()?.ToolUseId;
        if (failed) a.ToolFailures++;
        if (toolUseId is not null && toolUseId == a.CurrentToolUseId)
        {
            a.CurrentTool = a.CurrentToolUseId = a.CurrentCommand = null;
            a.CurrentAction = null;
        }
        changed.Add(new(EntityKinds.Agent, a.Id));
    }

    private static void TouchWorktree(WorldState s, Agent a, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.WorktreeId is null || !s.Worktrees.TryGetValue(e.WorktreeId, out var wt)) return;
        a.Worktrees[wt.Id] = e.Ts;
        // Inferred locations (e.g. a shell command's cwd fallback) never displace a reported one.
        if (e.Evidence != Evidence.Inferred || a.CurrentWorktreeId is null)
        {
            a.CurrentWorktreeId = wt.Id;
            a.CurrentRepositoryId = wt.RepositoryId;
        }
        a.Repositories[wt.RepositoryId] = e.Ts;
        wt.Agents[a.Id] = e.Ts;
        changed.Add(new(EntityKinds.Worktree, wt.Id));
    }

    private static void OnTasks(Agent a, TaskListData tl, DateTimeOffset ts, HashSet<EntityRef> changed)
    {
        var old = a.Tasks.ToDictionary(t => t.Key);
        a.Tasks = tl.Tasks.Select(t =>
        {
            var prev = old.GetValueOrDefault(t.Key);
            var task = new AgentTask
            {
                Key = t.Key, Title = t.Title, State = t.State, ActiveForm = t.ActiveForm, UpdatedAt = ts,
                StartedAt = prev?.StartedAt, CompletedAt = prev?.CompletedAt,
            };
            if (t.State == TaskState.InProgress) task.StartedAt ??= ts;
            if (t.State == TaskState.Completed) { task.StartedAt ??= ts; task.CompletedAt ??= ts; }
            return task;
        }).ToList();
        changed.Add(new(EntityKinds.Agent, a.Id));
    }

    private static void OnProcessExited(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.Payload<ProcessExitData>() is not { } p) return;
        var a = (e.AgentId is not null ? s.Agents.GetValueOrDefault(e.AgentId) : null) ?? s.AgentByPid(p.Pid, p.ProcStart);
        if (a is null || a.Pid != p.Pid) return; // only the agent's own process decides its liveness
        if (!a.IsTerminal)
            SetStatus(s, a, AgentStatus.Disconnected, p.Reason ?? "Process exited unexpectedly", e, changed, reopen: false);
    }

    private static void OnUsage(WorldState s, Agent a, UsageData u, DateTimeOffset ts, HashSet<EntityRef> changed)
    {
        var prev = s.UsageMarks.GetValueOrDefault(u.MessageId);
        // Count only the increase over what this response already contributed (streamed records grow).
        long D(long now, long? before) => Math.Max(0, now - (before ?? 0));
        var di = D(u.Input, prev?.Input); var dout = D(u.Output, prev?.Output);
        var dcw = D(u.CacheWrite, prev?.CacheWrite); var dcr = D(u.CacheRead, prev?.CacheRead);
        if (prev is null) a.Usage.Responses++;
        if (di + dout + dcw + dcr == 0 && prev is not null) return;
        a.Usage.Add(di, dout, dcw, dcr);
        if (u.Model is { Length: > 0 } m)
        {
            if (!a.UsageByModel.TryGetValue(m, out var byModel)) a.UsageByModel[m] = byModel = new TokenUsage();
            byModel.Add(di, dout, dcw, dcr);
            if (prev is null) byModel.Responses++;
        }
        s.UsageMarks[u.MessageId] = new UsageMark(
            Math.Max(u.Input, prev?.Input ?? 0), Math.Max(u.Output, prev?.Output ?? 0),
            Math.Max(u.CacheWrite, prev?.CacheWrite ?? 0), Math.Max(u.CacheRead, prev?.CacheRead ?? 0), ts);
        Bound(s.UsageMarks, WorldState.MaxUsageMarks, x => x.At);
        changed.Add(new(EntityKinds.Agent, a.Id));
    }

    // ---- messages ---------------------------------------------------------------

    private static void OnMessage(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        switch (e.Type)
        {
            case EventTypes.MessageSent when e.AgentId is not null && e.Payload<MessageData>() is { } m:
                s.Messages[m.MessageId] = new Message
                {
                    Id = m.MessageId, AgentId = e.AgentId, Text = m.Text, Transport = m.Transport,
                    Status = MessageStatus.Queued, SentAt = e.Ts,
                };
                Bound(s.Messages, WorldState.MaxMessages, x => x.SentAt);
                changed.Add(new(EntityKinds.Message, m.MessageId));
                break;
            case EventTypes.MessageDelivered or EventTypes.MessageFailed when e.Payload<MessageStatusData>() is { } st
                && s.Messages.TryGetValue(st.MessageId, out var msg):
                msg.Status = e.Type == EventTypes.MessageDelivered ? MessageStatus.Delivered : MessageStatus.Failed;
                msg.Transport = st.Transport;
                msg.Detail = st.Detail;
                if (msg.Status == MessageStatus.Delivered) msg.DeliveredAt = e.Ts;
                changed.Add(new(EntityKinds.Message, msg.Id));
                break;
            case EventTypes.MessageReceived when e.Payload<MessageReplyData>() is { } r && s.Messages.TryGetValue(r.MessageId, out var rm):
                if (rm.RepliedAt is { } prev && e.Ts >= prev) break; // the earliest reply wins
                rm.Status = MessageStatus.Answered;
                rm.Reply = Truncate(r.Text, 4000);
                rm.RepliedAt = e.Ts;
                changed.Add(new(EntityKinds.Message, rm.Id));
                break;
        }
    }

    /// <summary>Max gap between delivery and an output for it to count as the reply (inferred link).</summary>
    public static readonly TimeSpan ReplyWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Inferred reply linkage: a message's reply is the agent's EARLIEST output after delivery.
    /// Decided by timestamps, not arrival order, because agents may persist text late.
    /// </summary>
    private static void LinkReply(WorldState s, string agentId, string text, DateTimeOffset ts, HashSet<EntityRef> changed)
    {
        foreach (var m in s.Messages.Values)
        {
            if (m.AgentId != agentId || m.DeliveredAt is not { } delivered) continue;
            if (ts < delivered || ts - delivered > ReplyWindow) continue;
            if (m.RepliedAt is { } prev && ts >= prev) continue;
            m.Status = MessageStatus.Answered;
            m.Reply = Truncate(text, 4000);
            m.RepliedAt = ts;
            changed.Add(new(EntityKinds.Message, m.Id));
        }
    }

    // ---- files & git ------------------------------------------------------------

    private static FileChange? UpsertFile(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.WorktreeId is null || e.FilePath is null) return null;
        var key = Ids.FileKey(e.WorktreeId, e.FilePath);
        if (!s.Files.TryGetValue(key, out var f))
        {
            f = new FileChange
            {
                Key = key, WorktreeId = e.WorktreeId, Path = e.FilePath,
                RepositoryId = e.RepositoryId ?? s.Worktrees.GetValueOrDefault(e.WorktreeId)?.RepositoryId,
                FirstChangedAt = e.Ts,
            };
            s.Files[key] = f;
            s.BuiltIndex?.AddFile(f);
        }
        f.LastChangedAt = e.Ts > f.LastChangedAt ? e.Ts : f.LastChangedAt;
        f.LastEventSeq = e.Seq;
        changed.Add(new(EntityKinds.File, key));
        return f;
    }

    private static void OnFileChanged(WorldState s, Agent? a, AgentEvent e, HashSet<EntityRef> changed)
    {
        var f = UpsertFile(s, e, changed);
        if (f is null) return;
        var d = e.Payload<FileChangeData>();
        f.LastChangeKind = e.Type["file.".Length..];
        f.EditCount++;
        if (d?.OldPath is not null) f.OldPath = d.OldPath;
        if (a is null) return;
        if (!f.Agents.ContainsKey(a.Id)) { a.FilesChanged++; s.BuiltIndex?.AddAttribution(a.Id, f.Key); }
        f.Agents[a.Id] = f.Agents.TryGetValue(a.Id, out var prev)
            ? prev with { LastAt = e.Ts, Edits = prev.Edits + 1, Evidence = Stronger(prev.Evidence, e.Evidence) }
            : new Attribution(e.Evidence, e.Ts, 1);
        s.BuiltIndex?.RecordWrite(f.WorktreeId, a.Id, f.Agents[a.Id].LastAt);
        a.CurrentFile = e.FilePath;
        TouchWorktree(s, a, e, changed);
        changed.Add(new(EntityKinds.Agent, a.Id));
    }

    private static void OnGitFileStatus(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.Payload<GitFileStatusData>() is not { } g) return;
        if (e.WorktreeId is null || e.FilePath is null) return;
        var key = Ids.FileKey(e.WorktreeId, e.FilePath);
        // A file that becomes clean and was never attributed to an agent is just noise: drop it.
        if (g.Status == "clean" && s.Files.TryGetValue(key, out var existing) && existing.Agents.Count == 0)
        {
            s.Files.Remove(key);
            s.BuiltIndex?.RemoveFile(existing);
            changed.Add(new(EntityKinds.File, key));
            return;
        }
        if (g.Status == "clean" && !s.Files.ContainsKey(key)) return;
        var f = UpsertFile(s, e, changed)!;
        f.GitStatus = g.Status;
        f.Added = g.Added;
        f.Removed = g.Removed;
        if (g.OldPath is not null) f.OldPath = g.OldPath;
        if (g.AttributedAgentId is not null && s.Agents.TryGetValue(g.AttributedAgentId, out var a))
        {
            if (!f.Agents.ContainsKey(a.Id)) { a.FilesChanged++; s.BuiltIndex?.AddAttribution(a.Id, f.Key); }
            f.Agents[a.Id] = f.Agents.TryGetValue(a.Id, out var prev)
                ? prev with { LastAt = e.Ts }
                : new Attribution(Evidence.Inferred, e.Ts, 0);
            changed.Add(new(EntityKinds.Agent, a.Id));
        }
    }

    private static void OnRepository(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.RepositoryId is null || e.Payload<RepositoryData>() is not { } r) return;
        if (!s.Repositories.TryGetValue(e.RepositoryId, out var repo))
            s.Repositories[e.RepositoryId] = repo = new Repository { Id = e.RepositoryId, Name = r.Name, CommonDir = r.CommonDir, DiscoveredAt = e.Ts };
        repo.Name = r.Name;
        repo.MainWorktree = r.MainWorktree ?? repo.MainWorktree;
        repo.WebUrl = r.WebUrl ?? repo.WebUrl;
        changed.Add(new(EntityKinds.Repository, repo.Id));
    }

    private static void OnWorktree(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.WorktreeId is null || e.Payload<WorktreeData>() is not { } w) return;
        if (!s.Worktrees.TryGetValue(e.WorktreeId, out var wt))
            s.Worktrees[e.WorktreeId] = wt = new Worktree { Id = e.WorktreeId, Root = w.Root, RepositoryId = w.RepositoryId, CreatedAt = e.Ts };
        wt.Root = w.Root;
        wt.RepositoryId = w.RepositoryId;
        wt.Branch = w.Branch; wt.Head = w.Head; wt.Detached = w.Detached; wt.IsMain = w.IsMain;
        if (wt.Removed) s.ConflictDirtyWorktrees.Add(wt.Id);
        wt.Locked = w.Locked; wt.Prunable = w.Prunable; wt.Removed = false;
        wt.Modified = w.Modified; wt.Added = w.Added; wt.DeletedFiles = w.Deleted; wt.Untracked = w.Untracked;
        wt.Conflicted = w.Conflicted; wt.Ahead = w.Ahead; wt.Behind = w.Behind; wt.Upstream = w.Upstream;
        wt.Operation = w.Operation;
        wt.UpdatedAt = e.Ts;
        changed.Add(new(EntityKinds.Worktree, wt.Id));
    }

    private static void OnCommit(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.WorktreeId is null || e.Payload<GitCommitData>() is not { } c) return;
        s.Commits[c.Sha] = new Commit
        {
            Sha = c.Sha, WorktreeId = e.WorktreeId, Subject = c.Subject, Branch = c.Branch, Author = c.Author,
            AgentId = e.AgentId, At = c.CommittedAt ?? e.Ts, FilesChanged = c.FilesChanged,
        };
        Bound(s.Commits, WorldState.MaxCommits, x => x.At);
        changed.Add(new(EntityKinds.Commit, c.Sha));
    }

    private static void OnTest(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.Payload<TestRunData>() is not { } t) return;
        if (!s.TestRuns.TryGetValue(t.ToolUseId, out var run))
            s.TestRuns[t.ToolUseId] = run = new TestRun { Id = t.ToolUseId, Runner = t.Runner, Command = t.Command, StartedAt = e.Ts };
        run.AgentId ??= e.AgentId;
        run.WorktreeId ??= e.WorktreeId;
        if (e.Type != EventTypes.TestStarted)
        {
            run.State = e.Type == EventTypes.TestPassed ? "passed" : "failed";
            run.EndedAt = e.Ts;
            run.Passed = t.Passed; run.Failed = t.Failed; run.Skipped = t.Skipped;
            run.FailedTests = [.. t.FailedTests ?? []];
        }
        Bound(s.TestRuns, WorldState.MaxTestRuns, x => x.StartedAt);
        changed.Add(new(EntityKinds.Test, run.Id));
    }

    private static void OnTerminal(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        if (e.Payload<TerminalData>() is not { } t) return;
        if (!s.Terminals.TryGetValue(t.TerminalId, out var term))
            s.Terminals[t.TerminalId] = term = new Terminal { Id = t.TerminalId, StartedAt = e.Ts };
        term.AgentId ??= e.AgentId;
        term.Pid ??= t.Pid;
        term.Command ??= t.Command;
        term.Cwd ??= t.Cwd;
        term.Alive = e.Type == EventTypes.TerminalStarted;
        if (e.Type == EventTypes.TerminalExited) term.ExitCode = t.ExitCode;
        changed.Add(new(EntityKinds.Terminal, term.Id));
    }

    // ---- helpers ------------------------------------------------------------------

    private static Evidence Stronger(Evidence a, Evidence b) => (Evidence)Math.Min((int)a, (int)b);

    private static void Bound<T>(Dictionary<string, T> d, int max, Func<T, DateTimeOffset> order)
    {
        if (d.Count <= max + max / 10) return; // amortized: trim in batches, not on every insert
        foreach (var k in d.OrderBy(kv => order(kv.Value)).Take(d.Count - max).Select(kv => kv.Key).ToList())
            d.Remove(k);
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
