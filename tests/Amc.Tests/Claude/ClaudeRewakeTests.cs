using System.Text.Json.Nodes;
using Amc.Core.Events;
using Amc.Core.Providers.Claude;
using Amc.Daemon.Messaging;
using Amc.Daemon.Providers.Claude;

namespace Amc.Tests.Claude;

public sealed class ClaudeRewakeTests : IDisposable
{
    private const string S = "5e55a0aa-0000-4000-8000-0000000000aa";
    private readonly ClaudeHarness _h = new();
    private static string Main => ClaudePaths.MainId(S);

    private ClaudeRewake New() => new(_h.Mailbox, _h.Sink, _h.Context, _h.Health, _h.Time);
    private static JsonObject Hook(string evt, string? type = null) => new()
    {
        ["hook_event_name"] = evt, ["session_id"] = S, ["notification_type"] = type,
    };
    private void Queue(string text) => _h.Mailbox.Enqueue(new PendingMessage("m-" + text, Main, text, DateTimeOffset.UtcNow));

    [Fact]
    public async Task Idle_session_is_woken_as_soon_as_a_message_is_queued()
    {
        var r = New();
        r.Observe("Stop", S, Hook("Stop"));
        var wait = r.WaitAsync(Hook("Stop"), CancellationToken.None);
        await Until(() => r.WaitingCount(Main) == 1);
        Assert.False(wait.IsCompleted);

        Queue("focus on tests");
        var text = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("focus on tests", text);
        var delivered = Assert.Single(_h.Sink.OfType(EventTypes.MessageDelivered));
        Assert.Contains("asyncRewake", delivered.Data["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Message_already_queued_when_the_session_goes_idle_is_delivered_immediately()
    {
        var r = New();
        Queue("hello");
        Assert.Contains("hello", await r.WaitAsync(Hook("Notification", "idle_prompt"), CancellationToken.None));
    }

    [Fact]
    public async Task Waiters_are_released_when_the_session_becomes_busy_and_busy_sessions_do_not_wait()
    {
        var r = New();
        r.Observe("Stop", S, Hook("Stop"));
        var wait = r.WaitAsync(Hook("Stop"), CancellationToken.None);
        await Until(() => r.WaitingCount(Main) == 1);
        r.Observe("UserPromptSubmit", S, Hook("UserPromptSubmit"));
        Assert.Null(await wait.WaitAsync(TimeSpan.FromSeconds(5)));

        Queue("later");   // busy: left for PostToolUse to deliver mid-turn
        Assert.Null(await r.WaitAsync(Hook("PreToolUse"), CancellationToken.None));
        Assert.True(_h.Mailbox.HasPending(Main));
    }

    [Fact]
    public async Task Sub_agent_hooks_never_wait()
    {
        var r = New();
        var p = Hook("SubagentStop"); p["agent_id"] = "a1";
        Assert.Null(await r.WaitAsync(p, CancellationToken.None));
    }

    [Fact]
    public void Install_adds_async_rewake_waiters_and_uninstall_removes_them()
    {
        var s = ClaudeHooksConfig.Install(new JsonObject(), ClaudeHooksConfig.BuildCommand("/x/amcd.sock"), ClaudeHooksConfig.BuildRewakeCommand("/x/amcd.sock"));
        Assert.True(ClaudeHooksConfig.IsInstalled(s));
        Assert.True(ClaudeHooksConfig.IsRewakeInstalled(s));
        var idle = s["hooks"]!["Notification"]!.AsArray().Single(g => (string?)g!["matcher"] == "idle_prompt")!["hooks"]![0]!;
        Assert.True(idle["async"]!.GetValue<bool>());
        Assert.True(idle["asyncRewake"]!.GetValue<bool>());
        Assert.False(ClaudeHooksConfig.IsRewakeInstalled(ClaudeHooksConfig.Uninstall(s)));
        Assert.Null(ClaudeHooksConfig.Uninstall(s)["hooks"]);
    }

    private static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    public void Dispose() => _h.Dispose();
}
