using Amc.Daemon;
using Amc.Daemon.Collectors.Git;
using Microsoft.Extensions.DependencyInjection;

namespace Amc.Tests.Git;

public sealed class GitUnitTests
{
    [Fact]
    public void Parses_status_porcelain_v2()
    {
        var z = string.Join('\0',
            "# branch.oid 0123456789abcdef0123456789abcdef01234567", "# branch.head feature/x", "# branch.upstream origin/feature/x", "# branch.ab +2 -3",
            "1 .M N... 100644 100644 100644 aaa bbb src/file with space.cs",
            "1 A. N... 000000 100644 100644 000 bbb added.cs",
            "1 .D N... 100644 100644 000000 aaa aaa gone.cs",
            "2 R. N... 100644 100644 100644 aaa aaa R100 new name.cs", "old name.cs",
            "u UU N... 100644 100644 100644 100644 a b c conflict.cs",
            "? untracked dir/", "");
        var s = GitParsers.ParseStatus(z);
        Assert.Equal(("0123456789abcdef0123456789abcdef01234567", "feature/x", false, "origin/feature/x", 2, 3),
            (s.Head, s.Branch, s.Detached, s.Upstream, s.Ahead, s.Behind));
        Assert.Equal("M", s.Files["src/file with space.cs"].Status);
        Assert.Equal("A", s.Files["added.cs"].Status);
        Assert.Equal("D", s.Files["gone.cs"].Status);
        Assert.Equal(new FileState("R", OldPath: "old name.cs"), s.Files["new name.cs"]);
        Assert.Equal("U", s.Files["conflict.cs"].Status);
        Assert.Equal("?", s.Files["untracked dir/"].Status);

        var detached = GitParsers.ParseStatus("# branch.oid (initial)\0# branch.head (detached)\0");
        Assert.Equal((null, null, true), (detached.Head, detached.Branch, detached.Detached));
    }

    [Fact]
    public void Parses_numstat_with_renames_and_binaries()
    {
        var n = GitParsers.ParseNumstat("3\t1\ta.txt\0-\t-\timg.png\0" + "0\t0\t\0old.txt\0new.txt\0");
        Assert.Equal((3, 1), n["a.txt"]);
        Assert.Equal((null, null), n["img.png"]);
        Assert.Equal((0, 0), n["new.txt"]);
        Assert.False(n.ContainsKey("old.txt"));
    }

    [Fact]
    public void Parses_worktree_list()
    {
        var z = "worktree /r/main\0HEAD abc\0branch refs/heads/main\0\0worktree /tmp/x/wt\0HEAD def\0detached\0locked\0\0worktree /gone\0HEAD 123\0branch refs/heads/g\0prunable gitdir file points to non-existent location\0\0";
        var e = GitParsers.ParseWorktreeList(z);
        Assert.Equal(3, e.Count);
        Assert.Equal(new WorktreeEntry("/r/main", "abc", "main", false, false, false, false), e[0]);
        Assert.Equal(new WorktreeEntry("/tmp/x/wt", "def", null, true, false, true, false), e[1]);
        Assert.True(e[2].Prunable);
    }

    [Fact]
    public void Parses_log_with_shortstat()
    {
        var log = "\u001e" + "aaa\u001fAnn\u001f2026-10-07T10:00:00+00:00\u001fFix: a | b\n\n 2 files changed, 3 insertions(+)\n" +
                  "\u001e" + "bbb\u001fBob\u001f2026-10-06T10:00:00+00:00\u001fMerge\n";
        var c = GitParsers.ParseLog(log);
        Assert.Equal(("aaa", "Ann", "Fix: a | b", 2), (c[0].Sha, c[0].Author, c[0].Subject, c[0].FilesChanged));
        Assert.Equal(0, c[1].FilesChanged);
    }

    [Fact]
    public void Activity_windows_attribute_only_a_single_overlapping_agent()
    {
        var w = new ActivityWindows();
        var t0 = DateTimeOffset.UtcNow;
        w.Record(new WorktreeActivity("wt", "a", "1", ActivityKinds.Shell, ActivityKinds.Start, t0));
        w.Record(new WorktreeActivity("wt", "a", "1", ActivityKinds.Shell, ActivityKinds.End, t0.AddSeconds(5)));
        w.Record(new WorktreeActivity("wt", "b", "2", ActivityKinds.Read, ActivityKinds.Start, t0.AddSeconds(1)));
        Assert.Equal("a", w.SoleAgent("wt", t0.AddSeconds(-1), t0.AddSeconds(6)));
        Assert.Null(w.SoleAgent("wt", t0.AddSeconds(6), t0.AddSeconds(9)));   // after the window
        Assert.Null(w.SoleAgent("other", t0, t0.AddSeconds(9)));             // other worktree

        w.Record(new WorktreeActivity("wt", "b", "3", ActivityKinds.Write, ActivityKinds.Start, t0.AddSeconds(4)));
        Assert.Null(w.SoleAgent("wt", t0, t0.AddSeconds(6)));                // two agents overlap
        Assert.Equal("b", w.SoleAgent("wt", t0.AddSeconds(6), t0.AddSeconds(9))); // b still open
        var late = t0 + ActivityWindows.MaxOpen + TimeSpan.FromMinutes(1);
        Assert.Null(w.SoleAgent("wt", late, late.AddSeconds(5)));            // an unterminated window expires
    }

    [Fact]
    public void Path_validation_resolves_symlinks()
    {
        var root = GitPaths.RealPath(Path.Combine(Path.GetTempPath(), "amc-git-paths-" + Guid.NewGuid().ToString("N")))!;
        var outside = root + "-outside";
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(outside);
            File.CreateSymbolicLink(Path.Combine(root, "inner-link"), Path.Combine(root, "src"));
            File.CreateSymbolicLink(Path.Combine(root, "src", "up"), "../..");
            File.CreateSymbolicLink(Path.Combine(root, "loop"), Path.Combine(root, "loop"));

            Assert.True(GitPaths.TryResolve(root, "src/new/file.cs", out var rel, out var full, out _));
            Assert.Equal(("src/new/file.cs", Path.Combine(root, "src/new/file.cs")), (rel, full));
            Assert.True(GitPaths.TryResolve(root, "./inner-link//x.cs", out rel, out full, out _));
            Assert.Equal(("inner-link/x.cs", Path.Combine(root, "src", "x.cs")), (rel, full));
            Assert.False(GitPaths.TryResolve(root, "src/up/" + Path.GetFileName(outside), out _, out _, out var err));
            Assert.Equal("path escapes the worktree", err);
            Assert.False(GitPaths.TryResolve(root, "loop/x", out _, out _, out _));
            Assert.False(GitPaths.TryResolve(root, "", out _, out _, out _));
            Assert.False(GitPaths.TryResolve(root, "/etc/passwd", out _, out _, out _));
            Assert.False(GitPaths.TryResolve(root, "src/.GIT/config", out _, out _, out _));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Workspace_scan_skips_build_and_hidden_dirs()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "amc-git-scan-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (var d in new[] { "a/.git", "node_modules/x/.git", ".hidden/y/.git", "b/c/.git", "b/c/nested/.git", "d/e/f/g/.git" })
                Directory.CreateDirectory(Path.Combine(root, d));
            var found = GitCollector.FindWorkingTrees([root], 3).Select(p => Path.GetRelativePath(root, p)).Order().ToList();
            Assert.Equal(["a", "b/c"], found);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class PlaceholderIndex : IWorktreeIndex
    {
        public ValueTask<WorktreeHit?> ResolveAsync(string absolutePath, CancellationToken ct = default) => ValueTask.FromResult<WorktreeHit?>(null);
        public void NotifyActivity(WorktreeActivity activity) { }
    }

    [Fact]
    public async Task AddGit_replaces_the_default_worktree_index_and_registers_the_hosted_service()
    {
        await using var env = await GitTestEnv.CreateAsync();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddSingleton(env.Health).AddSingleton(env.State).AddSingleton<Amc.Daemon.Pipeline.IEventSink>(env.Sink);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(env.Options));
        services.AddSingleton<IWorktreeIndex, PlaceholderIndex>();
        services.AddGit();
        await using var sp = services.BuildServiceProvider();
        var index = sp.GetRequiredService<IWorktreeIndex>();
        Assert.IsType<GitCollector>(index);
        Assert.Same(index, Assert.Single(sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>()));
    }
}
