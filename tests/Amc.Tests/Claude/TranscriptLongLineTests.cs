using System.Text;
using Amc.Core.Events;
using Amc.Daemon.Providers.Claude;

namespace Amc.Tests.Claude;

public sealed class TranscriptLongLineTests : IDisposable
{
    private readonly ClaudeHarness _h = new();
    private const string S = ClaudeHarness.SessionId;

    public void Dispose() => _h.Dispose();

    private static string Head(string type, string ts) =>
        $"{{\"parentUuid\":null,\"isSidechain\":false,\"type\":\"{type}\",\"uuid\":\"{type}-{ts}\",\"timestamp\":\"{ts}\",\"cwd\":\"/work/repo\",\"sessionId\":\"{S}\",\"version\":\"2.1.292\",\"gitBranch\":\"main\",";

    [Fact]
    public async Task Lines_over_the_cap_still_start_and_end_their_tool_calls()
    {
        var huge = new string('A', TranscriptIngestor.MaxLineBytes + 1024);
        var lines = new[]
        {
            Head("user", "2026-10-07T10:00:00.000Z") + "\"promptId\":\"p-1\",\"message\":{\"role\":\"user\",\"content\":\"Fetch it\"}}",
            Head("assistant", "2026-10-07T10:00:01.000Z") + "\"message\":{\"model\":\"claude-opus-5-5\",\"id\":\"m1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[" +
                $"{{\"type\":\"tool_use\",\"id\":\"toolu_big\",\"name\":\"WebFetch\",\"input\":{{\"prompt\":\"{huge}\"}}}}]}}}}",
            Head("user", "2026-10-07T10:00:02.000Z") + "\"message\":{\"role\":\"user\",\"content\":[" +
                $"{{\"tool_use_id\":\"toolu_big\",\"type\":\"tool_result\",\"content\":\"{huge}\"}}]}}}}",
            Head("assistant", "2026-10-07T10:00:03.000Z") + "\"message\":{\"model\":\"claude-opus-5-5\",\"id\":\"m2\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"after\"}]}}",
        };
        Directory.CreateDirectory(_h.SessionDir);
        File.WriteAllText(_h.MainTranscript, string.Join("\n", lines) + "\n");

        await _h.Ingestor().ScanAllAsync(CancellationToken.None);

        Assert.Contains(_h.Sink.Stored, e => e.Type == EventTypes.ToolStarted && e.DedupeKey == "tool:toolu_big:start");
        Assert.Contains(_h.Sink.Stored, e => e.Type is EventTypes.ToolCompleted or EventTypes.ToolFailed && e.DedupeKey == "tool:toolu_big:end");
        Assert.Contains(_h.Sink.OfType(EventTypes.AgentOutput), e => e.Payload<AgentTextData>()!.Text == "after");
        Assert.DoesNotContain(_h.Sink.All, e => e.Data.ToJsonString().Contains("AAAAAAAAAA"));
        var health = _h.Health.Snapshot().Single(c => c.Name == TranscriptIngestor.HealthName);
        Assert.Equal(2, health.Counters["lines_oversized"]);
    }

    [Fact]
    public void Assembler_keeps_head_and_tail_and_counts_every_byte()
    {
        using var a = new TranscriptIngestor.LineAssembler(max: 100, headBytes: 10, tailBytes: 8);
        a.Append(Encoding.ASCII.GetBytes("0123456789abcdefghij"));
        Assert.False(a.Oversized);
        Assert.Equal("0123456789abcdefghij", Encoding.ASCII.GetString(a.Content.Span));
        for (var i = 0; i < 10; i++) a.Append(Encoding.ASCII.GetBytes(new string((char)('k' + i), 15)));
        a.Append(Encoding.ASCII.GetBytes("XYZ"));
        Assert.True(a.Oversized);
        Assert.Equal(20 + 150 + 3, a.Length);
        Assert.Equal("0123456789", Encoding.ASCII.GetString(a.Head.Span));
        Assert.Equal("tttttXYZ", Encoding.ASCII.GetString(a.Tail.Span));
        a.Reset();
        a.Append("x"u8);
        Assert.Equal("x", Encoding.ASCII.GetString(a.Content.Span));
    }

    [Fact]
    public void Oversized_record_without_tool_ids_is_skipped()
    {
        Assert.Null(TranscriptIngestor.OversizedRecord("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"very long prompt"u8, "\"}}"u8, 9 << 20));
    }
}
