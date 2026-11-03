using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Tests.E2E;
using Amc.Tests.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Amc.Tests.Claude;

/// <summary>The real daemon: a held PreToolUse hook, the approval endpoints and their control-action gating.</summary>
[Collection(DaemonCollection.Name)]
public sealed class ApprovalApiTests : IDisposable
{
    private const string RemoteHost = "phone.tail1234.ts.net";
    private readonly TempDir _data = new(), _claude = new();
    private static readonly string[] Vars = ["AMC_DataDir", "AMC_ClaudeHome", "AMC_WorkspaceScanDepth", "AMC_Approvals__Mode", "AMC_Approvals__AllowRemote", "AMC_AllowedHosts"];

    public void Dispose()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
        _data.Dispose();
        _claude.Dispose();
    }

    private (WebApplicationFactory<Program> Factory, HttpClient Http) Start(bool allowRemote = true)
    {
        Environment.SetEnvironmentVariable("AMC_DataDir", _data.Path);
        Environment.SetEnvironmentVariable("AMC_ClaudeHome", _claude.Path);
        Environment.SetEnvironmentVariable("AMC_WorkspaceScanDepth", "0");
        Environment.SetEnvironmentVariable("AMC_Approvals__Mode", "risky");
        Environment.SetEnvironmentVariable("AMC_Approvals__AllowRemote", allowRemote ? "true" : "false");
        Environment.SetEnvironmentVariable("AMC_AllowedHosts", RemoteHost);
        var factory = new WebApplicationFactory<Program>();
        _ = factory.Server;
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Amc-Token", File.ReadAllText(Path.Combine(_data.Path, "token")).Trim());
        return (factory, http);
    }

    private static StringContent Hook(string command, string session = "5e55a0aa-0000-4000-8000-00000000a001") => new(new JsonObject
    {
        ["session_id"] = session, ["cwd"] = "/work/repo", ["hook_event_name"] = "PreToolUse", ["tool_name"] = "Bash",
        ["tool_use_id"] = "toolu_" + Guid.NewGuid().ToString("N")[..10], ["tool_input"] = new JsonObject { ["command"] = command },
    }.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<string> PendingId(HttpClient http)
    {
        for (var i = 0; i < 500; i++)
        {
            var items = JsonNode.Parse(await http.GetStringAsync("/api/approvals"))!["items"]!.AsArray();
            if (items.Count > 0) return (string)items[0]!["id"]!;
            await Task.Delay(20);
        }
        throw new TimeoutException("no pending approval");
    }

    private static HttpRequestMessage Decide(string id, string decision, string? host = null, string? origin = null, bool token = true, HttpClient? http = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, $"/api/approvals/{id}/{decision}")
        {
            Content = new StringContent("""{"reason":"not now","client":"phone"}""", Encoding.UTF8, "application/json"),
        };
        if (host is not null) m.Headers.Host = host;
        if (origin is not null) m.Headers.Add("Origin", origin);
        if (!token) m.Headers.Add("X-Amc-Token", "wrong");
        return m;
    }

    [Fact]
    public async Task Held_hook_is_answered_from_a_remote_client_and_decisions_are_gated_like_control_actions()
    {
        var (factory, http) = Start();
        await using var _ = factory;

        // The plain hook route never holds, even for a risky call.
        var sw = Stopwatch.StartNew();
        var plain = await http.PostAsync("/hooks/claude", Hook("rm -rf ~/"));
        Assert.Equal("", await plain.Content.ReadAsStringAsync());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Empty(JsonNode.Parse(await http.GetStringAsync("/api/approvals"))!["items"]!.AsArray());

        var hook = http.PostAsync("/hooks/claude?approval=1", Hook("rm -rf ~/"));
        var id = await PendingId(http);
        var listed = false;
        for (var i = 0; i < 250 && !listed; i++)
        {
            listed = JsonNode.Parse(await http.GetStringAsync("/api/attention"))!["items"]!.AsArray()
                .Any(x => (string?)x!["kind"] == "approval" && (string?)x["priority"] == "urgent" && (string?)x["approval"]!["id"] == id);
            if (!listed) await Task.Delay(20);
        }
        Assert.True(listed, "the held call is an urgent item in the Needs-you queue");

        using (var anon = factory.CreateClient())
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Decide(id, "allow"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Decide(id, "allow", origin: "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Decide(id, "allow", host: RemoteHost, origin: "https://other.tail1234.ts.net"))).StatusCode);
        Assert.Equal((HttpStatusCode)421, (await http.SendAsync(Decide(id, "allow", host: "evil.example"))).StatusCode);
        Assert.False(hook.IsCompleted);

        // tailscale serve: the allowed name with its own origin, token included.
        var deny = await http.SendAsync(Decide(id, "deny", host: RemoteHost, origin: "https://" + RemoteHost));
        Assert.Equal(HttpStatusCode.OK, deny.StatusCode);
        var output = JsonNode.Parse(await (await hook.WaitAsync(TimeSpan.FromSeconds(10))).Content.ReadAsStringAsync())!;
        Assert.Equal("deny", (string?)output["hookSpecificOutput"]!["permissionDecision"]);
        Assert.Equal("Denied by the user in Overhive: not now", (string?)output["hookSpecificOutput"]!["permissionDecisionReason"]);

        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Decide(id, "allow"))).StatusCode);   // single use
        JsonArray resolved = [];
        for (var i = 0; i < 250 && resolved.Count == 0; i++)
        {
            resolved = JsonNode.Parse(await http.GetStringAsync("/api/events?type=approval.resolved"))!.AsArray();
            if (resolved.Count == 0) await Task.Delay(20);
        }
        var data = resolved.Single()!["data"]!;
        Assert.Equal(("deny", "remote", "phone"), ((string?)data["decision"], (string?)data["by"], (string?)data["client"]));
        Assert.Empty(JsonNode.Parse(await http.GetStringAsync("/api/approvals"))!["items"]!.AsArray());
    }

    [Fact]
    public async Task AllowRemote_false_restricts_decisions_to_loopback_clients()
    {
        var (factory, http) = Start(allowRemote: false);
        await using var _ = factory;
        var hook = http.PostAsync("/hooks/claude?approval=1", Hook("git push --force origin main"));
        var id = await PendingId(http);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Decide(id, "allow", host: RemoteHost))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Decide(id, "allow"))).StatusCode);
        var output = JsonNode.Parse(await (await hook.WaitAsync(TimeSpan.FromSeconds(10))).Content.ReadAsStringAsync())!;
        Assert.Equal("allow", (string?)output["hookSpecificOutput"]!["permissionDecision"]);
    }
}
