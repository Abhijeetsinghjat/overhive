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

    private static AgentEvent Status(string status, int daysAgo, string wt, string file) => new()
    {
        Type = EventTypes.GitFileStatus, Source = EventSources.Git, Ts = Now.AddDays(-daysAgo), WorktreeId = wt, FilePath = file,
        Data = Json.ToObject(new GitFileStatusData(status)),
    };

    private static AgentEvent Lifecycle(string type, int daysAgo, string wt) => new()
    {
        Type = type, Source = EventSources.Git, Ts = Now.AddDays(-daysAgo), WorktreeId = wt,
        Data = Json.ToObject(new WorktreeData("/r/" + wt, "r")),
    };

    [Fact]
    public void Compaction_drops_newest_rows_of_removed_worktrees_and_old_clean_files()
    {
        using var store = new EventStore(Path.Combine(_dir.Path, "amc.db"));
        store.Append([
            Lifecycle(EventTypes.WorktreeCreated, 40, "gone"), Status("D", 30, "gone", "x.cs"), Status("D", 30, "gone", "y.cs"),
            Lifecycle(EventTypes.WorktreeDeleted, 20, "gone"),                                   // removed long ago → its rows go
            Lifecycle(EventTypes.WorktreeCreated, 40, "back"), Status("M", 30, "back", "x.cs"),
            Lifecycle(EventTypes.WorktreeDeleted, 20, "back"), Lifecycle(EventTypes.WorktreeCreated, 10, "back"), // restored → kept
            Lifecycle(EventTypes.WorktreeCreated, 40, "fresh"), Status("M", 3, "fresh", "z.cs"),
            Lifecycle(EventTypes.WorktreeDeleted, 2, "fresh"),                                   // removed recently → kept
            Lifecycle(EventTypes.WorktreeCreated, 40, "w"),
            Status("clean", 30, "w", "old-clean.cs"),                                            // stale clean newest → goes
            Status("clean", 2, "w", "new-clean.cs"),                                             // recent → kept
            Status("M", 30, "w", "old-dirty.cs"),                                                // still dirty → kept
            Status("M", 30, "w", "both.cs"), Status("clean", 20, "w", "both.cs"),                // superseded, then stale clean → both go
            Status("M", 3, "w", "recent.cs"), Status("clean", 1, "w", "recent.cs"),              // superseded row is recent → kept
        ]);
        var max = store.Append([Status("clean", 30, "w", "after-snapshot.cs")])[0].Seq - 1;

        var r = store.Prune(Now, maxSeq: max, detailDays: 7, historyDays: 180);

        var left = store.Query(new EventQuery(Limit: 100)).Where(e => e.Type == EventTypes.GitFileStatus)
            .Select(e => $"{e.WorktreeId}|{e.FilePath}").Order().ToList();
        Assert.Equal(5, r.Compacted);
        Assert.Equal(["back|x.cs", "fresh|z.cs", "w|after-snapshot.cs", "w|new-clean.cs", "w|old-dirty.cs", "w|recent.cs", "w|recent.cs"], left);
    }

    public void Dispose() => _dir.Dispose();
}
