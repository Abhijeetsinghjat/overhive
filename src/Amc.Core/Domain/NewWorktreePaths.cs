namespace Amc.Core.Domain;

/// <summary>
/// Pure path rules for "start a session in a new worktree" (no IO): the default location of a new worktree, the
/// folder name derived from a branch, the quick pre-checks done before <c>git check-ref-format</c> and the Windows
/// long-path warning. The internal overloads take the OS as a flag so both forms are tested on every OS.
/// </summary>
public static class NewWorktreePaths
{
    /// <summary>Placeholder in <c>NewWorktree:Root</c> replaced by the repository's folder name.</summary>
    public const string RepoPlaceholder = "{repo}";

    /// <summary>Paths longer than this get a warning on Windows (MAX_PATH is 260 and files inside need room).</summary>
    public const int LongPathWarnAt = 240;

    /// <summary>Upper bound for a branch name typed into the dialog or CLI (git has no limit; file systems do).</summary>
    public const int MaxBranchLength = 200;

    /// <summary>
    /// Default location of a new worktree for <paramref name="branch"/> of the repository whose main worktree is
    /// <paramref name="mainRoot"/>: <c>&lt;repo-parent&gt;/&lt;repo&gt;.worktrees/&lt;branch-slug&gt;</c>, or
    /// <c>&lt;root&gt;/&lt;branch-slug&gt;</c> when <paramref name="rootTemplate"/> (config <c>NewWorktree:Root</c>, may
    /// start with <c>~</c> and contain <c>{repo}</c>) is set. Returns null when the template is not an absolute path.
    /// </summary>
    public static string? DefaultPath(string mainRoot, string branch, string? rootTemplate, string home) =>
        DefaultPath(mainRoot, branch, rootTemplate, home, PathText.Windows);

    internal static string? DefaultPath(string mainRoot, string branch, string? rootTemplate, string home, bool windows)
    {
        mainRoot = PathText.Normalize(mainRoot, windows);
        var repo = RepoName(mainRoot, windows);
        var slug = BranchSlug(branch, windows);
        if (string.IsNullOrWhiteSpace(rootTemplate))
        {
            var parent = PathText.Parent(mainRoot, windows) ?? mainRoot;
            return PathText.Combine(parent, repo + ".worktrees/" + slug, windows);
        }
        var root = rootTemplate.Trim().Replace(RepoPlaceholder, repo, StringComparison.Ordinal);
        if (root == "~" || root.StartsWith("~/", StringComparison.Ordinal) || windows && root.StartsWith("~\\", StringComparison.Ordinal))
            root = PathText.Normalize(home, windows) + "/" + root[1..];
        if (!PathText.IsAbsolute(root, windows)) return null;
        return PathText.Combine(root, slug, windows);
    }

    /// <summary>Folder name of a repository's main worktree ("/work/app" → "app").</summary>
    public static string RepoName(string mainRoot) => RepoName(mainRoot, PathText.Windows);

    internal static string RepoName(string mainRoot, bool windows)
    {
        var n = PathText.Normalize(mainRoot, windows);
        var i = n.LastIndexOf('/');
        var name = i >= 0 ? n[(i + 1)..] : n;
        return name.Length == 0 || name.EndsWith(':') ? "repo" : name;
    }

    /// <summary>
    /// One folder name for a branch: "/" becomes "-" ("feat/login" → "feat-login"). On Windows the characters git
    /// allows in ref names but Windows does not allow in file names (<c>&lt; &gt; | "</c>) also become "-".
    /// </summary>
    public static string BranchSlug(string branch) => BranchSlug(branch, PathText.Windows);

    internal static string BranchSlug(string branch, bool windows)
    {
        var chars = branch.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is '/' or '\\' || windows && chars[i] is '<' or '>' or '|' or '"' or ':')
                chars[i] = '-';
        var s = new string(chars).Trim('-', '.', ' ');
        return s.Length == 0 ? "worktree" : s;
    }

    /// <summary>
    /// Cheap checks before asking git (<c>git check-ref-format --branch</c> decides): returns an error or null.
    /// Rejects what git would misread as an option or a "previous branch" shorthand.
    /// </summary>
    public static string? BranchPrecheck(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return "Enter a branch name";
        if (branch.Length > MaxBranchLength) return $"Branch names are limited to {MaxBranchLength} characters";
        if (branch.Any(char.IsControl)) return "The branch name contains control characters";
        if (branch != branch.Trim()) return "The branch name starts or ends with a space";
        if (branch.StartsWith('-')) return "A branch name cannot start with '-'";
        if (branch.StartsWith('@') || branch.Contains("@{", StringComparison.Ordinal)) return "A branch name cannot start with '@' or contain '@{'";
        if (branch == "HEAD") return "HEAD is not a branch name";
        return null;
    }

    /// <summary>A base ref typed by the user: plain text git can resolve, never an option.</summary>
    public static string? BasePrecheck(string? baseRef)
    {
        if (string.IsNullOrWhiteSpace(baseRef)) return "Choose a base branch or commit";
        if (baseRef.Length > 256 || baseRef.Any(char.IsControl) || baseRef != baseRef.Trim()) return "Invalid base";
        if (baseRef.StartsWith('-')) return "A base cannot start with '-'";
        return null;
    }

    /// <summary>Warning for a path Windows tools may not handle (null when fine or not on Windows).</summary>
    public static string? LongPathWarning(string path) => LongPathWarning(path, PathText.Windows);

    internal static string? LongPathWarning(string path, bool windows) =>
        windows && path.Length > LongPathWarnAt
            ? $"The path is {path.Length} characters long; Windows tools often fail above 260 characters (including files inside). " +
              "Choose a shorter location, or enable long paths (git config core.longpaths true and the LongPathsEnabled policy)."
            : null;
}
