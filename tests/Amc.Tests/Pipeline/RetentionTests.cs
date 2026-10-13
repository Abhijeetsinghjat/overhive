using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Storage;

namespace Amc.Tests.Pipeline;

public class RetentionTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static AgentEvent Ev(string type, int daysAgo, string? wt = null, string? file = null) => new()
    {
        Type = type, Source = EventSources.Git, Ts = Now.AddDays(-daysAgo), WorktreeId = wt, FilePath = file,
        Data = Json.ToObject(new GitFileStatusData("M")),
    };

    [Fact]
    public void Compacts_superseded_status_drops_old_detail_keeps_history_and_newer_than_snapshot()
    {
        using var store = new EventStore(Path.Combine(_dir.Path, "amc.db"));
        store.Append([
            Ev(EventTypes.GitFileStatus, 20, "w", "a.cs"), Ev(EventTypes.GitFileStatus, 15, "w", "a.cs"), // superseded → compacted
            Ev(EventTypes.GitFileStatus, 12, "w", "a.cs"),                                               // newest for a.cs → kept
            Ev(EventTypes.GitFileStatus, 30, "w", "b.cs"),                                               // only one for b.cs → kept
            Ev(EventTypes.CommandCompleted, 10), Ev(EventTypes.CommandCompleted, 2),                     // old detail deleted, recent kept
            Ev(EventTypes.AgentCreated, 30), Ev(EventTypes.GitCommitCreated, 60),                       // history (180d) kept
        ]);
        var lateOld = store.Append([Ev(EventTypes.CommandCompleted, 20)])[0];                            // newer than the snapshot

        var r = store.Prune(Now, maxSeq: lateOld.Seq - 1, detailDays: 7, historyDays: 180);

        var left = store.Query(new EventQuery(Limit: 100)).Select(e => (e.Type, e.FilePath, (int)(Now - e.Ts).TotalDays)).ToList();
        Assert.Equal(2, r.Compacted);
        Assert.Equal(1, r.DetailDeleted);
        Assert.Contains((EventTypes.GitFileStatus, "a.cs", 12), left);
        Assert.Contains((EventTypes.GitFileStatus, "b.cs", 30), left);
        Assert.Contains((EventTypes.CommandCompleted, (string?)null, 2), left);
        Assert.Contains((EventTypes.CommandCompleted, (string?)null, 20), left); // beyond the snapshot: untouched
        Assert.Contains(left, x => x.Type == EventTypes.AgentCreated);
        Assert.Contains(left, x => x.Type == EventTypes.GitCommitCreated);
    }

    public void Dispose() => _dir.Dispose();
}
