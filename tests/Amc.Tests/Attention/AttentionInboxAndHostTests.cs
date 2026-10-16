using Amc.Core.Attention;
using Amc.Daemon.Api;
using Amc.Daemon.Attention;
using Amc.Daemon.Infrastructure;
using Amc.Tests.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Amc.Tests.Attention.NotificationTests;

namespace Amc.Tests.Attention;

public class AttentionInboxTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Dismiss_hides_until_the_item_is_gone_snooze_until_it_expires()
    {
        var inbox = new AttentionInbox();
        inbox.Dismiss("a", T0);
        inbox.Snooze("b", TimeSpan.FromMinutes(30), T0);

        var (visible, hidden) = inbox.Apply([Item("a"), Item("b"), Item("c")], T0.AddMinutes(1));
        Assert.Equal(["c"], visible.Select(i => i.Id));
        Assert.Equal(true, hidden.Single(i => i.Id == "a").Dismissed);
        Assert.Equal(T0.AddMinutes(30), hidden.Single(i => i.Id == "b").SnoozedUntil);

        (visible, _) = inbox.Apply([Item("a"), Item("b"), Item("c")], T0.AddMinutes(31));
        Assert.Equal(["b", "c"], visible.Select(i => i.Id));

        // Once the dismissed occurrence is resolved, its mark is pruned: the same id would show again.
        inbox.Apply([Item("c")], T0.AddMinutes(32));
        (visible, _) = inbox.Apply([Item("a")], T0.AddMinutes(33));
        Assert.Equal(["a"], visible.Select(i => i.Id));
    }

    [Fact]
    public void Marks_survive_a_restart_and_restore_undoes_them()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "attention-marks.json");
        new AttentionInbox(path).Dismiss("a", T0);

        var reloaded = new AttentionInbox(path);
        Assert.Empty(reloaded.Apply([Item("a")], T0).Visible);
        reloaded.Restore("a");
        Assert.Single(new AttentionInbox(path).Apply([Item("a")], T0).Visible);
    }

    [Fact]
    public void Snooze_is_bounded()
    {
        var inbox = new AttentionInbox();
        inbox.Snooze("a", TimeSpan.FromDays(365), T0);
        Assert.Equal(T0 + AttentionInbox.MaxSnooze, inbox.Apply([Item("a")], T0).Hidden.Single().SnoozedUntil);
    }
}

public class AllowedHostsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value))).Build();

    [Fact]
    public void Empty_by_default_and_reads_arrays_or_semicolon_lists()
    {
        Assert.Empty(HostAllowList.Resolve(Config()));
        Assert.Equal(["box.tail1234.ts.net", "100.64.0.7"],
            HostAllowList.Resolve(Config(("AllowedHosts:0", "Box.Tail1234.ts.net"), ("AllowedHosts:1", "100.64.0.7"))));
        Assert.Equal(["a.ts.net", "b.ts.net"], HostAllowList.Resolve(Config(("AllowedHosts", "a.ts.net; b.ts.net;"))));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.ts.net")]
    [InlineData("box.ts.net:443")]
    [InlineData("https://box.ts.net")]
    [InlineData("bad host")]
    public void Wildcards_and_non_host_entries_refuse_to_start(string entry)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HostAllowList.Resolve(Config(("AllowedHosts:0", entry))));
        Assert.Contains("AllowedHosts", ex.Message);
    }

    private static async Task<int> Request(IEnumerable<string> allowed, string host, string? origin = null, string path = "/")
    {
        var options = Options.Create(new AmcOptions { AllowedHosts = [.. allowed] });
        var mw = new SecurityMiddleware(_ => Task.CompletedTask, new ApiToken("tok"), NullLogger<SecurityMiddleware>.Instance, options);
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString(host);
        ctx.Request.Path = path;
        ctx.Connection.LocalPort = 47821;
        ctx.Response.Body = new MemoryStream();
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        await mw.InvokeAsync(ctx);
        return ctx.Response.StatusCode;
    }

    [Fact]
    public async Task Only_loopback_hosts_pass_without_an_allow_list()
    {
        Assert.Equal(200, await Request([], "127.0.0.1:47821"));
        Assert.Equal(200, await Request([], "localhost:47821", "http://localhost:47821"));
        Assert.Equal(421, await Request([], "box.tail1234.ts.net"));
        Assert.Equal(403, await Request([], "127.0.0.1:47821", "http://127.0.0.1:9999"));
    }

    [Fact]
    public async Task Allowed_host_passes_with_its_own_origin_only()
    {
        string[] allowed = ["box.tail1234.ts.net"];
        Assert.Equal(200, await Request(allowed, "box.tail1234.ts.net"));
        Assert.Equal(200, await Request(allowed, "BOX.tail1234.ts.net", "https://box.tail1234.ts.net"));
        Assert.Equal(421, await Request(allowed, "evil.example"));
        Assert.Equal(421, await Request(allowed, "x.box.tail1234.ts.net"));
        Assert.Equal(403, await Request(allowed, "box.tail1234.ts.net", "https://evil.example"));
        // An allowed origin may only call the host it was loaded from (no cross-host use of the allow-list).
        Assert.Equal(403, await Request(allowed, "127.0.0.1:47821", "https://box.tail1234.ts.net"));
        // The token is still required for the API.
        Assert.Equal(401, await Request(allowed, "box.tail1234.ts.net", "https://box.tail1234.ts.net", "/api/attention"));
    }
}
