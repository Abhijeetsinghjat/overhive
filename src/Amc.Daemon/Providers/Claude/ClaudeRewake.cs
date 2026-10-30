using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;

namespace Amc.Daemon.Providers.Claude;

/// <summary>
/// Immediate delivery to an IDLE session, using Claude Code's documented asyncRewake hooks:
/// when a session goes idle (Stop / idle_prompt notification / SessionStart) a background hook
/// long-polls POST /hooks/claude/wait. When a message is queued for that session the request
/// returns it at once; the hook prints it to stderr and exits 2, which wakes Claude with the
/// text as a system reminder. When the session becomes busy again, waiters are released
/// (empty response, exit 0) and normal mid-turn delivery (PostToolUse) takes over.
/// </summary>
public sealed class ClaudeRewake
{
    public const string HealthName = "claude-rewake";
    /// <summary>How long one waiter holds the request. Below the hook's own timeout (ClaudeHooksConfig.RewakeTimeoutSeconds).</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(3400);

    private sealed class Waiter
    {
        /// <summary>true = a message may be available; false = released (session busy / ended).</summary>
        public readonly TaskCompletionSource<bool> Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>main agent id → armed waiters. Guarded by <see cref="_gate"/>; a set is removed when its last waiter leaves.</summary>
    private readonly Dictionary<string, Dictionary<Guid, Waiter>> _waiters = new();
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, bool> _idle = new();
    private readonly Mailbox _mailbox;
    private readonly IEventSink _sink;
    private readonly ClaudeContext _ctx;
    private readonly HealthRegistry _health;
    private readonly TimeProvider _time;

    public ClaudeRewake(Mailbox mailbox, IEventSink sink, ClaudeContext ctx, HealthRegistry health, TimeProvider time,
        IHostApplicationLifetime? lifetime = null)
    {
        _mailbox = mailbox; _sink = sink; _ctx = ctx; _health = health; _time = time;
        _mailbox.Enqueued += agentId => Signal(agentId, true);
        // Open long-polls must not hold up shutdown (hooks just exit 0 and re-arm at the next idle).
        lifetime?.ApplicationStopping.Register(() => { foreach (var id in WaitingAgents()) Signal(id, false); });
        _health.Register(HealthName);
        _health.Ok(HealthName, "no idle sessions waiting");
    }

    public int WaitingCount(string agentId) { lock (_gate) return _waiters.TryGetValue(agentId, out var d) ? d.Count : 0; }

    /// <summary>Agents with at least one armed waiter (tests, health).</summary>
    public IReadOnlyList<string> WaitingAgents() { lock (_gate) return _waiters.Keys.ToList(); }

    /// <summary>Agents tracked as idle or busy (tests, health).</summary>
    public int TrackedSessions => _idle.Count;

    /// <summary>Forgets idle/busy flags of sessions that are gone (killed without SessionEnd). Returns how many.</summary>
    public int Prune(DateTimeOffset now)
    {
        var n = 0;
        foreach (var id in _idle.Keys)
            if (WaitingCount(id) == 0 && _ctx.IsGone(id, now) && _idle.TryRemove(id, out _)) n++;
        return n;
    }

    /// <summary>Called from every hook: tracks whether the main session is idle and releases waiters when it gets busy.</summary>
    public void Observe(string hookEvent, string sessionId, JsonObject payload)
    {
        if (payload["agent_id"] is not null) return; // sub-agent activity says nothing about the main prompt
        var main = ClaudePaths.MainId(sessionId);
        switch (hookEvent)
        {
            case "Stop":
            case "SessionStart":
                _idle[main] = true;
                break;
            case "Notification" when (string?)payload["notification_type"] == "idle_prompt":
                _idle[main] = true;
                break;
            case "UserPromptSubmit":
            case "PreToolUse":
                _idle[main] = false;
                Signal(main, false);
                break;
            case "SessionEnd":
                _idle.TryRemove(main, out _);
                Signal(main, false);
                break;
        }
    }

    /// <summary>Long-poll for the waiter hook. Returns the framed message text, or null (nothing to deliver; hook exits 0).</summary>
    public async Task<string?> WaitAsync(JsonObject payload, CancellationToken ct)
    {
        if ((string?)payload["session_id"] is not { Length: > 0 } sid || payload["agent_id"] is not null) return null;
        var main = ClaudePaths.MainId(sid);
        // The waiter hook is armed by an idle-making event; record it in case this request outran that hook.
        var evt = (string?)payload["hook_event_name"];
        if (evt is "Stop" or "SessionStart" || (evt == "Notification" && (string?)payload["notification_type"] == "idle_prompt"))
            _idle.TryAdd(main, true);

        var deadline = _time.GetUtcNow() + MaxHold;
        while (!ct.IsCancellationRequested)
        {
            if (!_idle.GetValueOrDefault(main)) return null;                 // busy: PostToolUse delivers mid-turn
            if (await TryDeliverAsync(sid, main, ct) is { } text) return text;

            var w = new Waiter();
            var id = Guid.NewGuid();
            lock (_gate)
            {
                if (!_waiters.TryGetValue(main, out var set)) _waiters[main] = set = [];
                set[id] = w;
            }
            UpdateHealth();
            try
            {
                if (_mailbox.HasPending(main)) continue;                         // raced with an enqueue
                var remaining = deadline - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) return null;
                // The hold timer is cancelled as soon as the signal wins, so it does not live on for up to MaxHold.
                using var hold = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var done = await Task.WhenAny(w.Signal.Task, Task.Delay(remaining, _time, hold.Token));
                await hold.CancelAsync();
                ct.ThrowIfCancellationRequested();
                if (done != w.Signal.Task || !await w.Signal.Task) return null;  // timeout or released
            }
            catch (OperationCanceledException) { return null; }
            finally
            {
                lock (_gate)
                    if (_waiters.TryGetValue(main, out var set) && set.Remove(id) && set.Count == 0) _waiters.Remove(main);
                UpdateHealth();
            }
        }
        return null;
    }

    private async Task<string?> TryDeliverAsync(string sid, string main, CancellationToken ct)
    {
        if (!_mailbox.HasPending(main)) return null;
        var messages = _mailbox.DrainFor(main);
        if (messages.Count == 0) return null;
        var now = _time.GetUtcNow();
        var o = new Origin(EventSources.Hook, sid, main, now, null);
        foreach (var m in messages)
        {
            await _sink.PublishAsync(ClaudeEventMapper.New(o, EventTypes.MessageDelivered,
                new MessageStatusData(m.MessageId, Transports.HookMailbox, "Woke the idle session via asyncRewake hook"),
                $"msg:{m.MessageId}:delivered", evidence: Evidence.Observed), ct);
            _ctx.AddPendingReply(main, new PendingReply(m.MessageId, now));
        }
        _health.Increment(HealthName, "messages_delivered", messages.Count);
        return string.Join("\n\n", messages.Select(ClaudeHookReceiver.Frame));
    }

    private void Signal(string agentId, bool message)
    {
        List<Waiter> ws;
        lock (_gate)
        {
            if (!_waiters.TryGetValue(agentId, out var set)) return;
            ws = [.. set.Values];
        }
        foreach (var w in ws) w.Signal.TrySetResult(message);
    }

    private void UpdateHealth()
    {
        int n;
        lock (_gate) n = _waiters.Values.Sum(d => d.Count);
        _health.SetCounter(HealthName, "waiting", n);
        _health.Ok(HealthName, n == 0 ? "no idle sessions waiting" : $"{n} idle session waiter(s) armed");
    }
}
