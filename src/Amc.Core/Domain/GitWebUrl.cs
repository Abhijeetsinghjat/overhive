using System.Text.RegularExpressions;

namespace Amc.Core.Domain;

/// <summary>
/// Turns a git remote URL into the repository's web URL and builds commit links.
/// Handles https and ssh forms for GitHub, GitLab, Bitbucket and Azure DevOps; credentials in
/// the remote are never carried over. Returns null for anything that is not a recognisable web host.
/// </summary>
public static partial class GitWebUrl
{
    public static string? FromRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        var r = remote.Trim();

        // Azure DevOps ssh: git@ssh.dev.azure.com:v3/org/project/repo
        if (AzureSsh().Match(r) is { Success: true } az)
            return $"https://dev.azure.com/{az.Groups["org"].Value}/{az.Groups["proj"].Value}/_git/{az.Groups["repo"].Value}";

        string host, path;
        if (ScpLike().Match(r) is { Success: true } scp)               // git@github.com:owner/repo.git
        {
            host = scp.Groups["host"].Value; path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(r, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" or "ssh" or "git")
        {
            host = u.Host; path = u.AbsolutePath.TrimStart('/');          // userinfo (tokens) dropped here
        }
        else return null;

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (path.Length == 0 || host.Length == 0) return null;
        if (host.StartsWith("ssh.", StringComparison.OrdinalIgnoreCase) && host.Contains("github", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        return $"https://{host}/{path}";
    }

    public static string? CommitUrl(string? webUrl, string? sha)
    {
        if (webUrl is null || string.IsNullOrEmpty(sha) || !Sha().IsMatch(sha)) return null;
        var host = new Uri(webUrl).Host;
        if (host.Contains("gitlab", StringComparison.OrdinalIgnoreCase)) return $"{webUrl}/-/commit/{sha}";
        if (host.Contains("bitbucket", StringComparison.OrdinalIgnoreCase)) return $"{webUrl}/commits/{sha}";
        return $"{webUrl}/commit/{sha}"; // GitHub, Azure DevOps (/_git/repo/commit/sha), Gitea, …
    }

    [GeneratedRegex(@"^[^@\s]+@(?<host>[^:\s/]+):(?<path>[^\s]+)$")]
    private static partial Regex ScpLike();
    [GeneratedRegex(@"^[^@\s]+@ssh\.dev\.azure\.com:v3/(?<org>[^/]+)/(?<proj>[^/]+)/(?<repo>[^/\s]+)$")]
    private static partial Regex AzureSsh();
    [GeneratedRegex(@"^[0-9a-fA-F]{7,64}$")]
    private static partial Regex Sha();
}
