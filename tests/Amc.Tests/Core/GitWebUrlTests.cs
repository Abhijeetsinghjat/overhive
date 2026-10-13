using Amc.Core.Domain;

namespace Amc.Tests.Core;

public class GitWebUrlTests
{
    [Theory]
    [InlineData("https://github.com/acme/acme-backend.git", "https://github.com/acme/acme-backend")]
    [InlineData("git@github.com:acme/acme.git", "https://github.com/acme/acme")]
    [InlineData("ssh://git@github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://x-access-token:ghs_SECRET@github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("git@gitlab.com:group/sub/repo.git", "https://gitlab.com/group/sub/repo")]
    [InlineData("git@ssh.dev.azure.com:v3/org/proj/repo", "https://dev.azure.com/org/proj/_git/repo")]
    [InlineData("/srv/git/local.git", null)]
    [InlineData("", null)]
    public void Remote_to_web_url(string remote, string? expected) => Assert.Equal(expected, GitWebUrl.FromRemote(remote));

    [Theory]
    [InlineData("https://github.com/o/r", "abc1234", "https://github.com/o/r/commit/abc1234")]
    [InlineData("https://gitlab.com/g/r", "abc1234", "https://gitlab.com/g/r/-/commit/abc1234")]
    [InlineData("https://bitbucket.org/o/r", "abc1234", "https://bitbucket.org/o/r/commits/abc1234")]
    [InlineData("https://github.com/o/r", "not-a-sha", null)]
    public void Commit_url(string web, string sha, string? expected) => Assert.Equal(expected, GitWebUrl.CommitUrl(web, sha));
}
