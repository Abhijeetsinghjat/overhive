using Amc.Core.Domain;
using Amc.Core.Security;

namespace Amc.Core.Conflicts;

/// <summary>One agent (or the agents of one worktree) as named in a warning.</summary>
/// <param name="Inferred">True when the agent's link to the file is only inferred (said so in the text).</param>
public sealed record WarnParty(string AgentId, string Name, string Tool, string WorktreeId, string? Branch, bool Inferred = false);

/// <summary>What one recipient is told about one other worktree. Overlapping null = not analysed (no region claim).</summary>
public sealed record WarnCounterpart(IReadOnlyList<WarnParty> Agents, string? Branch, IReadOnlyList<LineSpan> TheirLines,
    IReadOnlyList<LineSpan> YourLines, bool? Overlapping);

/// <summary>Warning texts and the automatic-warning dedupe rules (pure).</summary>
public static class ConflictWarnings
{
    public const int MaxNameChars = 60;
    /// <summary>An automatic warning is never repeated to the same agent for the same conflict within this time.</summary>
    public static readonly TimeSpan MinAutoInterval = TimeSpan.FromMinutes(10);
    /// <summary>Overlap regions that moved by at most this many lines count as unchanged (no new automatic warning).</summary>
    public const int RangeSlack = 10;

    /// <summary>
    /// Short, factual heads-up for <paramref name="recipient"/>. Only states what git showed (line ranges)
    /// and which agent the other change is attributed to.
    /// </summary>
    public static string Message(string path, WarnParty recipient, IReadOnlyList<WarnCounterpart> others)
    {
        var parts = new List<string>();
        foreach (var o in others)
        {
            var who = o.Agents.Count == 0
                ? $"another worktree (branch {o.Branch ?? "detached"})"
                : string.Join(" and ", o.Agents.Select(a => $"{Short(a.Name)} ({a.Tool}, branch {o.Branch ?? "detached"})"))
                  + (o.Agents.All(a => a.Inferred) ? " [attribution inferred]" : "");
            var theirs = o.TheirLines.Count > 0 ? " " + OverlapMath.FormatLines(o.TheirLines) : "";
            var yours = o.YourLines.Count > 0 ? "your " + OverlapMath.FormatLines(o.YourLines) : "your changes";
            parts.Add(o.Overlapping switch
            {
                true => $"{who} is also editing {path}{theirs}, overlapping {yours}.",
                false => $"{who} is also editing {path}{theirs}; {yours} {(o.YourLines is [{ } one] && one.Start == one.End ? "is" : "are")} in a different region, so a merge will likely be clean.",
                null => $"{who} is also editing {path}{theirs} in another worktree.",
            });
        }
        var advice = others.Any(o => o.Overlapping == true) ? " Coordinate before changing those lines."
            : others.All(o => o.Overlapping == false) ? " Coordinate before widening your edits."
            : " Coordinate before changing it further.";
        var observed = others.Any(o => o.TheirLines.Count > 0 || o.YourLines.Count > 0) ? " (Line ranges observed in git.)" : "";
        var text = "Heads-up from Overhive: " + string.Join(" ", parts) + advice + observed;
        return Redactor.Redact(text);
    }

    /// <summary>
    /// Whether an automatic warning should go to <paramref name="agentId"/> now (manual warnings count too).
    /// Never within <see cref="MinAutoInterval"/> of the last attempt; after a queued or delivered warning,
    /// only when an overlap region is not within <see cref="RangeSlack"/> lines of what the agent was told.
    /// </summary>
    public static bool ShouldAutoWarn(IReadOnlyList<ConflictWarning> history, string agentId, IReadOnlyList<LineSpan> currentBase,
        DateTimeOffset now, out string reason)
    {
        var attempts = history.OrderByDescending(w => w.At)
            .SelectMany(w => w.Recipients.Where(r => r.AgentId == agentId).Select(r => (w.At, r))).ToList();
        // Any attempt (also a failed one) holds off the next automatic one, so a broken transport is not retried in a loop.
        if (attempts.Count > 0 && now - attempts[0].At < MinAutoInterval) { reason = "warned recently"; return false; }
        var last = attempts.FirstOrDefault(x => x.r.Outcome is "delivered" or "queued");
        if (last.r is null) { reason = "not warned yet"; return true; }
        if (last.r.BaseSpans is { } prev && OverlapMath.Covered(prev, currentBase, RangeSlack)) { reason = "ranges unchanged"; return false; }
        reason = "overlapping ranges changed";
        return true;
    }

    private static string Short(string s) => s.Length <= MaxNameChars ? s : s[..(MaxNameChars - 3)] + "...";
}
