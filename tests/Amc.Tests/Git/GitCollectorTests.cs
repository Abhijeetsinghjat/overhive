using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon;
using Amc.Daemon.Collectors.Git;

namespace Amc.Tests.Git;

[Collection(WatcherCollection.Name)]
public sealed class GitCollectorTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Scan_discovers_repository_and_all_worktrees_including_ones_outside_roots()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var main = env.InitRepo("ws/proj");
        var linked = PathText.Normalize(Path.Combine(env.Dir, "elsewhere", "wt-feature"));
        Assert.Equal(0, GitTestEnv.RunGit(main, "worktree", "add", "-q", "-b", "feature", linked).Code);
        env.InitRepo("ws/deep/a/b/c/too-deep"); // beyond scan depth 3

        await env.Collector.ScanAsync(None);
        await env.Applied();

        var repoId = Ids.Repository(Path.Combine(main, ".git"));
        var (repo, wts) = env.State.Read(s => (s.Repositories.GetValueOrDefault(repoId)?.Name,
            s.Worktrees.Values.Select(w => (w.Root, w.IsMain, w.RepositoryId)).OrderBy(w => w.Root).ToList()));
        Assert.Equal("proj", repo);
        Assert.Equal(2, wts.Count);
        Assert.Contains((main, true, repoId), wts);
        Assert.Contains((linked, false, repoId), wts);
        Assert.Single(env.Sink.OfType(EventTypes.RepositoryDiscovered));
        Assert.Equal(2, env.Sink.OfType(EventTypes.WorktreeCreated).Count);

        // A second scan is idempotent.
        env.Sink.Clear();
        await env.Collector.ScanAsync(None);
        Assert.Empty(env.Sink.OfType(EventTypes.WorktreeCreated));
        Assert.Empty(env.Sink.OfType(EventTypes.RepositoryDiscovered));
    }

    [Fact]
    public async Task Resolve_discovers_on_demand_and_caches_misses()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var main = env.InitRepo("repo");
        var linked = PathText.Normalize(Path.Combine(env.Dir, "scratch", "wt"));
        GitTestEnv.RunGit(main, "worktree", "add", "-q", "-b", "side", linked);
        var outside = Directory.CreateDirectory(Path.Combine(env.Dir, "plain")).FullName;

        var hit = await env.Collector.ResolveAsync(Path.Combine(linked, "src", "new", "File.cs"));
        Assert.NotNull(hit);
        Assert.Equal(Ids.Worktree(linked), hit.WorktreeId);
        Assert.Equal("src/new/File.cs", hit.RelativePath);
        // The whole repository was discovered, including the main worktree.
        var mainHit = await env.Collector.ResolveAsync(Path.Combine(main, "README.md"));
        Assert.Equal(Ids.Worktree(main), mainHit?.WorktreeId);

        var calls = Calls(env);
        Assert.NotNull(await env.Collector.ResolveAsync(Path.Combine(linked, "other.txt")));
        Assert.Equal(calls, Calls(env)); // known path: no git invocation

        Assert.Null(await env.Collector.ResolveAsync(Path.Combine(outside, "a.txt")));
        calls = Calls(env);
        Assert.Null(await env.Collector.ResolveAsync(Path.Combine(outside, "b.txt")));
        Assert.Equal(calls, Calls(env)); // negative cache
    }

    private static long Calls(GitTestEnv env) =>
        env.Health.Snapshot().Single(c => c.Name == GitCli.HealthName).Counters.GetValueOrDefault("git_calls");

    [Fact]
    public async Task Refresh_emits_file_status_deltas_for_modify_add_delete_rename_and_clean()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var dir = env.InitRepo("repo", ("a.txt", "1\n2\n"), ("b.txt", "b\n"), ("c.txt", "c1\nc2\nc3\n"), ("keep.txt", "k\n"));
        var wt = await env.DiscoverAsync(dir);
        Assert.Empty(env.Sink.OfType(EventTypes.GitFileStatus));

        File.AppendAllText(Path.Combine(dir, "a.txt"), "3\n4\n");
        GitTestEnv.Write(dir, "e.txt", "x\ny\nz\n");
        GitTestEnv.Write(dir, "newdir/f.txt", "f\n"); // untracked-files=normal reports the directory
        File.Delete(Path.Combine(dir, "b.txt"));
        GitTestEnv.RunGit(dir, "mv", "c.txt", "c2.txt");
        await env.Collector.RefreshAsync(wt, None);

        Assert.Equal(new GitFileStatusData("M", 2, 0), env.FileEvents("a.txt").Single());
        Assert.Equal(new GitFileStatusData("?", 3, 0), env.FileEvents("e.txt").Single());
        Assert.Equal(new GitFileStatusData("?"), env.FileEvents("newdir/").Single());
        Assert.Equal("D", env.FileEvents("b.txt").Single().Status);
        var r = env.FileEvents("c2.txt").Single();
        Assert.Equal(("R", "c.txt"), (r.Status, r.OldPath));
        Assert.Empty(env.FileEvents("keep.txt"));
        Assert.All(env.Sink.OfType(EventTypes.GitFileStatus), e => Assert.Equal((wt, Evidence.Observed), (e.WorktreeId, e.Evidence)));

        await env.Applied();
        var w = env.State.Read(s => s.Worktrees[wt]);
        Assert.Equal((2, 0, 1, 2), (w.Modified, w.Added, w.DeletedFiles, w.Untracked)); // M + R count as modified

        // Unchanged → no events; reverting a file → "clean".
        env.Sink.Clear();
        await env.Collector.RefreshAsync(wt, None);
        Assert.Empty(env.Sink.OfType(EventTypes.GitFileStatus));
        GitTestEnv.RunGit(dir, "checkout", "--", "a.txt");
        await env.Collector.RefreshAsync(wt, None);
        Assert.Equal("clean", env.FileEvents("a.txt").Single().Status);
        Assert.Single(env.Sink.OfType(EventTypes.GitFileStatus));
    }

    [Fact]
    public async Task Commit_is_detected_once_with_dedupe_key()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var dir = env.InitRepo("repo", ("a.txt", "a\n"));
        var wt = await env.DiscoverAsync(dir);

        GitTestEnv.Write(dir, "a.txt", "changed\n");
        GitTestEnv.Write(dir, "b.txt", "new\n");
        await env.Collector.RefreshAsync(wt, None);
        GitTestEnv.RunGit(dir, "add", "-A");
        GitTestEnv.RunGit(dir, "commit", "-q", "-m", "Add b and change a");
        var sha = GitTestEnv.Head(dir);
        await env.Collector.RefreshAsync(wt, None);
        await env.Collector.RefreshAsync(wt, None);

        var commit = Assert.Single(env.Sink.OfType(EventTypes.GitCommitCreated));
        var data = commit.Payload<GitCommitData>()!;
        Assert.Equal((sha, "Add b and change a", "main", 2, "Test"), (data.Sha, data.Subject, data.Branch, data.FilesChanged, data.Author));
        Assert.Equal($"commit:{wt}:{sha}", commit.DedupeKey);
        Assert.Null(commit.AgentId);
        Assert.Equal("clean", env.FileEvents("a.txt").Last().Status);
        Assert.Equal("clean", env.FileEvents("b.txt").Last().Status);
        await env.Applied();
        Assert.Equal(sha, env.State.Read(s => s.Worktrees[wt].Head));
        Assert.True(env.State.Read(s => s.Commits.ContainsKey(sha)));
    }

    [Fact]
    public async Task Branch_switch_emits_checkout()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var dir = env.InitRepo("repo");
        var wt = await env.DiscoverAsync(dir);

        GitTestEnv.RunGit(dir, "checkout", "-q", "-b", "topic");
        await env.Collector.RefreshAsync(wt, None);

        var ev = Assert.Single(env.Sink.OfType(EventTypes.GitCheckout)).Payload<GitRefData>()!;
        Assert.Equal(("topic", "main"), (ev.Branch, ev.PreviousBranch));
        Assert.Empty(env.Sink.OfType(EventTypes.GitCommitCreated));
        await env.Applied();
        Assert.Equal("topic", env.State.Read(s => s.Worktrees[wt].Branch));
    }

    [Fact]
    public async Task Merge_conflict_sets_operation_and_emits_conflict()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var dir = env.InitRepo("repo", ("f.txt", "base\n"));
        GitTestEnv.RunGit(dir, "checkout", "-q", "-b", "other");
        GitTestEnv.Write(dir, "f.txt", "other\n");
        GitTestEnv.RunGit(dir, "commit", "-q", "-am", "other");
        GitTestEnv.RunGit(dir, "checkout", "-q", "main");
        GitTestEnv.Write(dir, "f.txt", "mine\n");
        GitTestEnv.RunGit(dir, "commit", "-q", "-am", "mine");
        var wt = await env.DiscoverAsync(dir);

        Assert.NotEqual(0, GitTestEnv.RunGit(dir, "merge", "other").Code);
        await env.Collector.RefreshAsync(wt, None);

        var conflict = Assert.Single(env.Sink.OfType(EventTypes.GitConflict)).Payload<GitConflictData>()!;
        Assert.Equal(["f.txt"], conflict.Paths);
        Assert.Equal("merge", conflict.Operation);
        Assert.Equal("U", env.FileEvents("f.txt").Single().Status);
        Assert.Equal("started", Assert.Single(env.Sink.OfType(EventTypes.GitMerge)).Payload<GitRefData>()!.Detail);
        await env.Applied();
        var w = env.State.Read(s => (s.Worktrees[wt].Operation, s.Worktrees[wt].Conflicted, s.Conflicts.ContainsKey($"git|{wt}")));
        Assert.Equal(("merge", 1, true), w);

        // Same conflict on the next refresh is not re-reported; resolving ends the operation.
        await env.Collector.RefreshAsync(wt, None);
        Assert.Single(env.Sink.OfType(EventTypes.GitConflict));
        GitTestEnv.Write(dir, "f.txt", "resolved\n");
        GitTestEnv.RunGit(dir, "commit", "-q", "-am", "merge other");
        await env.Collector.RefreshAsync(wt, None);
        await env.Applied();
        Assert.Null(env.State.Read(s => s.Worktrees[wt].Operation));
        Assert.Contains(env.Sink.OfType(EventTypes.GitMerge), e => e.Payload<GitRefData>()!.Detail == "finished");
        Assert.Contains(env.Sink.OfType(EventTypes.GitCommitCreated), e => e.Payload<GitCommitData>()!.Subject == "merge other");
    }

    [Fact]
    public async Task Removed_worktree_emits_worktree_deleted()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var main = env.InitRepo("repo");
        var linked = PathText.Normalize(Path.Combine(env.Dir, "tmp-wt"));
        GitTestEnv.RunGit(main, "worktree", "add", "-q", "-b", "tmp", linked);
        await env.DiscoverAsync(main);
        var id = Ids.Worktree(linked);
        Assert.False(env.State.Read(s => s.Worktrees[id].Removed));

        GitTestEnv.RunGit(main, "worktree", "remove", linked);
        await env.Collector.RefreshAsync(id, None); // root missing → relist is scheduled; run it directly
        await env.Collector.DiscoverRepositoryAsync(Path.Combine(main, ".git"), None);
        await env.Applied();

        Assert.Single(env.Sink.OfType(EventTypes.WorktreeDeleted), e => e.WorktreeId == id);
        Assert.True(env.State.Read(s => s.Worktrees[id].Removed));
        Assert.Null(await env.Collector.ResolveAsync(Path.Combine(linked, "x")));
    }

    [Fact]
    public async Task Shell_change_is_attributed_only_to_a_sole_active_agent()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var dir = env.InitRepo("repo", ("a.txt", "a\n"), ("b.txt", "b\n"));
        var wt = await env.DiscoverAsync(dir);
        void Act(string agent, string tool, string phase) =>
            env.Collector.NotifyActivity(new WorktreeActivity(wt, agent, tool, ActivityKinds.Shell, phase, DateTimeOffset.UtcNow));

        // One agent: attributed (inferred), including the commit it makes.
        Act("agent-one", "t1", ActivityKinds.Start);
        GitTestEnv.Write(dir, "a.txt", "a\nmore\n");
        Act("agent-one", "t1", ActivityKinds.End);
        await env.Collector.RefreshAsync(wt, None);
        var e1 = Assert.Single(env.Sink.OfType(EventTypes.GitFileStatus));
        Assert.Equal(("agent-one", Evidence.Inferred), (e1.Payload<GitFileStatusData>()!.AttributedAgentId, e1.Evidence));

        // Next window, no activity: unattributed.
        GitTestEnv.Write(dir, "a.txt", "a\nmore\nagain\n");
        await env.Collector.RefreshAsync(wt, None);
        Assert.Null(env.FileEvents("a.txt").Last().AttributedAgentId);

        // Two agents overlapping: unattributed.
        Act("agent-one", "t2", ActivityKinds.Start);
        Act("agent-two", "t3", ActivityKinds.Start);
        GitTestEnv.Write(dir, "b.txt", "b\nchanged\n");
        Act("agent-one", "t2", ActivityKinds.End);
        Act("agent-two", "t3", ActivityKinds.End);
        await env.Collector.RefreshAsync(wt, None);
        var e3 = env.Sink.OfType(EventTypes.GitFileStatus).Single(e => e.FilePath == "b.txt");
        Assert.Null(e3.Payload<GitFileStatusData>()!.AttributedAgentId);
        Assert.Equal(Evidence.Observed, e3.Evidence);

        // Sole agent commits: commit event carries the agent (inferred).
        Act("agent-two", "t4", ActivityKinds.Start);
        GitTestEnv.RunGit(dir, "commit", "-q", "-am", "agent commit");
        Act("agent-two", "t4", ActivityKinds.End);
        await env.Collector.RefreshAsync(wt, None);
        var c = Assert.Single(env.Sink.OfType(EventTypes.GitCommitCreated));
        Assert.Equal(("agent-two", Evidence.Inferred), (c.AgentId, c.Evidence));
    }

    [Fact]
    public async Task Metadata_watcher_and_activity_trigger_debounced_refreshes()
    {
        var settings = new GitSettings { ScanOnStart = true, Watch = true, Debounce = TimeSpan.FromMilliseconds(100) };
        await using var env = await GitTestEnv.CreateAsync(settings);
        var dir = env.InitRepo("ws/repo", ("a.txt", "a\n"));
        await env.Collector.StartAsync(None);
        var wt = Ids.Worktree(dir);
        await GitTestEnv.Eventually(() => env.State.Read(s => s.Worktrees.ContainsKey(wt)), "startup scan");
        Assert.True(env.Collector.WatchCount > 0);

        // Index change (git add) is seen through the .git watcher.
        GitTestEnv.Write(dir, "staged.txt", "s\n");
        GitTestEnv.RunGit(dir, "add", "staged.txt");
        await GitTestEnv.Eventually(() => env.FileEvents("staged.txt").Any(f => f.Status == "A"), "watcher-triggered refresh");

        // A plain working-tree edit is invisible to the metadata watcher; an activity notification triggers the refresh.
        GitTestEnv.Write(dir, "a.txt", "edited\n");
        env.Collector.NotifyActivity(new WorktreeActivity(wt, "agent-x", "t1", ActivityKinds.Write, ActivityKinds.End, DateTimeOffset.UtcNow));
        await GitTestEnv.Eventually(() => env.FileEvents("a.txt").Any(f => f.Status == "M"), "activity-triggered refresh");

        // Commit via the CLI: refs/heads watcher → commit event.
        GitTestEnv.RunGit(dir, "commit", "-q", "-am", "via watcher");
        await GitTestEnv.Eventually(() => env.Sink.OfType(EventTypes.GitCommitCreated).Count == 1, "commit detection");
        await env.Collector.StopAsync(None);
    }
}
