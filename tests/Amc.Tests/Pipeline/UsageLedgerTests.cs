using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Core.Events;
using Amc.Daemon.Providers.Claude;
using Amc.Daemon.Storage;
using Amc.Daemon.Usage;
using Amc.Tests.E2E;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Amc.Tests.Pipeline;

public class UsageLedgerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static AgentEvent U(string agent, UsageData d, DateTimeOffset at) => new()
    {
        Type = EventTypes.AgentUsage, Source = EventSources.Transcript, AgentId = agent, Ts = at,
        DedupeKey = $"usage:{agent}:{d.MessageId}:{d.Input}:{d.Output}:{d.CacheWrite}:{d.CacheRead}",
        Data = Json.ToObject(d),
    };

    [Fact]
    public void One_row_per_response_with_the_maximum_of_streamed_records_and_the_first_record_time()
    {
        using var store = new EventStore(Path.Combine(_dir.Path, "amc.db"));
        store.Append([
            U("a1", new UsageData("m1", "claude-opus-5-5", 2, 10, 100, 1000), T0),
            U("a1", new UsageData("m1", "claude-opus-5-5", 2, 250, 100, 1000, CacheWrite1h: 100, Speed: "fast", Geo: "us"), T0.AddSeconds(3)),
            U("a2", new UsageData("m1", "claude-opus-5-5", 1, 1, 0, 0), T0.AddSeconds(4)),       // same id, other agent
            U("a1", new UsageData("m0", "claude-opus-5-5", 9, 9, 0, 0), T0.AddMinutes(-30)),     // started before the range
            U("a1", new UsageData("m9", "claude-opus-5-5", 9, 9, 0, 0), T0.AddHours(2)),         // after the range
        ]);
        var rows = store.UsageResponses(T0.AddMinutes(-1), T0.AddHours(1)).OrderBy(r => r.AgentId).ToList();
        Assert.Equal(2, rows.Count);
        var r = rows[0];
        Assert.Equal(("a1", 2L, 250L, 100L, 1000L), (r.AgentId, r.Input, r.Output, r.CacheWrite, r.CacheRead));
        Assert.Equal((100L, "fast", "us"), (r.CacheWrite1h!.Value, r.Speed, r.Geo));
        Assert.Equal(T0, r.At);
        Assert.Null(rows[1].CacheWrite1h);
        // A response whose first record is before `from` is not counted from a later record.
        store.Append([U("a1", new UsageData("m0", "claude-opus-5-5", 9, 99, 0, 0), T0.AddMinutes(1))]);
        Assert.DoesNotContain(store.UsageResponses(T0, T0.AddHours(1)), x => x.Output == 99);
    }

    [Theory]
    [InlineData("48h", 48 * 60)]
    [InlineData("30d", 30 * 24 * 60)]
    [InlineData("999d", 180 * 24 * 60)] // capped at retention
    [InlineData("all", 180 * 24 * 60)]
    [InlineData(null, 180 * 24 * 60)]
    public void Ranges_parse_and_are_capped_at_history_retention(string? range, double minutes) =>
        Assert.Equal(minutes, UsageService.ParseRange(range, 180).TotalMinutes);

    [Theory]
    [InlineData("30")]
    [InlineData("0d")]
    [InlineData("-3d")]
    [InlineData("2w")]
    public void Bad_ranges_are_rejected(string range) => Assert.Throws<ArgumentException>(() => UsageService.ParseRange(range, 180));

    [Fact]
    public void Transcript_usage_keeps_cache_lifetime_fast_mode_and_US_only_inference()
    {
        var o = new Origin(EventSources.Transcript, "s1", "a1", T0);
        JsonObject Msg(JsonObject usage) => new() { ["id"] = "msg_1", ["model"] = "claude-opus-5-5", ["usage"] = usage };
        var d = ClaudeTranscriptIngestorUsage(Msg(new JsonObject
        {
            ["input_tokens"] = 2, ["output_tokens"] = 30, ["cache_creation_input_tokens"] = 400, ["cache_read_input_tokens"] = 5000,
            ["cache_creation"] = new JsonObject { ["ephemeral_1h_input_tokens"] = 400, ["ephemeral_5m_input_tokens"] = 0 },
            ["speed"] = "fast", ["inference_geo"] = "us",
        }), o);
        Assert.Equal((400L, "fast", "us"), (d.CacheWrite1h!.Value, d.Speed, d.Geo));

        var plain = ClaudeTranscriptIngestorUsage(Msg(new JsonObject
        {
            ["input_tokens"] = 2, ["output_tokens"] = 30, ["cache_creation_input_tokens"] = 400,
            ["cache_creation"] = new JsonObject { ["ephemeral_1h_input_tokens"] = 0, ["ephemeral_5m_input_tokens"] = 400 },
            ["speed"] = "standard", ["inference_geo"] = "global",
        }), o);
        Assert.Equal((0L, null, null), (plain.CacheWrite1h!.Value, plain.Speed, plain.Geo)); // recorded zero, not unknown

        var old = ClaudeTranscriptIngestorUsage(Msg(new JsonObject { ["input_tokens"] = 2, ["output_tokens"] = 30 }), o);
        Assert.Null(old.CacheWrite1h);
        Assert.DoesNotContain("cacheWrite1h", Json.Serialize(old)); // absent from the stored payload
    }

    private static UsageData ClaudeTranscriptIngestorUsage(JsonObject msg, Origin o) =>
        // Round-trip through text: transcripts are parsed JSON (numbers read as any integer type), like production.
        TranscriptIngestor.UsageEvent(JsonNode.Parse(msg.ToJsonString())!.AsObject(), o)!.Payload<UsageData>()!;

    public void Dispose() => _dir.Dispose();
}

/// <summary>The real daemon: usage reported through the open protocol → /api/usage/report and /api/pricing.</summary>
[Collection(DaemonCollection.Name)]
public sealed class UsageApiTests : IDisposable
{
    private readonly TempDir _data = new(), _claude = new();

    [Fact]
    public async Task Report_prices_reported_usage_per_agent_and_lists_unpriced_models()
    {
        Environment.SetEnvironmentVariable("AMC_DataDir", _data.Path);
        Environment.SetEnvironmentVariable("AMC_ClaudeHome", _claude.Path);
        Environment.SetEnvironmentVariable("AMC_WorkspaceScanDepth", "0");
        Environment.SetEnvironmentVariable("AMC_Pricing__Models__gpt-5-codex__Input", "1.25");
        Environment.SetEnvironmentVariable("AMC_Pricing__Models__gpt-5-codex__Output", "10");
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            _ = factory.Server;
            var token = File.ReadAllText(Path.Combine(_data.Path, "token")).Trim();
            using var http = factory.CreateClient();
            http.DefaultRequestHeaders.Add("X-Amc-Token", token);

            await Report(http, "codex", "s1", "start", new JsonObject());
            await Report(http, "codex", "s1", "usage", new JsonObject { ["id"] = "r1", ["model"] = "gpt-5-codex", ["input"] = 1_000_000, ["output"] = 100_000 });
            await Report(http, "codex", "s1", "usage", new JsonObject
            {
                ["id"] = "r2", ["model"] = "claude-opus-5-5", ["input"] = 1_000_000, ["cacheWrite"] = 1_000_000, ["cacheWrite1h"] = 1_000_000,
                ["speed"] = "FAST", ["geo"] = "<script>",
            });
            await Report(http, "gemini", "g1", "start", new JsonObject());
            await Report(http, "gemini", "g1", "usage", new JsonObject { ["id"] = "r3", ["model"] = "gemini-2.5-pro", ["input"] = 10, ["output"] = 5 });

            // Reports are cached briefly: wait until the pipeline stored all usage, then ask once.
            await Eventually(http, "/api/agents?x=1", n => n.AsArray().Sum(a => a!["usage"]!["responses"]!.GetValue<int>()) == 3);
            var rep = JsonNode.Parse(await http.GetStringAsync("/api/usage/report?range=48h&tz=UTC"))!;
            Assert.Equal(3, rep["totals"]!["responses"]!.GetValue<int>());
            Assert.Equal("hour", (string?)rep["bucket"]);
            // gpt-5-codex (config): 1.25 + 1.0; opus 5.5 fast: (4 + 8) × 2; geo "<script>" is dropped, not stored.
            Assert.Equal(2.25 + 24, rep["agents"]!["codex:s1"]!["cost"]!.GetValue<double>(), 6);
            Assert.Equal(15, rep["agents"]!["gemini:g1"]!["unpricedTokens"]!.GetValue<long>());
            Assert.Equal(["gemini-2.5-pro"], rep["unpricedModels"]!.AsArray().Select(x => (string?)x));
            Assert.Equal(49, rep["buckets"]!.AsArray().Count);

            var pricing = JsonNode.Parse(await http.GetStringAsync("/api/pricing"))!;
            Assert.True(pricing["enabled"]!.GetValue<bool>());
            var models = pricing["models"]!.AsArray();
            Assert.Contains(models, m => (string?)m!["model"] == "claude-opus-5-5" && m["price"]!["input"]!.GetValue<double>() == 4);
            Assert.Contains(models, m => (string?)m!["model"] == "gpt-5-codex" && m["custom"]!.GetValue<bool>());

            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/usage/report?range=forever")).StatusCode);
            using var anon = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/usage/report")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AMC_Pricing__Models__gpt-5-codex__Input", null);
            Environment.SetEnvironmentVariable("AMC_Pricing__Models__gpt-5-codex__Output", null);
        }
    }

    private static async Task Report(HttpClient http, string tool, string agent, string kind, JsonObject data)
    {
        var body = new JsonObject { ["tool"] = tool, ["kind"] = kind, ["agent"] = new JsonObject { ["id"] = agent, ["title"] = "Work" }, ["data"] = data };
        var r = await http.PostAsync("/api/v1/report", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        r.EnsureSuccessStatusCode();
    }

    private static async Task<JsonNode> Eventually(HttpClient http, string path, Func<JsonNode, bool> ok)
    {
        JsonNode? last = null;
        for (var i = 0; i < 100; i++)
        {
            last = JsonNode.Parse(await http.GetStringAsync(path + "&n=" + i))!;
            if (ok(last)) return last;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException("condition not met: " + last?.ToJsonString());
    }

    public void Dispose()
    {
        _data.Dispose();
        _claude.Dispose();
    }
}
