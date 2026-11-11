using Amc.Core.Domain;

namespace Amc.Daemon.Worktrees;

public enum CleanupMode { Off, Suggest, Auto }

/// <summary>
/// Configuration section "WorktreeCleanup" (docs/ARCHITECTURE.md, Worktree cleanup). Defaults: suggest only, 30 min
/// grace period for auto mode, remote branches are never deleted.
/// </summary>
public sealed class WorktreeCleanupOptions
{
    /// <summary>"suggest" (default): badge, attention item and a confirmed one-click cleanup. "auto": remove after
    /// <see cref="GracePeriodMinutes"/> when every safety check passes. "off": no detection, no cleanup.</summary>
    public string Mode { get; set; } = "suggest";
    /// <summary>Auto mode waits this long after the merge was detected.</summary>
    public double GracePeriodMinutes { get; set; } = 30;
    /// <summary>Also delete the branch on origin, only when its tip equals the merged PR's head. Off: GitHub usually deletes it.</summary>
    public bool DeleteRemoteBranch { get; set; }
    /// <summary>How often merged pull requests are listed per repository through gh (only while there are candidate worktrees).</summary>
    public double PollMinutes { get; set; } = 10;
    /// <summary>Ask GitHub through an installed, authenticated gh CLI. Overhive never reads or stores GitHub tokens.</summary>
    public bool UseGh { get; set; } = true;
    /// <summary>Auto mode also removes worktrees that git alone infers as merged ("likely merged"). Off by default.</summary>
    public bool AutoRemoveLikelyMerged { get; set; }
    /// <summary>
    /// Ignored paths that are safe to delete with a worktree because they can be regenerated: a directory name matching any
    /// path segment, or "*.ext" matching the file name. Empty means <see cref="DefaultRegenerableIgnored"/>; a configured
    /// list replaces the default. Any other ignored path (.env, *.pem, local config, data) blocks automatic removal and
    /// needs an explicit confirmation for a manual one.
    /// </summary>
    public List<string> RegenerableIgnored { get; set; } = [];

    public static readonly IReadOnlyList<string> DefaultRegenerableIgnored =
    [
        "node_modules", "bin", "obj", "dist", "build", "out", "target", ".next", ".nuxt", ".turbo", ".cache", "coverage",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".venv", "venv", "*.pyc", ".gradle", ".angular", ".svelte-kit",
        ".parcel-cache", ".vite", ".tox",
    ];

    public IReadOnlyList<string> EffectiveRegenerableIgnored => RegenerableIgnored.Count > 0 ? RegenerableIgnored : DefaultRegenerableIgnored;

    /// <summary>Whether an ignored path (relative, '/'-separated, directories with a trailing '/') can be regenerated.</summary>
    public bool IsRegenerable(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        var cmp = PathText.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var raw in EffectiveRegenerableIgnored)
        {
            var p = raw.Trim().Trim('/');
            if (p.Length == 0) continue;
            if (p.StartsWith("*.", StringComparison.Ordinal))
            {
                if (segments[^1].EndsWith(p[1..], cmp)) return true;
            }
            else if (segments.Any(s => s.Equals(p, cmp))) return true;
        }
        return false;
    }

    /// <summary>Repositories never checked nor cleaned: repository name, main worktree path or common git dir.</summary>
    public List<string> ExcludeRepositories { get; set; } = [];

    public CleanupMode EffectiveMode => Mode?.Trim().ToLowerInvariant() switch
    {
        "off" or "false" or "disabled" => CleanupMode.Off,
        "auto" => CleanupMode.Auto,
        _ => CleanupMode.Suggest,
    };

    public bool Excludes(Repository? repo) =>
        repo is not null && ExcludeRepositories.Any(x => x.Length > 0 &&
            (string.Equals(x, repo.Name, StringComparison.OrdinalIgnoreCase)
             || PathText.IsAbsolute(x) && (Same(x, repo.MainWorktree) || Same(x, repo.CommonDir))));

    private static bool Same(string a, string? b) => b is not null && PathText.Normalize(a).Equals(PathText.Normalize(b), PathText.Comparison);
}
