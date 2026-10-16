using System.Net;
using Amc.Core.Attention;
using Amc.Core.Events;
using Amc.Daemon.Attention;
using Amc.Tests.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amc.Tests.Attention;

public class NotificationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    internal static AttentionItem Item(string id, AttentionPriority p = AttentionPriority.Urgent, string title = "Needs your approval: fix tests", string? tool = "codex") => new()
    {
        Id = id, Kind = AttentionKinds.AgentBlocked, Priority = p, Title = title, Tool = tool, Since = T0,
        Detail = "Waiting for permission: rm -rf build", Evidence = Evidence.Reported, AgentIds = ["codex:" + id],
        Action = new AttentionAction(AttentionActions.OpenAgent, "Open", "codex:" + id),
    };

    // ---- throttle --------------------------------------------------------------------------------

    [Fact]
    public void Throttle_coalesces_a_burst_into_one_batch()
    {
        var t = new NotificationThrottle(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), 20);
        t.Offer([Item("a")], T0);
        t.Offer([Item("b")], T0.AddSeconds(2));
        Assert.Null(t.TakeDue(T0.AddSeconds(4)));
        var batch = t.TakeDue(T0.AddSeconds(5));
        Assert.Equal(["a", "b"], batch!.Select(i => i.Id));
        Assert.Null(t.TakeDue(T0.AddSeconds(6)));
    }

    [Fact]
    public void Throttle_keeps_a_minimum_interval_and_coalesces_what_arrives_meanwhile()
    {
        var t = new NotificationThrottle(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), 20);
        t.Offer([Item("a")], T0);
        Assert.NotNull(t.TakeDue(T0.AddSeconds(5)));
        t.Offer([Item("b")], T0.AddSeconds(10));
        t.Offer([Item("c")], T0.AddSeconds(30));
        Assert.Null(t.TakeDue(T0.AddSeconds(64)));
        Assert.Equal(T0.AddSeconds(65), t.NextDue(T0.AddSeconds(64)));
        Assert.Equal(["b", "c"], t.TakeDue(T0.AddSeconds(65))!.Select(i => i.Id));
    }

    [Fact]
    public void Throttle_caps_sends_per_hour()
    {
        var t = new NotificationThrottle(TimeSpan.Zero, TimeSpan.Zero, 2);
        var now = T0;
        for (var i = 0; i < 2; i++)
        {
            t.Offer([Item("x" + i)], now);
            Assert.NotNull(t.TakeDue(now));
            now = now.AddMinutes(1);
        }
        t.Offer([Item("x2")], now);
        Assert.Null(t.TakeDue(now));
        Assert.Null(t.TakeDue(T0.AddMinutes(59)));
        Assert.NotNull(t.TakeDue(T0.AddMinutes(60)));
    }

    [Fact]
    public void Throttle_drops_items_answered_before_they_were_sent()
    {
        var t = new NotificationThrottle(TimeSpan.FromSeconds(5), TimeSpan.Zero, 20);
        t.Offer([Item("a"), Item("a")], T0);
        Assert.Equal(1, t.Pending);
        t.Retain(new HashSet<string>());
        Assert.Null(t.NextDue(T0));
        Assert.Null(t.TakeDue(T0.AddMinutes(1)));
    }

    // ---- dispatcher ------------------------------------------------------------------------------

    internal sealed class FakeNotifier(string name = "fake", bool onlyWithoutDashboard = false) : INotifier
    {
        public List<NotificationMessage> Sent { get; } = [];
        public string Name => name;
        public bool Enabled => true;
        public bool OnlyWithoutDashboard => onlyWithoutDashboard;
        public Task<bool> SendAsync(NotificationMessage message, CancellationToken ct) { Sent.Add(message); return Task.FromResult(true); }
    }

    private static NotificationOptions Fast => new() { CoalesceSeconds = 0, MinIntervalSeconds = 0 };

    private static List<NotificationMessage> Run(NotificationDispatcher d, IReadOnlyList<AttentionItem> visible, DateTimeOffset now, int clients = 0)
    {
        var due = d.Observe(visible, now, clients);
        foreach (var (n, m) in due) n.SendAsync(m, default).GetAwaiter().GetResult();
        return [.. due.Select(x => x.Message)];
    }

    [Fact]
    public void Dispatcher_ignores_what_is_there_at_startup_and_notifies_each_new_urgent_item_once()
    {
        var fake = new FakeNotifier();
        var d = new NotificationDispatcher([fake], Fast, t => t == "codex" ? "Codex CLI" : t ?? "agent");

        Assert.Empty(Run(d, [Item("old")], T0));
        var sent = Run(d, [Item("old"), Item("new")], T0.AddSeconds(1));
        var m = Assert.Single(sent);
        Assert.Equal(["new"], m.ItemIds);
        Assert.Equal("Overhive: an agent needs you", m.Title);
        Assert.Equal("Codex CLI · Needs your approval: fix tests", m.Body);
        Assert.DoesNotContain("rm -rf", m.Body); // details never go into notifications

        Assert.Empty(Run(d, [Item("old"), Item("new")], T0.AddSeconds(2))); // dedupe by stable id
        Assert.Single(fake.Sent);
    }

    [Fact]
    public void Dispatcher_respects_min_priority()
    {
        var fake = new FakeNotifier();
        var d = new NotificationDispatcher([fake], Fast, t => t ?? "");
        Run(d, [], T0);
        Assert.Empty(Run(d, [Item("w", AttentionPriority.Normal)], T0.AddSeconds(1)));

        var withWaiting = new NotificationDispatcher([fake], new NotificationOptions { CoalesceSeconds = 0, MinIntervalSeconds = 0, MinPriority = "normal" }, t => t ?? "");
        Run(withWaiting, [], T0);
        Assert.Single(Run(withWaiting, [Item("w", AttentionPriority.Normal), Item("l", AttentionPriority.Low)], T0.AddSeconds(1)));
        Assert.Equal(["w"], fake.Sent.Single().ItemIds);
    }

    [Fact]
    public void Desktop_channel_stays_quiet_while_a_dashboard_is_open_but_ntfy_does_not()
    {
        var desktop = new FakeNotifier("desktop", onlyWithoutDashboard: true);
        var phone = new FakeNotifier("ntfy");
        var d = new NotificationDispatcher([desktop, phone], Fast, t => t ?? "");
        Run(d, [], T0, clients: 1);
        Run(d, [Item("a")], T0.AddSeconds(1), clients: 1);
        Assert.Empty(desktop.Sent);
        Assert.Single(phone.Sent);

        Run(d, [Item("a"), Item("b")], T0.AddSeconds(2), clients: 0);
        Assert.Equal(["b"], desktop.Sent.Single().ItemIds);
    }

    [Fact]
    public void Snoozed_item_notifies_again_when_it_comes_back()
    {
        var fake = new FakeNotifier();
        var d = new NotificationDispatcher([fake], Fast, t => t ?? "");
        Run(d, [], T0);
        Run(d, [Item("a")], T0.AddSeconds(1));
        Run(d, [], T0.AddSeconds(2));             // snoozed: hidden from the visible queue
        Run(d, [Item("a")], T0.AddMinutes(60));  // snooze over
        Assert.Equal(2, fake.Sent.Count);
    }

    [Fact]
    public void Batch_text_lists_titles_and_counts_the_rest()
    {
        var items = Enumerable.Range(0, 7).Select(i => Item("i" + i, title: "Needs your approval: task " + i, tool: null)).ToList();
        var m = NotificationFormatter.Format(items, t => t ?? "");
        Assert.Equal("Overhive: 7 items need you", m.Title);
        var lines = m.Body.Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.Equal("+2 more", lines[^1]);
    }

    // ---- desktop command lines ----------------------------------------------------------------------

    [Fact]
    public void Desktop_commands_use_argument_lists_and_escape_text()
    {
        var m = new NotificationMessage("Overhive: an agent needs you", "Codex · say \"hi\" \\ <b>x</b>\nnext", AttentionPriority.Urgent, ["a"]);

        var (file, args) = DesktopNotifier.Command(m, macOS: false);
        Assert.Equal("notify-send", file);
        Assert.Equal(["--app-name=Overhive", "--urgency=critical", "--", m.Title, "Codex · say \"hi\" \\ &lt;b&gt;x&lt;/b&gt;\nnext"], args);

        var (mac, macArgs) = DesktopNotifier.Command(m, macOS: true);
        Assert.Equal("osascript", mac);
        Assert.Equal("-e", macArgs[0]);
        Assert.Equal("display notification \"Codex · say \\\"hi\\\" \\\\ <b>x</b> next\" with title \"Overhive: an agent needs you\"", macArgs[1]);
        Assert.Equal(2, macArgs.Count);
    }

    // ---- ntfy -----------------------------------------------------------------------------------------

    private sealed class FakeHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status);
        }
    }

    [Fact]
    public async Task Ntfy_posts_a_short_redacted_message_with_the_token_from_the_file()
    {
        using var dir = new TempDir();
        var tokenFile = Path.Combine(dir.Path, "ntfy-token");
        File.WriteAllText(tokenFile, "tk_abc123\n");
        var handler = new FakeHandler();
        var ntfy = new NtfyNotifier(new NotificationOptions { NtfyUrl = "https://ntfy.example.org/overhive-me", NtfyTokenFile = tokenFile },
            NullLogger<NtfyNotifier>.Instance, TimeProvider.System, handler);
        Assert.True(ntfy.Enabled);
        Assert.False(ntfy.OnlyWithoutDashboard);

        var leaky = Item("a", title: "Needs your approval: push with ghp_0123456789abcdefghijklmnopqrstuvwxyzAB");
        var m = NotificationFormatter.Format([leaky], t => "Codex CLI");
        Assert.True(await ntfy.SendAsync(m, default));

        var (req, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://ntfy.example.org/overhive-me", req.RequestUri!.ToString());
        Assert.Equal("Overhive", req.Headers.GetValues("Title").Single());
        Assert.Equal("high", req.Headers.GetValues("Priority").Single());
        Assert.Equal("Bearer tk_abc123", req.Headers.Authorization!.ToString());
        Assert.StartsWith("Overhive: an agent needs you\nCodex CLI · Needs your approval: push with", body);
        Assert.DoesNotContain("ghp_0123456789", body);
        Assert.DoesNotContain("rm -rf", body);
    }

    [Fact]
    public async Task Ntfy_does_not_retry_and_pauses_after_repeated_failures()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        var ntfy = new NtfyNotifier(new NotificationOptions { NtfyUrl = "https://ntfy.example.org/t" }, NullLogger<NtfyNotifier>.Instance, TimeProvider.System, handler);
        var m = NotificationFormatter.Format([Item("a")], t => "x");
        for (var i = 0; i < NtfyNotifier.FailuresBeforePause; i++) Assert.False(await ntfy.SendAsync(m, default));
        Assert.Equal(NtfyNotifier.FailuresBeforePause, handler.Requests.Count); // one request per send, no retries
        Assert.False(await ntfy.SendAsync(m, default));
        Assert.Equal(NtfyNotifier.FailuresBeforePause, handler.Requests.Count); // paused
        Assert.Null(handler.Requests[0].Request.Headers.Authorization);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("ftp://ntfy.example.org/t", false)]
    [InlineData("https://user:pass@ntfy.example.org/t", false)]
    [InlineData("https://ntfy.example.org/", false)]
    [InlineData("not a url", false)]
    [InlineData("https://ntfy.example.org/overhive", true)]
    [InlineData("http://127.0.0.1:8080/overhive", true)]
    public void Ntfy_url_validation(string url, bool ok)
    {
        Assert.Equal(ok, NtfyNotifier.TryParseUrl(url, out _, out _));
        var n = new NtfyNotifier(new NotificationOptions { NtfyUrl = url }, NullLogger<NtfyNotifier>.Instance, TimeProvider.System, new FakeHandler());
        Assert.Equal(ok, n.Enabled);
    }

    [Fact]
    public void Everything_is_off_by_default()
    {
        var o = new NotificationOptions();
        Assert.False(new DesktopNotifier(o, NullLogger<DesktopNotifier>.Instance).Enabled);
        Assert.False(new NtfyNotifier(o, NullLogger<NtfyNotifier>.Instance, TimeProvider.System, new FakeHandler()).Enabled);
        Assert.Equal(AttentionPriority.Urgent, o.EffectiveMinPriority);
        Assert.Empty(new NotificationDispatcher([new DesktopNotifier(o, NullLogger<DesktopNotifier>.Instance)], o, t => "").Channels);
    }
}
