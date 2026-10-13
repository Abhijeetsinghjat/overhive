using Amc.Daemon.Collectors.Git;
using Amc.Tests.Pipeline;

namespace Amc.Tests.Git;

public class GitPathsSymlinkTests
{
    [Fact]
    public void Symlink_into_dot_git_is_rejected()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, ".git"));
        File.WriteAllText(Path.Combine(dir.Path, ".git", "config"), "[remote] url=https://user:secret@x");
        File.CreateSymbolicLink(Path.Combine(dir.Path, "gl"), ".git");
        Assert.False(GitPaths.TryResolve(dir.Path, "gl/config", out _, out _, out var error));
        Assert.Contains(".git", error);
    }
}
