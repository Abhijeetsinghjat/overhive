using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Providers.Claude;
using Xunit.Abstractions;

namespace Amc.Tests.Claude;

/// <summary>Runs only when AMC_LOCAL_DRYRUN=1 (reads the real ~/.claude, read-only).</summary>
public sealed class LocalFactAttribute : FactAttribute
{
    public LocalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AMC_LOCAL_DRYRUN") != "1") Skip = "Set AMC_LOCAL_DRYRUN=1 to run against the real ~/.claude";
    }
}

/// <summary>
/// Read-only dry run of the transcript parser over the real ~/.claude/projects: in-memory
/// cursors, no worktree index, nothing persisted. Prints aggregate counts only (no content).
/// </summary>
[Trait("Category", "Local")]
public sealed class ClaudeLocalDryRunTests(ITestOutputHelper output)
{
    [LocalFact]
    public async Task Parse_real_transcripts_and_report_counts()
    {
        var options = new AmcOptions { ClaudeHome = Path.Combine(AmcOptions.Home, ".claude"), BackfillHours = 24 * 3650 };
        var sink = new CollectingSink();
        var health = new HealthRegistry();
        var ctx = new ClaudeContext(new StateStore());
        var mapper = new ClaudeEventMapper(ctx, new FakeWorktreeIndex(), TimeProvider.System);
        var ingestor = new TranscriptIngestor(mapper, sink, new InMemoryCursors(), health,
            Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System);
        await ingestor.ScanAllAsync(CancellationToken.None);

        foreach (var g in sink.Stored.GroupBy(e => e.Type).OrderBy(g => g.Key))
            output.WriteLine($"{g.Key,-26} {g.Count(),6}");
        output.WriteLine($"published {sink.All.Count}, stored after dedupe {sink.Stored.Count}");
        foreach (var g in sink.All.Where(e => e.DedupeKey is not null).GroupBy(e => e.DedupeKey).Where(g => g.Count() > 1)
                     .GroupBy(g => g.Key!.Split(':')[0] + "/" + g.First().Type))
            output.WriteLine($"duplicate-keys {g.Key} = {g.Count()}");
        foreach (var c in health.Snapshot().Single(h => h.Name == TranscriptIngestor.HealthName).Counters.OrderBy(k => k.Key))
            output.WriteLine($"counter {c.Key} = {c.Value}");
        var state = sink.Fold();
        var agents = state.Agents.Values.ToList();
        output.WriteLine($"agents main={agents.Count(a => a.Kind == Amc.Core.Domain.AgentKind.Main)} sub={agents.Count(a => a.Kind == Amc.Core.Domain.AgentKind.Sub)} " +
                         $"sub-without-parent={agents.Count(a => a.Kind == Amc.Core.Domain.AgentKind.Sub && a.ParentId is null)} " +
                         $"max-depth={agents.Select(a => Depth(state, a)).DefaultIfEmpty(0).Max()}");
        foreach (var g in agents.GroupBy(a => a.Status).OrderBy(g => g.Key)) output.WriteLine($"status {g.Key} = {g.Count()}");
        Assert.NotEmpty(sink.Stored);
    }

    private static int Depth(Amc.Core.State.WorldState s, Amc.Core.Domain.Agent a)
    {
        var d = 0;
        while (a.ParentId is not null && s.Agents.TryGetValue(a.ParentId, out var p) && d < 50) { a = p; d++; }
        return d;
    }
}
