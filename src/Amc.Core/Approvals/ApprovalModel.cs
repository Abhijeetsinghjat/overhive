using System.Text;
using System.Text.Json.Nodes;
using Amc.Core.Events;
using Amc.Core.Risk;
using Amc.Core.State;

namespace Amc.Core.Approvals;

/// <summary>Values of <see cref="ApprovalSettings.Mode"/>.</summary>
public static class ApprovalModes
{
    /// <summary>Default: no tool call waits; the PreToolUse hook is installed exactly as before.</summary>
    public const string Off = "off";
    /// <summary>Tool calls the risk classifier rates at or above <see cref="ApprovalSettings.MinSeverity"/> wait for a decision.</summary>
    public const string Risky = "risky";
    /// <summary>Like <see cref="Risky"/>, and every Write / Edit / MultiEdit / NotebookEdit waits too.</summary>
    public const string AllEdits = "all-edits";

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Risky => Risky,
        AllEdits or "alledits" or "all_edits" or "edits" => AllEdits,
        _ => Off,
    };
}

/// <summary>
/// Configuration section "Approvals": approve or deny Claude Code tool calls from the dashboard (docs/AGENT_INTEGRATION.md).
/// Read by the daemon (which holds the hook) and by agentctl (which writes the hook with a matching timeout).
/// </summary>
public sealed class ApprovalSettings
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MinTimeoutSeconds = 10;
    public const int MaxTimeoutSeconds = 3000;
    /// <summary>Seconds the hook's transport (curl -m / hook-forward) waits beyond the daemon's hold, so the daemon always answers first.</summary>
    public const int TransportMarginSeconds = 15;
    /// <summary>Seconds Claude Code's hook timeout exceeds the transport's, so the transport always fails open before Claude Code cancels the hook.</summary>
    public const int HookMarginSeconds = 15;

    /// <summary>"off" (default), "risky" or "all-edits".</summary>
    public string Mode { get; set; } = ApprovalModes.Off;
    /// <summary>Lowest risk severity that waits for approval in "risky" mode: "warn" (default) or "critical" ("info" is treated as warn).</summary>
    public string MinSeverity { get; set; } = RiskSeverity.Warn;
    /// <summary>How long a tool call waits for a decision before Claude Code's normal permission flow takes over.</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
    /// <summary>Whether a client reaching the daemon through an AllowedHosts name (tailscale serve, docs/REMOTE_ACCESS.md) may decide.</summary>
    public bool AllowRemote { get; set; } = true;

    public string EffectiveMode => ApprovalModes.Normalize(Mode);
    public bool Enabled => EffectiveMode != ApprovalModes.Off;
    public int EffectiveTimeoutSeconds => Math.Clamp(TimeoutSeconds <= 0 ? DefaultTimeoutSeconds : TimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);
    /// <summary>Rank of the lowest severity that waits (never info: info findings are noise by design).</summary>
    public int MinRank => Math.Max(1, RiskSeverity.Rank(RiskSeverity.Normalize(MinSeverity)));
    /// <summary>curl -m / hook-forward --max-time for the approval variant of the PreToolUse hook.</summary>
    public int TransportSeconds => EffectiveTimeoutSeconds + TransportMarginSeconds;
    /// <summary>Claude Code's "timeout" of the approval variant of the PreToolUse hook.</summary>
    public int HookTimeoutSeconds => TransportSeconds + HookMarginSeconds;
}

/// <summary>Values of <see cref="ApprovalResolvedData.Decision"/>.</summary>
public static class ApprovalDecisions
{
    public const string Allow = "allow";
    public const string Deny = "deny";
    /// <summary>The user chose to answer in Claude Code's own permission prompt (hook answers "ask" at once).</summary>
    public const string Ask = "ask";
    /// <summary>No decision in time, the hook went away, or the daemon stopped: Claude Code's normal permission flow applies.</summary>
    public const string Expired = "expired";
}

/// <summary>Who decided (<see cref="ApprovalResolvedData.By"/>).</summary>
public static class ApprovalActors
{
    /// <summary>A loopback client: the dashboard, the phone page through an SSH tunnel, or the CLI.</summary>
    public const string Local = "local";
    /// <summary>A client that reached the daemon through an AllowedHosts name (e.g. tailscale serve).</summary>
    public const string Remote = "remote";
    /// <summary>An "allow this exact command / edits to this file for this session" rule matched.</summary>
    public const string SessionRule = "session-rule";
    /// <summary>Timeout, hook gone or daemon stopping.</summary>
    public const string System = "system";
}

/// <summary>One risk finding as shown on an approval card.</summary>
public sealed record ApprovalFinding(string RuleId, string Severity, string Explanation, string Span, string? Target = null);

/// <summary>Payload of <c>approval.requested</c>: a tool call held until the user decides.</summary>
public sealed record ApprovalRequestedData(
    string ApprovalId,
    string Tool,
    string? ToolUseId,
    /// <summary>"risk" (the classifier flagged it) or "edit" (all-edits mode).</summary>
    string Reason,
    string? Command = null,
    string? FilePath = null,
    string? Cwd = null,
    /// <summary>Diff-style preview of an edit (old/new strings truncated) or the start of written content.</summary>
    string? Preview = null,
    string? Severity = null,
    IReadOnlyList<ApprovalFinding>? Findings = null,
    string? SimilarLabel = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Payload of <c>approval.resolved</c>.</summary>
public sealed record ApprovalResolvedData(
    string ApprovalId,
    string Decision,
    string By,
    string? Reason = null,
    long? LatencyMs = null,
    /// <summary>"dashboard", "phone", "cli" (as the client said; display only).</summary>
    string? Client = null,
    bool AllowSimilar = false,
    /// <summary>Set when a session rule decided without a prior request event.</summary>
    string? Tool = null,
    string? Command = null,
    string? FilePath = null,
    string? ToolUseId = null,
    IReadOnlyList<ApprovalFinding>? Findings = null);

/// <summary>A pending approval in the state (removed when resolved; the events stay in the log).</summary>
public sealed class PendingApproval
{
    public required string Id { get; init; }
    public string? AgentId { get; set; }
    public string? SessionId { get; set; }
    public string? WorktreeId { get; set; }
    public required string Tool { get; set; }
    public string? ToolUseId { get; set; }
    public required string Reason { get; set; }
    public string? Command { get; set; }
    public string? FilePath { get; set; }
    public string? Cwd { get; set; }
    public string? Preview { get; set; }
    public string? Severity { get; set; }
    public List<ApprovalFinding> Findings { get; set; } = [];
    /// <summary>What "allow for this session" covers (the exact command in this directory, or this file); null when not offered (critical findings).</summary>
    public string? SimilarLabel { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// A call the user decided (allow / deny / ask, also by a session rule), kept briefly so the risk finding of that
/// same call does not raise a second "Needs you" item (<see cref="ApprovalCoverage"/>). Expired calls are not kept:
/// their finding falls back to the normal risk item.
/// </summary>
public sealed class ApprovalOutcome
{
    public required string Id { get; init; }
    public string? AgentId { get; init; }
    public string? ToolUseId { get; init; }
    public required string Decision { get; init; }
    public List<ApprovalFinding> Findings { get; init; } = [];
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
}

/// <summary>Whether a risk finding is the same call as a pending or decided approval (one card, not two).</summary>
public static class ApprovalCoverage
{
    /// <summary>Process scans and transcripts may record the finding a little before the request or after the decision.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(10);

    public static bool Covers(WorldState s, Risk.Risk r)
    {
        if (r.AgentId is null || (s.Approvals.Count == 0 && s.ApprovalOutcomes.Count == 0)) return false;
        foreach (var a in s.Approvals.Values)
            if (Same(r, a.AgentId, a.ToolUseId, a.Findings, a.RequestedAt, null)) return true;
        foreach (var o in s.ApprovalOutcomes.Values)
            if (Same(r, o.AgentId, o.ToolUseId, o.Findings, o.RequestedAt, o.DecidedAt)) return true;
        return false;
    }

    private static bool Same(Risk.Risk r, string? agentId, string? toolUseId, List<ApprovalFinding> findings, DateTimeOffset from, DateTimeOffset? to)
    {
        if (r.AgentId != agentId) return false;
        if (toolUseId is not null && r.ToolUseId == toolUseId) return true;
        if (r.At < from - Slack || (to is { } t && r.At > t + Slack)) return false;
        return findings.Any(f => f.RuleId == r.RuleId && (f.Span == r.Span || (f.Target is not null && f.Target == r.Target)));
    }
}

internal static class ApprovalReducer
{
    public static void Apply(WorldState s, AgentEvent e, HashSet<EntityRef> changed)
    {
        switch (e.Type)
        {
            case EventTypes.ApprovalRequested when e.Payload<ApprovalRequestedData>() is { } d && !s.Approvals.ContainsKey(d.ApprovalId):
                if (s.Approvals.Count >= WorldState.MaxApprovals) return;
                s.Approvals[d.ApprovalId] = new PendingApproval
                {
                    Id = d.ApprovalId, AgentId = e.AgentId, SessionId = e.SessionId, WorktreeId = e.WorktreeId,
                    Tool = d.Tool, ToolUseId = d.ToolUseId, Reason = d.Reason, Command = d.Command, FilePath = d.FilePath ?? e.FilePath,
                    Cwd = d.Cwd, Preview = d.Preview, Severity = d.Severity, Findings = [.. d.Findings ?? []],
                    SimilarLabel = d.SimilarLabel, RequestedAt = e.Ts, ExpiresAt = d.ExpiresAt,
                };
                changed.Add(new(EntityKinds.Approval, d.ApprovalId));
                break;
            case EventTypes.ApprovalResolved when e.Payload<ApprovalResolvedData>() is { } r:
            {
                s.Approvals.Remove(r.ApprovalId, out var pending);
                if (pending is not null) changed.Add(new(EntityKinds.Approval, r.ApprovalId));
                if (r.Decision is ApprovalDecisions.Allow or ApprovalDecisions.Deny or ApprovalDecisions.Ask)
                {
                    s.ApprovalOutcomes[r.ApprovalId] = new ApprovalOutcome
                    {
                        Id = r.ApprovalId, AgentId = pending?.AgentId ?? e.AgentId, ToolUseId = pending?.ToolUseId ?? r.ToolUseId,
                        Decision = r.Decision, Findings = pending?.Findings ?? [.. r.Findings ?? []],
                        RequestedAt = pending?.RequestedAt ?? e.Ts, DecidedAt = e.Ts,
                    };
                    // Only recent outcomes matter (ApprovalCoverage.Slack): keep the newest few hundred.
                    if (s.ApprovalOutcomes.Count > WorldState.MaxApprovalOutcomes)
                        foreach (var old in s.ApprovalOutcomes.Values.OrderBy(o => o.DecidedAt).Take(s.ApprovalOutcomes.Count - WorldState.MaxApprovalOutcomes).ToList())
                            s.ApprovalOutcomes.Remove(old.Id);
                }
                break;
            }
        }
    }
}

/// <summary>What a held tool call is about (the pure part of the decision whether to hold it).</summary>
public sealed record ApprovalNeed(
    string Reason,
    string Tool,
    string? Command,
    string? FilePath,
    string? Preview,
    string? Severity,
    IReadOnlyList<RiskFinding> Findings,
    /// <summary>
    /// "Allow for this session" key: the exact (whitespace-normalized) command in the same working directory, or edits
    /// to the same file. Null when not offered: a critical finding is decided one call at a time.
    /// </summary>
    string? SimilarKey,
    string? SimilarLabel);

/// <summary>
/// Pure decision whether a Claude Code PreToolUse call waits for approval, and what the card shows. Uses the
/// risk classifier (<see cref="RiskClassifier"/>): commands are classified as a whole; file tools by their path.
/// </summary>
public static class ApprovalPolicy
{
    public static readonly IReadOnlySet<string> EditTools = new HashSet<string> { "Write", "Edit", "MultiEdit", "NotebookEdit" };
    public const int MaxPreview = 6000;
    public const int MaxEditString = 1500;

    public static ApprovalNeed? Evaluate(ApprovalSettings settings, string tool, JsonObject? input, RiskContext ctx)
    {
        if (!settings.Enabled || string.IsNullOrEmpty(tool)) return null;
        input ??= new JsonObject();
        var allEdits = settings.EffectiveMode == ApprovalModes.AllEdits;
        string? command = null, path = null;
        IReadOnlyList<RiskFinding> findings;
        if (tool == "Bash")
        {
            command = Str(input, "command");
            if (string.IsNullOrWhiteSpace(command)) return null;
            findings = RiskClassifier.ClassifyCommand(command, ctx);
        }
        else
        {
            path = Absolute(Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path"), ctx);
            var kind = EditTools.Contains(tool) ? "modified" : tool == "Read" ? "read" : "accessed";
            findings = path is null ? [] : RiskClassifier.ClassifyFileEvent(kind, path, ctx);
        }
        var relevant = findings.Where(f => RiskSeverity.Rank(f.Severity) >= settings.MinRank).ToList();
        var isEdit = EditTools.Contains(tool);
        if (relevant.Count == 0 && !(allEdits && isEdit)) return null;

        var severity = relevant.Count == 0 ? null : relevant.Select(f => f.Severity).Aggregate(RiskSeverity.Max);
        string? key = null, label = null;
        if (severity != RiskSeverity.Critical)
        {
            if (command is not null)
            {
                key = $"cmd|{ctx.Cwd}|{NormalizeCommand(command)}";
                label = $"this exact command in {ctx.Cwd ?? "the same directory"}";
            }
            else if (path is not null)
            {
                key = $"file|{(isEdit ? "edit" : tool)}|{path}";
                label = isEdit ? $"edits to {path}" : $"{tool} of {path}";
            }
        }
        return new ApprovalNeed(relevant.Count > 0 ? "risk" : "edit", tool, command, path, Preview(tool, input),
            severity, relevant, key, label);
    }

    /// <summary>The command with runs of spaces and tabs collapsed and the ends trimmed (line breaks kept: they separate commands).</summary>
    public static string NormalizeCommand(string command) =>
        string.Join('\n', command.Replace("\r\n", "\n").Split('\n')
            .Select(l => string.Join(' ', l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)))).Trim();

    /// <summary>Diff-style preview of what a file tool writes; null for other tools.</summary>
    public static string? Preview(string tool, JsonObject input)
    {
        var sb = new StringBuilder();
        switch (tool)
        {
            case "Edit":
                AppendEdit(sb, Str(input, "old_string"), Str(input, "new_string"));
                break;
            case "MultiEdit" when input["edits"] is JsonArray edits:
                var n = 0;
                foreach (var e in edits.OfType<JsonObject>())
                {
                    if (n++ > 0) sb.Append("@@\n");
                    if (sb.Length > MaxPreview) { sb.Append($"… {edits.Count - n + 1} more edit(s)\n"); break; }
                    AppendEdit(sb, Str(e, "old_string"), Str(e, "new_string"));
                }
                break;
            case "Write":
                AppendLines(sb, '+', Str(input, "content"));
                break;
            case "NotebookEdit":
                AppendLines(sb, '+', Str(input, "new_source"));
                break;
            default:
                return null;
        }
        var text = sb.ToString();
        if (text.Length == 0) return null;
        return text.Length <= MaxPreview ? text : text[..MaxPreview] + $"\n… [{text.Length - MaxPreview} more chars]";
    }

    private static void AppendEdit(StringBuilder sb, string? oldText, string? newText)
    {
        AppendLines(sb, '-', oldText);
        AppendLines(sb, '+', newText);
    }

    private static void AppendLines(StringBuilder sb, char sign, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var cut = text.Length > MaxEditString;
        if (cut) text = text[..MaxEditString];
        foreach (var line in text.Split('\n')) sb.Append(sign).Append(' ').Append(line.TrimEnd('\r')).Append('\n');
        if (cut) sb.Append($"{sign} … [truncated]\n");
    }

    private static string? Absolute(string? p, RiskContext ctx)
    {
        if (string.IsNullOrWhiteSpace(p) || p.Contains('\0')) return null;
        p = p.Trim();
        if ((p == "~" || p.StartsWith("~/", StringComparison.Ordinal)) && ctx.HomeDir is { } home) p = home.TrimEnd('/', '\\') + "/" + p[1..].TrimStart('/');
        if (RiskPaths.IsAbsolute(p, ctx.Windows)) return RiskPaths.Normalize(p, ctx.Windows);
        return ctx.Cwd is { } cwd ? RiskPaths.Normalize(cwd.TrimEnd('/', '\\') + "/" + p, ctx.Windows) : null;
    }

    private static string? Str(JsonNode? n, string key) =>
        n is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>The exact PreToolUse hook output Claude Code reads (hookSpecificOutput.permissionDecision).</summary>
public static class ApprovalHookOutput
{
    public static JsonObject Allow(string reason = "Approved by the user in Overhive") => Build(ApprovalDecisions.Allow, reason);

    /// <summary>Hand the call to Claude Code's own permission prompt now (the reason is shown in that prompt).</summary>
    public static JsonObject Ask() => Build(ApprovalDecisions.Ask, "Overhive: the user chose to answer in the terminal");

    public static JsonObject Deny(string? userReason) =>
        Build(ApprovalDecisions.Deny, "Denied by the user in Overhive" + (string.IsNullOrWhiteSpace(userReason) ? "." : ": " + userReason.Trim()));

    private static JsonObject Build(string decision, string reason) => new()
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = decision,
            ["permissionDecisionReason"] = reason,
        },
    };
}
