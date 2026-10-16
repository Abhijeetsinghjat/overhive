using Amc.Core.Conflicts;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Tools;

namespace Amc.Daemon.Conflicts;

public sealed record WarnRecipientPreview(
    string AgentId, string Name, string Tool, string WorktreeId, string? Branch,
    bool CanReceive, string? Transport, string? Reason, string Text, List<LineSpan> BaseSpans);

public sealed record WarnPreview(string ConflictKey, string? Path, OverlapVerdict Verdict, List<WarnRecipientPreview> Recipients, string? Error = null);

/// <summary>Per agent: delivered, queued, unsupported or failed.</summary>
public sealed record WarnOutcome(string AgentId, string Name, string Outcome, string? MessageId, string? Transport, string Detail);

public sealed record WarnResult(bool Ok, string ConflictKey, List<WarnOutcome> Agents, string Detail, int StatusCode = 200);

/// <summary>
/// "Warn agents": tells every active agent attributed to a cross-worktree conflict's file which other
/// agent/branch changes the same file and which lines overlap, through the agents' existing message
/// transports (<see cref="AgentOperations.SendMessageAsync"/>: pty, hook mailbox, protocol inbox).
/// Every warning is recorded as a <see cref="EventTypes.ConflictWarned"/> event plus an
/// <see cref="EventTypes.AuditControl"/> audit entry. Manual warnings require confirm=true.
/// </summary>
public sealed class ConflictWarner(StateStore state, AgentOperations ops, IEventSink sink, ToolRegistry tools, TimeProvider clock)
{
    public const string Author = "overhive";
    private readonly SemaphoreSlim _sending = new(1, 1);

    private sealed record Party(string AgentId, string Name, string Tool, bool Active, bool Inferred);
    private sealed record SideInfo(string WorktreeId, string? Branch, List<Party> Agents);

    /// <summary>Who would be warned and the exact text each would get. Null if the conflict does not exist.</summary>
    public WarnPreview? Preview(string key)
    {
        var data = state.Read(s => Collect(s, key));
        if (data is null) return null;
        var (path, kind, overlap, sides) = data.Value;
        var verdict = overlap?.Verdict ?? OverlapVerdict.Unknown;
        if (kind != ConflictKind.CrossWorktree || path is null)
            return new WarnPreview(key, path, verdict, [], "Only cross-worktree conflicts (same file in several worktrees) can be warned about");

        var recipients = new List<WarnRecipientPreview>();
        foreach (var side in sides)
            foreach (var p in side.Agents.Where(p => p.Active))
            {
                var counterparts = new List<WarnCounterpart>();
                var baseSpans = new List<LineSpan>();
                foreach (var other in sides.Where(o => o.WorktreeId != side.WorktreeId))
                {
                    var pair = overlap?.Pairs.FirstOrDefault(x =>
                        (x.WorktreeA == side.WorktreeId && x.WorktreeB == other.WorktreeId) || (x.WorktreeA == other.WorktreeId && x.WorktreeB == side.WorktreeId));
                    var mineIsA = pair?.WorktreeA == side.WorktreeId;
                    bool? overlapping = pair is null || pair.Note is not null && !pair.Overlapping ? null : pair.Overlapping;
                    var yours = pair is null ? [] : overlapping == true ? (mineIsA ? pair.OverlapA : pair.OverlapB) : (mineIsA ? pair.ChangedA : pair.ChangedB);
                    var theirs = pair is null ? [] : overlapping == true ? (mineIsA ? pair.OverlapB : pair.OverlapA) : (mineIsA ? pair.ChangedB : pair.ChangedA);
                    if (pair is { Overlapping: true }) baseSpans.AddRange(pair.OverlapBase);
                    counterparts.Add(new WarnCounterpart(
                        other.Agents.Select(a => new WarnParty(a.AgentId, a.Name, a.Tool, other.WorktreeId, other.Branch, a.Inferred)).ToList(),
                        other.Branch, theirs, yours, overlapping));
                }
                var text = ConflictWarnings.Message(path, new WarnParty(p.AgentId, p.Name, p.Tool, side.WorktreeId, side.Branch), counterparts);
                var caps = ops.Capabilities(p.AgentId);
                var transport = caps?.MessageTransports.FirstOrDefault();
                recipients.Add(new WarnRecipientPreview(p.AgentId, p.Name, p.Tool, side.WorktreeId, side.Branch, transport is not null, transport,
                    transport is null ? "This agent cannot receive messages: no supported transport (start it from Overhive or connect its hooks)" : null,
                    text, OverlapMath.Merge(baseSpans)));
            }
        return new WarnPreview(key, path, verdict, recipients);
    }

    /// <summary>Manual "Warn agents" action (dashboard / CLI).</summary>
    public async Task<WarnResult> WarnAsync(string key, bool confirmed, CancellationToken ct)
    {
        var preview = Preview(key);
        if (preview is null) return new(false, key, [], "Unknown conflict (it may have been resolved)", 404);
        if (preview.Error is not null) return new(false, key, [], preview.Error, 409);
        if (preview.Recipients.Count == 0) return new(false, key, [], "No active agent is attributed to this file", 409);
        if (!confirmed) return new(false, key, [], "Warning agents sends them messages and requires confirm=true", 428);
        return await SendAsync(preview, preview.Recipients, auto: false, ct);
    }

    /// <summary>
    /// Automatic warning (AmcOptions.AutoWarnConflicts): only for overlapping changes, only agents that can
    /// receive messages, deduped per conflict and agent by <see cref="ConflictWarnings.ShouldAutoWarn"/>.
    /// Returns null when nobody needed a warning.
    /// </summary>
    public async Task<WarnResult?> AutoWarnAsync(string key, CancellationToken ct)
    {
        var preview = Preview(key);
        if (preview is null || preview.Error is not null || preview.Verdict != OverlapVerdict.Overlapping) return null;
        var history = state.Read(s => s.ConflictInsights.TryGetValue(key, out var i) ? i.Warnings.ToList() : []);
        var now = clock.GetUtcNow();
        var due = preview.Recipients
            .Where(r => r.CanReceive && r.BaseSpans.Count > 0 && ConflictWarnings.ShouldAutoWarn(history, r.AgentId, r.BaseSpans, now, out _))
            .ToList();
        return due.Count == 0 ? null : await SendAsync(preview, due, auto: true, ct);
    }

    private async Task<WarnResult> SendAsync(WarnPreview preview, List<WarnRecipientPreview> recipients, bool auto, CancellationToken ct)
    {
        await _sending.WaitAsync(ct);
        try
        {
            var outcomes = new List<WarnOutcome>();
            var records = new List<ConflictWarningRecipient>();
            foreach (var r in recipients)
            {
                WarnOutcome o;
                if (!r.CanReceive) o = new WarnOutcome(r.AgentId, r.Name, "unsupported", null, null, r.Reason ?? "no message transport");
                else
                {
                    var sent = await ops.SendMessageAsync(r.AgentId, r.Text, ct, Author);
                    o = new WarnOutcome(r.AgentId, r.Name, !sent.Ok ? "failed" : sent.Delivered ? "delivered" : "queued",
                        sent.MessageId is { Length: > 0 } id ? id : null, sent.Transport, sent.Detail);
                }
                outcomes.Add(o);
                records.Add(new ConflictWarningRecipient(o.AgentId, o.Outcome, o.MessageId, o.Transport, o.Detail, r.BaseSpans));
            }

            await sink.PublishAndWaitAsync(new AgentEvent
            {
                Type = EventTypes.ConflictWarned, Source = auto ? EventSources.System : EventSources.User, Evidence = Evidence.Observed,
                FilePath = preview.Path,
                Data = Json.ToObject(new ConflictWarnedData(preview.ConflictKey, auto, records)),
            }, ct);
            var sentCount = outcomes.Count(o => o.Outcome is "delivered" or "queued");
            var outcome = sentCount == outcomes.Count ? "ok" : sentCount > 0 ? "partial" : "failed";
            var detail = string.Join("; ", outcomes.Select(o => $"{o.Name}: {o.Outcome}"));
            await sink.PublishAsync(new AgentEvent
            {
                Type = EventTypes.AuditControl, Source = auto ? EventSources.System : EventSources.User, Evidence = Evidence.Observed,
                FilePath = preview.Path,
                Data = Json.ToObject(new AuditData(auto ? "auto-warn-conflict" : "warn-conflict", preview.Path ?? preview.ConflictKey, outcome, detail)),
            }, ct);
            return new WarnResult(sentCount > 0, preview.ConflictKey, outcomes,
                $"{sentCount} of {outcomes.Count} agent(s) warned", sentCount > 0 ? 200 : 409);
        }
        finally { _sending.Release(); }
    }

    /// <summary>Copies what the warning needs out of state (read lock held by the caller).</summary>
    private (string? Path, ConflictKind Kind, ConflictOverlap? Overlap, List<SideInfo> Sides)? Collect(WorldState s, string key)
    {
        if (!s.Conflicts.TryGetValue(key, out var c)) return null;
        var sides = new List<SideInfo>();
        if (c.Kind == ConflictKind.CrossWorktree && c.Path is not null)
            foreach (var wtId in c.WorktreeIds)
            {
                if (!s.Worktrees.TryGetValue(wtId, out var w) || w.Removed) continue;
                if (!s.Files.TryGetValue(Ids.FileKey(wtId, c.Path), out var f)) continue;
                var agents = f.Agents.OrderBy(kv => kv.Key).Select(kv =>
                {
                    var a = s.Agents.GetValueOrDefault(kv.Key);
                    return new Party(kv.Key, a is null ? kv.Key : a.Title ?? a.AgentType ?? ToolName(a.Provider), a is null ? "?" : ToolName(a.Provider),
                        a is { IsTerminal: false }, kv.Value.Evidence == Evidence.Inferred);
                }).ToList();
                sides.Add(new SideInfo(wtId, w.Detached ? null : w.Branch, agents));
            }
        var overlap = c.Insight?.Overlap is { } o ? Json.Deserialize<ConflictOverlap>(Json.Serialize(o)) : null;
        return (c.Path, c.Kind, overlap, sides);
    }

    private string ToolName(string provider) => tools.Get(provider)?.Name ?? provider;
}
