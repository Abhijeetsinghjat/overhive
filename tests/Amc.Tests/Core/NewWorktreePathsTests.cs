using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Tests.Core;

/// <summary>Both OS forms run on every OS: the internal overloads take the OS as a flag.</summary>
public class NewWorktreePathsTests
{
    [Theory]
    [InlineData("/home/me/dev/app", "feat/login", null, "/home/me/dev/app.worktrees/feat-login")]
    [InlineData("/home/me/dev/app", "fix", "", "/home/me/dev/app.worktrees/fix")]
    [InlineData("/home/me/dev/app", "a/b/c", "~/wt/{repo}", "/home/me/wt/app/a-b-c")]
    [InlineData("/home/me/dev/app", "x", "/srv/trees", "/srv/trees/x")]
    [InlineData("/home/me/dev/app", "x", "relative/{repo}", null)]
    [InlineData("/home/me/dev/app/", "x", null, "/home/me/dev/app.worktrees/x")]
    public void Default_path_unix(string main, string branch, string? root, string? expected) =>
        Assert.Equal(expected, NewWorktreePaths.DefaultPath(main, branch, root, "/home/me", windows: false));

    [Theory]
    [InlineData(@"C:\Users\me\dev\app", "feat/login", null, "C:/Users/me/dev/app.worktrees/feat-login")]
    [InlineData(@"c:/Users/me/dev/app", "feat/a|b", null, "C:/Users/me/dev/app.worktrees/feat-a-b")]
    [InlineData(@"C:\Users\me\dev\app", "x", @"~\wt\{repo}", "C:/Users/me/wt/app/x")]
    [InlineData(@"C:\Users\me\dev\app", "x", @"D:\trees\{repo}", "D:/trees/app/x")]
    [InlineData(@"\\server\share\app", "x", null, "//server/share/app.worktrees/x")]
    [InlineData(@"C:\Users\me\dev\app", "x", @"trees", null)]
    public void Default_path_windows(string main, string branch, string? root, string? expected) =>
        Assert.Equal(expected, NewWorktreePaths.DefaultPath(main, branch, root, @"C:\Users\me", windows: true));

    [Theory]
    [InlineData("feat/login", false, "feat-login")]
    [InlineData("a<b>c\"d", false, "a<b>c\"d")]   // valid file names on Unix
    [InlineData("a<b>c\"d", true, "a-b-c-d")]
    [InlineData("/", false, "worktree")]
    public void Branch_slug(string branch, bool windows, string expected) => Assert.Equal(expected, NewWorktreePaths.BranchSlug(branch, windows));

    [Theory]
    [InlineData("feat/x", true)]
    [InlineData("", false)]
    [InlineData(" x", false)]
    [InlineData("-b", false)]
    [InlineData("@{-1}", false)]
    [InlineData("a@{u}", false)]
    [InlineData("HEAD", false)]
    [InlineData("a\tb", false)]
    public void Branch_precheck(string branch, bool ok) => Assert.Equal(ok, NewWorktreePaths.BranchPrecheck(branch) is null);

    [Fact]
    public void Long_paths_warn_on_windows_only()
    {
        var p = "C:/" + new string('a', 250);
        Assert.NotNull(NewWorktreePaths.LongPathWarning(p, windows: true));
        Assert.Null(NewWorktreePaths.LongPathWarning(p, windows: false));
        Assert.Null(NewWorktreePaths.LongPathWarning("C:/short", windows: true));
        Assert.NotNull(NewWorktreePaths.BasePrecheck("--upload-pack=x"));
        Assert.Null(NewWorktreePaths.BasePrecheck("origin/main"));
    }
}

public class WorktreeOriginReducerTests
{
    private readonly WorldState _s = new();
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private void Apply(string type, object data, int minutes) => Reducer.ApplyBatch(_s, [new AgentEvent
    {
        Type = type, Source = EventSources.Git, Ts = T0.AddMinutes(minutes), RepositoryId = "repo:1", WorktreeId = "wt:1",
        Data = Json.ToObject(data),
    }]);

    [Fact]
    public void Provisioned_marks_the_worktree_and_a_new_worktree_at_a_reused_path_starts_unmarked()
    {
        Apply(EventTypes.WorktreeCreated, new WorktreeData("/w/app.worktrees/x", "repo:1", "x"), 0);
        Apply(EventTypes.WorktreeProvisioned, new WorktreeProvisionedData("/w/app.worktrees/x", "x", "origin/main", true, "dashboard", "T1", "claude"), 1);
        var o = _s.Worktrees["wt:1"].Origin;
        Assert.Equal(("dashboard", "x", "origin/main", true, "T1", "claude"), (o!.Via, o.Branch, o.Base, o.NewBranch, o.TerminalId, o.Tool));

        Apply(EventTypes.WorktreeChanged, new WorktreeData("/w/app.worktrees/x", "repo:1", "x", Modified: 2), 2);
        Assert.NotNull(_s.Worktrees["wt:1"].Origin);                      // status refreshes keep it
        Apply(EventTypes.WorktreeDeleted, new WorktreeData("/w/app.worktrees/x", "repo:1"), 3);
        Assert.NotNull(_s.Worktrees["wt:1"].Origin);                      // kept after removal (history)
        Apply(EventTypes.WorktreeCreated, new WorktreeData("/w/app.worktrees/x", "repo:1", "y"), 4);
        Assert.Null(_s.Worktrees["wt:1"].Origin);                         // someone else's worktree now
    }

    [Fact]
    public void Provisioned_before_discovery_creates_the_entry_that_discovery_completes()
    {
        Apply(EventTypes.WorktreeProvisioned, new WorktreeProvisionedData("/w/t", "t", null, false, "cli"), 0);
        Apply(EventTypes.WorktreeCreated, new WorktreeData("/w/t", "repo:1", "t", Head: "abc"), 1);
        var w = _s.Worktrees["wt:1"];
        Assert.Equal(("abc", false), (w.Head, w.Removed));
        Assert.Equal("cli", w.Origin!.Via);
    }
}
