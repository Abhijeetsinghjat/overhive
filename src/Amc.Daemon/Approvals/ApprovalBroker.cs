using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amc.Core.Approvals;
using Amc.Core.Events;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Providers.Claude;
using Amc.Daemon.Risk;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Approvals;

/// <summary>
/// Holds Claude Code PreToolUse calls that need the user's approval (config "Approvals", docs/AGENT_INTEGRATION.md):
/// the hook request stays open (long poll, like the rewake waiter) until the dashboard, the phone page or the CLI
/// allows or denies it, or until <see cref="ApprovalSettings.TimeoutSeconds"/>. Allow / deny answer the hook with
/// Claude Code's <c>permissionDecision</c>; a timeout, the hook going away, the daemon stopping or any error answer
/// nothing, so Claude Code's normal permission flow applies. Nothing is ever allowed without a decision (or a
/// session rule the user created with "Allow similar"). Ids are 128-bit random and single-use.
/// </summary>
public sealed class ApprovalBroker
{
    public const string HealthName = "approvals";
    /// <summary>Most calls held at once; beyond it new calls are not held (normal permission flow).</summary>
    public const int MaxPending = 100;
    public const int MaxRulesPerSession = 50;
    public const int MaxSessionsWithRules = 1000;
    public const int MaxReason = 500;

    /// <summary>One PreToolUse call that needs a decision (from <see cref="Prepare"/>).</summary>
    public sealed record Ticket(ApprovalNeed Need, string AgentId, string SessionId, string? WorktreeId, string? RepositoryId,
        string? Cwd, string? ToolUseId, bool AutoAllowed);

    /// <summary>Outcome of <see cref="ResolveAsync"/>: HTTP status and a short text.</summary>
    public sealed record ResolveResult(bool Ok, int StatusCode, string Message, long? LatencyMs = null);

    private sealed record Outcome(string Decision, string? Reason);

    private sealed class Waiter(string id, Ticket ticket, DateTimeOffset at)
    {
        public string Id { get; } = id;
        public Ticket Ticket { get; } = ticket;
        public DateTimeOffset RequestedAt { get; } = at;
        public TaskCompletionSource<Outcome> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, Waiter> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _rules = new(StringComparer.Ordinal);
    private readonly StateStore _state;
    private readonly IEventSink _sink;
    private readonly HealthRegistry _health;
    private readonly TimeProvider _time;
    private readonly ILogger<ApprovalBroker> _log;
    private readonly RiskEnvironment _env;
    private readonly ApprovalSettings _settings;

    public ApprovalBroker(StateStore state, IEventSink sink, HealthRegistry health, IOptions<AmcOptions> options, TimeProvider time,
        ILogger<ApprovalBroker> log, IHostApplicationLifetime? lifetime = null, RiskEnvironment? env = null)
    {
        _state = state; _sink = sink; _health = health; _time = time; _log = log;
        _settings = options.Value.Approvals;
        _env = env ?? RiskEnvironment.Current();
        _health.Register(HealthName);
        // Open long polls must not hold up shutdown: release them with no decision (normal permission flow).
        lifetime?.ApplicationStopping.Register(() => ReleaseAll("Overhive stopped before a decision"));
        UpdateHealth();
    }

    public ApprovalSettings Settings => _settings;
    /// <summary>Tests: a hold shorter than the 10 s minimum <see cref="ApprovalSettings.TimeoutSeconds"/> allows.</summary>
    internal TimeSpan? HoldOverride { get; set; }
    public int PendingCount => _pending.Count;
    public IReadOnlyCollection<string> PendingIds => [.. _pending.Keys];
    public int RuleCount(string sessionId) { lock (_rules) return _rules.TryGetValue(sessionId, out var r) ? r.Count : 0; }

    /// <summary>
    /// Fast, synchronous part (before the payload is compacted for mapping): decides whether this hook call waits.
    /// Null for every hook that does not (approvals off, not the approval route, not PreToolUse, nothing risky): those
    /// answer exactly as before. <paramref name="holdable"/> is true only for the hook installed for approvals (?approval=1).
    /// </summary>
    public Ticket? Prepare(JsonObject payload, bool holdable = true)
    {
        var evt = Str(payload, "hook_event_name");
        var sid = Str(payload, "session_id");
        if (sid is null) return null;
        if (evt == "SessionEnd" && payload["agent_id"] is null) { lock (_rules) _rules.TryRemove(sid, out _); return null; }
        if (!holdable || !_settings.Enabled || evt != "PreToolUse" || Str(payload, "tool_name") is not { Length: > 0 } tool) return null;

        var agentId = ClaudeHookReceiver.AgentFor(sid, payload);
        var cwd = Str(payload, "cwd");
        var (ctx, wt) = _state.Read(s => RiskRecorder.ContextFor(s, agentId, cwd, _env));
        var need = ApprovalPolicy.Evaluate(_settings, tool, payload["tool_input"] as JsonObject, ctx);
        if (need is null) return null;
        bool auto;
        lock (_rules) auto = _rules.TryGetValue(sid, out var keys) && keys.Contains(need.SimilarKey);
        return new Ticket(need, agentId, sid, wt?.Id, wt?.RepositoryId, cwd, Str(payload, "tool_use_id"), auto);
    }

    /// <summary>
    /// Holds the hook until a decision. Returns the hook output for allow / deny, or null (no output: Claude Code's
    /// normal permission flow) on timeout, cancellation (hook gone, daemon stopping) and errors.
    /// </summary>
    public async Task<JsonObject?> WaitAsync(Ticket t, CancellationToken ct)
    {
        try
        {
            if (t.AutoAllowed)
            {
                await _sink.PublishAsync(Resolved(t, "auto-" + NewId()[..12], ApprovalDecisions.Allow, ApprovalActors.SessionRule,
                    "Matched an \"Allow similar\" rule for this session: " + t.Need.SimilarLabel, 0, null, includeRequest: true), ct);
                _health.Increment(HealthName, "auto_allowed");
                return ApprovalHookOutput.Allow("Allowed by an Overhive session rule (Allow similar)");
            }
            if (_pending.Count >= MaxPending)
            {
                _health.Increment(HealthName, "over_capacity");
                _health.Degraded(HealthName, $"more than {MaxPending} calls waiting; new ones use the normal permission flow");
                return null;
            }
            return await HoldAsync(t, ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Approval wait failed; falling back to the normal permission flow");
            _health.Increment(HealthName, "errors");
            return null;
        }
    }

    private async Task<JsonObject?> HoldAsync(Ticket t, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var timeout = HoldOverride ?? TimeSpan.FromSeconds(_settings.EffectiveTimeoutSeconds);
        var w = new Waiter(NewId(), t, now);
        _pending[w.Id] = w;
        UpdateHealth();
        try
        {
            var n = t.Need;
            await _sink.PublishAsync(Event(t, EventTypes.ApprovalRequested, now, new ApprovalRequestedData(w.Id, n.Tool, t.ToolUseId, n.Reason,
                n.Command, n.FilePath, t.Cwd, n.Preview, n.Severity,
                [.. n.Findings.Select(f => new ApprovalFinding(f.RuleId, f.Severity, f.Explanation, f.Span, f.Target))],
                n.SimilarLabel, now + timeout)), ct);
            _health.Increment(HealthName, "requested");

            using var hold = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var done = await Task.WhenAny(w.Done.Task, Task.Delay(timeout, _time, hold.Token));
            await hold.CancelAsync();
            if (done != w.Done.Task && _pending.TryRemove(w.Id, out _))
            {
                await Expire(w, $"No decision within {_settings.EffectiveTimeoutSeconds} s; Claude Code's normal permission flow applies");
                return null;
            }
            var outcome = await w.Done.Task.WaitAsync(ct);
            return outcome.Decision switch
            {
                ApprovalDecisions.Allow => ApprovalHookOutput.Allow(),
                ApprovalDecisions.Deny => ApprovalHookOutput.Deny(outcome.Reason),
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            // Claude Code cancelled the hook, the transport timed out, or the daemon is stopping.
            if (_pending.TryRemove(w.Id, out _)) await Expire(w, "The hook went away before a decision (cancelled or timed out)");
            return null;
        }
        finally
        {
            _pending.TryRemove(w.Id, out _); // errors: never leave a waiter behind
            UpdateHealth();
        }
    }

    /// <summary>
    /// Allow or deny a held call. Single-use: the first decision wins, later ones (and unknown or expired ids) get 404.
    /// <paramref name="allowSimilar"/> (allow only) also allows later calls of this session with the same similar-key.
    /// </summary>
    public async Task<ResolveResult> ResolveAsync(string id, string decision, string? reason, bool allowSimilar, string by, string? client,
        CancellationToken ct = default)
    {
        if (decision is not (ApprovalDecisions.Allow or ApprovalDecisions.Deny))
            return new(false, 400, "decision must be allow or deny");
        if (!IsWellFormed(id) || !_pending.TryRemove(id, out var w))
            return new(false, 404, "No such pending approval (already decided, expired, or the agent moved on)");
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim() is var r && r.Length > MaxReason ? r[..MaxReason] : reason.Trim();
        var similar = allowSimilar && decision == ApprovalDecisions.Allow;
        if (similar) AddRule(w.Ticket.SessionId, w.Ticket.Need.SimilarKey);
        var latency = (long)Math.Max(0, (_time.GetUtcNow() - w.RequestedAt).TotalMilliseconds);
        w.Done.TrySetResult(new Outcome(decision, reason));
        UpdateHealth();
        _health.Increment(HealthName, decision == ApprovalDecisions.Allow ? "allowed" : "denied");
        await _sink.PublishAsync(Resolved(w.Ticket, w.Id, decision, by, reason, latency, client, similar: similar), ct);
        return new(true, 200, decision == ApprovalDecisions.Allow ? "Allowed" + (similar ? " (and similar calls in this session)" : "") : "Denied", latency);
    }

    /// <summary>Releases every held call with no decision (shutdown). Each is recorded as expired.</summary>
    public void ReleaseAll(string reason)
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var w))
            {
                w.Done.TrySetResult(new Outcome(ApprovalDecisions.Expired, reason));
                _sink.TryPublish(Resolved(w.Ticket, w.Id, ApprovalDecisions.Expired, ApprovalActors.System, reason, Latency(w), null));
            }
        UpdateHealth();
    }

    private async Task Expire(Waiter w, string reason)
    {
        w.Done.TrySetResult(new Outcome(ApprovalDecisions.Expired, reason));
        _health.Increment(HealthName, "expired");
        // The hook may be gone (ct cancelled): record with a fresh token so the expiry is never lost.
        await _sink.PublishAsync(Resolved(w.Ticket, w.Id, ApprovalDecisions.Expired, ApprovalActors.System, reason, Latency(w), null), CancellationToken.None);
    }

    private long Latency(Waiter w) => (long)Math.Max(0, (_time.GetUtcNow() - w.RequestedAt).TotalMilliseconds);

    private void AddRule(string sessionId, string key)
    {
        lock (_rules)
        {
            if (!_rules.TryGetValue(sessionId, out var set))
            {
                if (_rules.Count >= MaxSessionsWithRules) return;
                _rules[sessionId] = set = new HashSet<string>(StringComparer.Ordinal);
            }
            if (set.Count < MaxRulesPerSession) set.Add(key);
        }
    }

    private AgentEvent Resolved(Ticket t, string id, string decision, string by, string? reason, long latency, string? client,
        bool similar = false, bool includeRequest = false) =>
        Event(t, EventTypes.ApprovalResolved, _time.GetUtcNow(), new ApprovalResolvedData(id, decision, by, reason, latency, client, similar,
            includeRequest ? t.Need.Tool : null, includeRequest ? t.Need.Command : null, includeRequest ? t.Need.FilePath : null));

    private static AgentEvent Event<T>(Ticket t, string type, DateTimeOffset at, T data) where T : class => new()
    {
        Type = type, Source = EventSources.Hook, Evidence = Evidence.Reported, Ts = at,
        AgentId = t.AgentId, SessionId = t.SessionId, WorktreeId = t.WorktreeId, RepositoryId = t.RepositoryId,
        Data = Json.ToObject(data),
    };

    /// <summary>128 random bits, lower-case hex.</summary>
    public static string NewId() => RandomNumberGenerator.GetHexString(32, lowercase: true);

    public static bool IsWellFormed(string? id) =>
        id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void UpdateHealth()
    {
        if (!_settings.Enabled) { _health.Disabled(HealthName, "Approvals.Mode is off (tool calls never wait)"); return; }
        var n = _pending.Count;
        _health.SetCounter(HealthName, "pending", n);
        _health.Ok(HealthName, $"mode {_settings.EffectiveMode}; {n} call(s) waiting for a decision");
    }

    private static string? Str(JsonNode? n, string key) => ClaudeEventMapper.Str(n, key);
}

/// <summary>On startup, records approvals left pending by a previous run as expired (their hooks have long failed open).</summary>
public sealed class ApprovalRestorer(StateStore state, IEventSink sink) : IHostedService
{
    public const string Reason = "Overhive restarted before a decision; Claude Code's normal permission flow applied";

    public async Task StartAsync(CancellationToken ct)
    {
        var stale = state.Read(s => s.Approvals.Values.Select(a => (a.Id, a.AgentId, a.SessionId, a.WorktreeId)).ToList());
        foreach (var (id, agent, session, wt) in stale)
            await sink.PublishAsync(new AgentEvent
            {
                Type = EventTypes.ApprovalResolved, Source = EventSources.System, Evidence = Evidence.Observed,
                AgentId = agent, SessionId = session, WorktreeId = wt,
                Data = Json.ToObject(new ApprovalResolvedData(id, ApprovalDecisions.Expired, ApprovalActors.System, Reason)),
            }, ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
