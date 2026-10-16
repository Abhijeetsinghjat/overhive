using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Tests.E2E;
using Amc.Tests.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Amc.Tests.Attention;

/// <summary>The real daemon: open-protocol reports → /api/attention, dismiss, WebSocket push, phone page.</summary>
[Collection(DaemonCollection.Name)]
public sealed class AttentionApiTests : IDisposable
{
    private readonly TempDir _data = new(), _claude = new();

    [Fact]
    public async Task Blocked_agent_shows_up_can_be_dismissed_and_is_pushed_live()
    {
        Environment.SetEnvironmentVariable("AMC_DataDir", _data.Path);
        Environment.SetEnvironmentVariable("AMC_ClaudeHome", _claude.Path);
        Environment.SetEnvironmentVariable("AMC_WorkspaceScanDepth", "0");
        await using var factory = new WebApplicationFactory<Program>();
        _ = factory.Server;
        var token = File.ReadAllText(Path.Combine(_data.Path, "token")).Trim();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Amc-Token", token);

        var ws = factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => r.Headers["X-Amc-Token"] = token;
        using var socket = await ws.ConnectAsync(new Uri(factory.Server.BaseAddress, "ws"), CancellationToken.None);

        await Report(http, "start", "s1", new JsonObject());
        await Report(http, "status", "s1", new JsonObject { ["status"] = "blocked", ["reason"] = "Waiting for permission: shell" });

        var snap = await Eventually(http, s => s["urgent"]!.GetValue<int>() == 1);
        var item = snap["items"]!.AsArray().Single()!;
        Assert.Equal("agent.blocked", (string?)item["kind"]);
        Assert.Equal("urgent", (string?)item["priority"]);
        Assert.Equal("codex", (string?)item["tool"]);
        Assert.Equal("reported", (string?)item["evidence"]);
        Assert.Equal("codex:s1", (string?)item["agentIds"]![0]);

        // Pushed to connected dashboards.
        Assert.True(await ReceivesAttention(socket, n => n["urgent"]!.GetValue<int>() == 1));

        var dismissed = await http.PostAsJsonAsync("/api/attention/dismiss", new { id = (string?)item["id"] });
        dismissed.EnsureSuccessStatusCode();
        var after = JsonNode.Parse(await dismissed.Content.ReadAsStringAsync())!;
        Assert.Empty(after["items"]!.AsArray());
        Assert.Equal(1, after["hidden"]!.GetValue<int>());
        var all = JsonNode.Parse(await http.GetStringAsync("/api/attention?all=true"))!;
        Assert.True(all["hiddenItems"]![0]!["dismissed"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/attention/dismiss", new { id = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/attention/snooze", new { id = (string?)item["id"], minutes = 0 })).StatusCode);

        // Phone page: token embedded, CSP present, same Host rules as the dashboard.
        using var anon = factory.CreateClient();
        var page = await anon.GetStringAsync("/m");
        Assert.Contains(token, page);
        Assert.Contains("Content-Security-Policy", page);
        Assert.DoesNotContain("__AMC_TOKEN__", page);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/attention")).StatusCode);
    }

    private static async Task Report(HttpClient http, string kind, string agent, JsonObject data)
    {
        var body = new JsonObject { ["tool"] = "codex", ["kind"] = kind, ["agent"] = new JsonObject { ["id"] = agent, ["title"] = "Fix tests" }, ["data"] = data };
        var r = await http.PostAsync("/api/v1/report", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        r.EnsureSuccessStatusCode();
    }

    private static async Task<JsonNode> Eventually(HttpClient http, Func<JsonNode, bool> cond)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        JsonNode? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = JsonNode.Parse(await http.GetStringAsync("/api/attention"))!;
            if (cond(last)) return last;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException("Timed out; last: " + last?.ToJsonString());
    }

    private static async Task<bool> ReceivesAttention(System.Net.WebSockets.WebSocket socket, Func<JsonNode, bool> cond)
    {
        var buf = new byte[1 << 20];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var r = await socket.ReceiveAsync(buf, cts.Token);
                var msg = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
                if ((string?)msg?["t"] == "attention" && cond(msg!)) return true;
            }
        }
        catch (OperationCanceledException) { return false; }
    }

    public void Dispose()
    {
        foreach (var k in new[] { "AMC_DataDir", "AMC_ClaudeHome", "AMC_WorkspaceScanDepth" })
            Environment.SetEnvironmentVariable(k, null);
        _data.Dispose(); _claude.Dispose();
    }
}
