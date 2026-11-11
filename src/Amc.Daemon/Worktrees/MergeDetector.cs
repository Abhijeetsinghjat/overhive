using System.Text.Json;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Collectors.Git;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Worktrees;

public sealed record MergeCheckResult(int Candidates, int Merged, int LikelyMerged, bool GhUsed, string? GhError);

/// <summary>
/// Detects linked worktrees whose branch is merged and publishes <see cref="EventTypes.WorktreeMerged"/>.
/// <list type="number">
/// <item>GitHub through gh (observed): <c>gh pr list --state merged</c> per repository with a GitHub remote, at most
/// every <see cref="WorktreeCleanupOptions.PollMinutes"/>, and only while candidate worktrees exist. A pull request
/// matches when its head branch is the worktree's branch AND the worktree's HEAD is the PR head or an ancestor of it,
/// so a branch reused after the merge (new commits) is not merged.</item>
/// <item>git alone (inferred, "likely merged"), never fetching: the branch has commits of its own (reflog) and its tip
/// is contained in origin's default branch, or its upstream is gone and <c>git cherry</c> finds every commit in
/// origin's default branch by patch id.</item>
/// </list>
/// </summary>
public sealed class MergeDetector(GitCli git, IGitHubCli gh, StateStore state, IEventSink sink, IOptions<AmcOptions> options,
    TimeProvider clock, ILogger<MergeDetector> log)
{
    /// <summary>Worktrees looked up individually (<c>gh pr list --head</c>) per repository and poll, when not in the recent list.</summary>
    public const int MaxBranchLookups = 5;
    /// <summary>A forced check (dashboard/CLI) still waits this long between gh calls for one repository.</summary>
    public static readonly TimeSpan MinForcedInterval = TimeSpan.FromSeconds(30);
    /// <summary>After gh failed to start or is not authenticated, it is not tried again for this long.</summary>
    public static readonly TimeSpan GhRetryAfter = TimeSpan.FromMinutes(10);

    private sealed record Candidate(string Id, string Root, string Branch, string Head, WorktreeMerge? Merged);
    private sealed record RepoCandidates(Repository Repo, List<Candidate> Worktrees);
    public sealed record GhPr(int Number, string HeadRefName, string HeadRefOid, DateTimeOffset? MergedAt, string? Url);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly Dictionary<string, DateTimeOffset> _lastGhPoll = new(StringComparer.Ordinal);
    private DateTimeOffset _ghUnavailableUntil = DateTimeOffset.MinValue;
    private string? _ghError;
    private readonly SemaphoreSlim _running = new(1, 1);

    private WorktreeCleanupOptions Opt => options.Value.WorktreeCleanup;

    public async Task<MergeCheckResult> CheckAsync(bool force, CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        try { return await CheckCoreAsync(force, ct); }
        finally { _running.Release(); }
    }

    private async Task<MergeCheckResult> CheckCoreAsync(bool force, CancellationToken ct)
    {
        if (Opt.EffectiveMode == CleanupMode.Off) return new(0, 0, 0, false, null);
        var groups = state.Read(s => s.Worktrees.Values
            .Where(w => !w.Removed && !w.IsMain && !w.Detached && w.Branch is { Length: > 0 } && w.Head is { Length: > 0 })
            .GroupBy(w => w.RepositoryId)
            .Select(g => s.Repositories.TryGetValue(g.Key, out var r)
                ? new RepoCandidates(Copy(r), g.Select(w => new Candidate(w.Id, w.Root, w.Branch!, w.Head!, w.Merged)).ToList())
                : null)
            .Where(x => x is not null).Select(x => x!).ToList());

        int candidates = 0, merged = 0, likely = 0;
        var ghUsed = false;
        foreach (var g in groups)
        {
            if (Opt.Excludes(g.Repo)) continue;
            var workDir = g.Repo.MainWorktree is { } mw && Directory.Exists(mw) ? mw : g.Repo.CommonDir;
            if (!Directory.Exists(workDir)) continue;
            var defaultRef = await DefaultRemoteRefAsync(workDir, ct);
            var defaultBranch = defaultRef?["refs/remotes/origin/".Length..];
            var list = g.Worktrees.Where(c => c.Branch != defaultBranch && Directory.Exists(c.Root)).ToList();
            candidates += list.Count;
            if (list.Count == 0) continue; // no network call without candidates

            var found = new Dictionary<string, (GhPr Pr, Candidate C)>(StringComparer.Ordinal);
            var pending = list.Where(c => c.Merged is not { Source: MergeSources.GitHub }).ToList();
            if (pending.Count > 0 && Opt.UseGh && IsGitHub(g.Repo.WebUrl) && DueForGh(g.Repo.Id, force))
            {
                var prs = await GhMergedAsync(workDir, pending, ct);
                if (prs is not null)
                {
                    ghUsed = true;
                    foreach (var c in pending)
                        foreach (var pr in prs.Where(p => p.HeadRefName == c.Branch && p.HeadRefOid is { Length: > 0 }))
                            if (pr.HeadRefOid == c.Head || await IsAncestorAsync(c.Root, c.Head, pr.HeadRefOid, ct))
                            {
                                found[c.Id] = (pr, c);
                                break;
                            }
                }
            }
            foreach (var (pr, c) in found.Values)
            {
                merged++;
                await PublishAsync(g.Repo, c, new WorktreeMergedData(c.Root, c.Branch, c.Head, MergeSources.GitHub, pr.Number, pr.Url,
                    pr.HeadRefOid, pr.MergedAt), Evidence.Observed, ct);
            }
            if (defaultRef is null) continue;
            foreach (var c in list.Where(c => !found.ContainsKey(c.Id) && c.Merged is null))
            {
                if (await InferMergedAsync(c, defaultRef, ct) is not { } reason) continue;
                likely++;
                await PublishAsync(g.Repo, c, new WorktreeMergedData(c.Root, c.Branch, c.Head, MergeSources.Git, Reason: reason), Evidence.Inferred, ct);
            }
            merged += list.Count(c => c.Merged is { Source: MergeSources.GitHub } && !found.ContainsKey(c.Id));
            likely += list.Count(c => c.Merged is { Source: MergeSources.Git } && !found.ContainsKey(c.Id));
        }
        return new(candidates, merged, likely, ghUsed, _ghError);
    }

    private static Repository Copy(Repository r) => new() { Id = r.Id, Name = r.Name, CommonDir = r.CommonDir, MainWorktree = r.MainWorktree, WebUrl = r.WebUrl };

    public static bool IsGitHub(string? webUrl) =>
        Uri.TryCreate(webUrl, UriKind.Absolute, out var u) && u.Host.Contains("github", StringComparison.OrdinalIgnoreCase);

    private bool DueForGh(string repoId, bool force)
    {
        var now = clock.GetUtcNow();
        if (now < _ghUnavailableUntil && !force) return false;
        var interval = force ? MinForcedInterval : TimeSpan.FromMinutes(Math.Max(1, Opt.PollMinutes));
        if (_lastGhPoll.TryGetValue(repoId, out var last) && now - last < interval) return false;
        _lastGhPoll[repoId] = now;
        return true;
    }

    private const string Fields = "number,headRefName,headRefOid,mergedAt,url";

    /// <summary>Recent merged PRs plus individual lookups for candidates not among them. Null when gh is unavailable.</summary>
    private async Task<List<GhPr>?> GhMergedAsync(string workDir, List<Candidate> pending, CancellationToken ct)
    {
        var r = await gh.RunAsync(workDir, ["pr", "list", "--state", "merged", "--json", Fields, "--limit", "100"], ct);
        if (!r.Ok)
        {
            _ghError = r.NotInstalled ? "gh is not installed" : $"gh failed: {FirstLine(r.Stderr)}";
            _ghUnavailableUntil = clock.GetUtcNow() + GhRetryAfter;
            log.LogDebug("Merged PR lookup through gh unavailable in {Dir}: {Error}", workDir, _ghError);
            return null;
        }
        _ghError = null;
        var prs = Parse(r.Stdout);
        foreach (var c in pending.Where(c => !prs.Any(p => p.HeadRefName == c.Branch)).Take(MaxBranchLookups))
        {
            var one = await gh.RunAsync(workDir, ["pr", "list", "--state", "merged", "--head", c.Branch, "--json", Fields, "--limit", "10"], ct);
            if (one.Ok) prs.AddRange(Parse(one.Stdout));
        }
        return prs;
    }

    public static List<GhPr> Parse(string json)
    {
        try { return JsonSerializer.Deserialize<List<GhPr>>(json, JsonOpts) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    /// <summary>origin's default branch as a remote-tracking ref (origin/HEAD, else origin/main or origin/master); never fetched.</summary>
    public async Task<string?> DefaultRemoteRefAsync(string workDir, CancellationToken ct)
    {
        var sym = await git.RunAsync(workDir, ["symbolic-ref", "-q", "refs/remotes/origin/HEAD"], ct);
        if (sym.Ok && sym.Stdout.Trim() is { Length: > 0 } s && s.StartsWith("refs/remotes/origin/", StringComparison.Ordinal)) return s;
        foreach (var name in new[] { "refs/remotes/origin/main", "refs/remotes/origin/master" })
            if ((await git.RunAsync(workDir, ["rev-parse", "--verify", "-q", name], ct)).Ok) return name;
        return null;
    }

    private async Task<bool> IsAncestorAsync(string dir, string a, string b, CancellationToken ct) =>
        (await git.RunAsync(dir, ["merge-base", "--is-ancestor", a, b], ct)).ExitCode == 0;

    /// <summary>The reason git alone considers the branch merged, or null.</summary>
    private async Task<string?> InferMergedAsync(Candidate c, string defaultRef, CancellationToken ct)
    {
        var shortRef = defaultRef["refs/remotes/".Length..];
        // A branch with no commits of its own (fresh from the default branch) is contained in it, but it is not merged.
        var reflog = await git.RunAsync(c.Root, ["log", "-g", "--format=%H", "refs/heads/" + c.Branch, "--"], ct);
        var created = reflog.Ok ? reflog.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() : null;
        if (created is null || created == c.Head) return null;

        if (await IsAncestorAsync(c.Root, c.Head, defaultRef, ct)) return $"the branch tip is contained in {shortRef}";

        var track = await git.RunAsync(c.Root, ["for-each-ref", "--format=%(upstream:track)", "refs/heads/" + c.Branch], ct);
        if (!track.Ok || !track.Stdout.Contains("[gone]", StringComparison.Ordinal)) return null;
        var cherry = await git.RunAsync(c.Root, ["cherry", defaultRef, c.Head], ct);
        if (!cherry.Ok) return null;
        var lines = cherry.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 && lines.All(l => l.StartsWith('-'))
            ? $"its upstream branch is gone and every commit is in {shortRef} (by patch id)"
            : null;
    }

    private async Task PublishAsync(Repository repo, Candidate c, WorktreeMergedData d, Evidence evidence, CancellationToken ct)
    {
        if (c.Merged is { } m && m.Head == d.Head && m.Source == d.Source && m.Pr == d.Pr) return;
        log.LogInformation("Worktree {Root} ({Branch}) is {What}", c.Root, c.Branch,
            d.Pr is { } pr ? $"merged (PR #{pr}, observed via gh)" : $"likely merged ({d.Reason})");
        await sink.PublishAndWaitAsync(new AgentEvent
        {
            Type = EventTypes.WorktreeMerged, Source = EventSources.Git, Evidence = evidence,
            RepositoryId = repo.Id, WorktreeId = c.Id,
            DedupeKey = $"merged:{c.Id}:{d.Head}:{d.Source}:{d.Pr}",
            Data = Json.ToObject(d),
        }, ct);
    }
}
