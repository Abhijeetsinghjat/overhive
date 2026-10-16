using System.Text;
using Amc.Core.Conflicts;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Collectors.Git;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Conflicts;

/// <summary>Bounds of the conflict analysis (it runs inside the daemon and must stay cheap).</summary>
public sealed record ConflictAnalyzerSettings
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>A file must be quiet this long (no new change event) before it is analysed.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxConflictsPerTick { get; init; } = 8;
    /// <summary>Only the first N worktrees of a conflict are compared (pairs grow quadratically).</summary>
    public int MaxWorktrees { get; init; } = 4;
    public long MaxFileBytes { get; init; } = 1024 * 1024;
    public int MaxDiffChars { get; init; } = 512 * 1024;
    public TimeSpan GitTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int Margin { get; init; } = OverlapMath.DefaultMargin;
    /// <summary>Confirm committed state with `git merge-tree --write-tree` (writes unreferenced objects that `git gc` removes).</summary>
    public bool UseMergeTree { get; init; } = true;
}

/// <summary>
/// Hunk-level analysis of cross-worktree conflicts. For each pair of worktrees that changed the same
/// path it diffs each worktree (committed + uncommitted changes) against the pair's merge base with
/// `git diff -U0`, compares the changed base-line ranges (<see cref="OverlapMath"/>) and, when both
/// heads changed the file, confirms the committed state with `git merge-tree`. Only conflicts the
/// detector already flagged are analysed, only when their inputs changed, after a quiet period.
/// Results are published as observed <see cref="EventTypes.ConflictAnalyzed"/> events.
/// </summary>
public sealed class ConflictAnalyzer(
    GitCli git, StateStore state, IEventSink sink, HealthRegistry health, IOptions<AmcOptions> options,
    ConflictWarner warner, TimeProvider clock, ILogger<ConflictAnalyzer> log, ConflictAnalyzerSettings? settings = null) : BackgroundService
{
    public const string HealthName = "conflict-analysis";
    private readonly ConflictAnalyzerSettings _s = settings ?? new();

    private sealed record Side(string WorktreeId, string Root, string? Head, string? GitStatus, string? OldPath);
    private sealed record Candidate(string Key, string Path, List<Side> Sides, string InputKey, DateTimeOffset LastChange, int Total);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        health.Register(HealthName, _s.Interval * 20);
        using var timer = new PeriodicTimer(_s.Interval, clock);
        do
        {
            try
            {
                var n = await RunOnceAsync(ct);
                health.Ok(HealthName, n == 0 ? "up to date" : $"analysed {n} conflict(s)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Conflict analysis failed");
                health.Degraded(HealthName, ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One pass: analyses due conflicts, then (if enabled) sends automatic warnings. Returns the number analysed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct, bool ignoreDebounce = false)
    {
        var now = clock.GetUtcNow();
        var due = state.Read(Candidates)
            .Where(c => ignoreDebounce || now - c.LastChange >= _s.Debounce)
            .Take(_s.MaxConflictsPerTick).ToList();
        foreach (var c in due)
        {
            var overlap = await AnalyzeAsync(c, ct);
            await sink.PublishAndWaitAsync(new AgentEvent
            {
                Type = EventTypes.ConflictAnalyzed, Source = EventSources.Git, Evidence = Evidence.Observed,
                FilePath = c.Path,
                Data = Json.ToObject(new ConflictAnalysisData(c.Key, overlap)),
            }, ct);
        }
        if (options.Value.AutoWarnConflicts)
        {
            var overlapping = state.Read(s => s.Conflicts.Values
                .Where(c => c.Insight?.Overlap?.Verdict == OverlapVerdict.Overlapping).Select(c => c.Key).ToList());
            foreach (var key in overlapping) await warner.AutoWarnAsync(key, ct);
        }
        return due.Count;
    }

    /// <summary>Cross-worktree conflicts whose inputs differ from the last analysis.</summary>
    private List<Candidate> Candidates(Amc.Core.State.WorldState s)
    {
        var list = new List<Candidate>();
        foreach (var c in s.Conflicts.Values)
        {
            if (c.Kind != ConflictKind.CrossWorktree || c.Path is null) continue;
            var sides = new List<Side>();
            var key = new StringBuilder();
            var last = DateTimeOffset.MinValue;
            foreach (var wtId in c.WorktreeIds)
            {
                if (!s.Worktrees.TryGetValue(wtId, out var w) || w.Removed) continue;
                if (!s.Files.TryGetValue(Ids.FileKey(wtId, c.Path), out var f)) continue;
                sides.Add(new Side(wtId, w.Root, w.Head, f.GitStatus, f.OldPath));
                key.Append(wtId).Append(':').Append(w.Head).Append(':').Append(f.GitStatus).Append(':').Append(f.LastEventSeq)
                    .Append(':').Append(f.Added).Append(':').Append(f.Removed).Append(';');
                if (f.LastChangedAt > last) last = f.LastChangedAt;
            }
            if (sides.Count < 2) continue;
            key.Append("m=").Append(_s.Margin);
            var inputKey = key.ToString();
            if (c.Insight?.Overlap?.InputKey == inputKey) continue;
            list.Add(new Candidate(c.Key, c.Path, sides.Take(_s.MaxWorktrees).ToList(), inputKey, last, sides.Count));
        }
        return list.OrderBy(c => c.LastChange).ToList();
    }

    private async Task<ConflictOverlap> AnalyzeAsync(Candidate c, CancellationToken ct)
    {
        health.Increment(HealthName, "analyses");
        var pairs = new List<OverlapPair>();
        var diffs = new Dictionary<(string Wt, string Base), (FileDiff? Diff, string? Note)>();
        for (var i = 0; i < c.Sides.Count; i++)
            for (var j = i + 1; j < c.Sides.Count; j++)
                pairs.Add(await ComparePairAsync(c.Path, c.Sides[i], c.Sides[j], diffs, ct));
        return new ConflictOverlap
        {
            Verdict = OverlapMath.Verdict(pairs), AnalyzedAt = clock.GetUtcNow(), InputKey = c.InputKey, Margin = _s.Margin,
            Pairs = pairs,
            Note = c.Total > c.Sides.Count ? $"only the first {c.Sides.Count} of {c.Total} worktrees were compared" : null,
        };
    }

    private async Task<OverlapPair> ComparePairAsync(string path, Side a, Side b,
        Dictionary<(string, string), (FileDiff?, string?)> cache, CancellationToken ct)
    {
        OverlapPair Fail(string? mergeBase, string note) => new(a.WorktreeId, b.WorktreeId, mergeBase, [], [], [], [], [], false, Note: note);
        if (a.Head is null || b.Head is null) return Fail(null, "a worktree has no commit (HEAD) yet");
        var mb = await Git(a.Root, ["merge-base", a.Head, b.Head], ct, 4096);
        if (!mb.Ok) return Fail(null, mb.TimedOut ? "git merge-base timed out" : "the branches have no merge base");
        var mergeBase = mb.Stdout.Trim();

        var (da, na) = await DiffCachedAsync(a, mergeBase, path, cache, ct);
        var (db, nb) = await DiffCachedAsync(b, mergeBase, path, cache, ct);
        if (da is null || db is null) return Fail(mergeBase, na ?? nb ?? "diff unavailable");
        var pair = OverlapMath.Compare(a.WorktreeId, b.WorktreeId, mergeBase, da, db, _s.Margin);
        return _s.UseMergeTree ? pair with { CommittedConflict = await MergeTreeAsync(a, b, mergeBase, path, ct) } : pair;
    }

    private async Task<(FileDiff?, string?)> DiffCachedAsync(Side side, string mergeBase, string path,
        Dictionary<(string, string), (FileDiff?, string?)> cache, CancellationToken ct)
    {
        if (cache.TryGetValue((side.WorktreeId, mergeBase), out var hit)) return hit;
        var r = await DiffAsync(side, mergeBase, path, ct);
        cache[(side.WorktreeId, mergeBase)] = r;
        return r;
    }

    /// <summary>The worktree's file (working tree, so uncommitted edits count) against the merge base, as hunks.</summary>
    private async Task<(FileDiff?, string?)> DiffAsync(Side side, string mergeBase, string path, CancellationToken ct)
    {
        if (!GitPaths.TryResolve(side.Root, path, out var rel, out var full, out _)) return (null, "path is not inside the worktree");
        if (File.Exists(full))
        {
            var info = new FileInfo(full);
            if (info.Length > _s.MaxFileBytes) return (null, $"file larger than {_s.MaxFileBytes / 1024} KB: not analysed");
            if (side.GitStatus == "?")
            {
                // Untracked: git diff does not show it; the whole file is new.
                var bytes = await File.ReadAllBytesAsync(full, ct);
                if (GitParsers.IsBinary(bytes)) return (new FileDiff(null, rel, true, false, true, []), null);
                var lines = bytes.Count(b => b == (byte)'\n') + (bytes.Length > 0 && bytes[^1] != (byte)'\n' ? 1 : 0);
                return (FileDiff.Added(rel, lines), null);
            }
        }
        List<string> args = ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "-U0", "-M", mergeBase, "--", rel];
        if (side.OldPath is { Length: > 0 } old && old != rel && GitPaths.TryResolve(side.Root, old, out var oldRel, out _, out _)) args.Add(oldRel);
        var d = await Git(side.Root, args, ct, _s.MaxDiffChars);
        if (d.Truncated) return (null, "diff too large: not analysed");
        if (!d.Ok) return (null, d.TimedOut ? "git diff timed out" : "git diff failed");
        return (HunkParser.ForPath(HunkParser.Parse(d.Stdout), rel), null);
    }

    /// <summary>
    /// Committed state only: when both heads changed the file since the merge base, asks `git merge-tree`
    /// whether merging them conflicts on this path. Null when only one side (or none) committed a change to it,
    /// or when merge-tree is not available (git &lt; 2.38).
    /// </summary>
    private async Task<bool?> MergeTreeAsync(Side a, Side b, string mergeBase, string path, CancellationToken ct)
    {
        var blobs = await Git(a.Root, ["rev-parse", $"{mergeBase}:{path}", $"{a.Head}:{path}", $"{b.Head}:{path}"], ct, 4096);
        var ids = blobs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (!blobs.Ok || ids.Length != 3) return null;
        if (ids[1] == ids[0] || ids[2] == ids[0]) return null; // not both sides committed a change: nothing to confirm
        if (ids[1] == ids[2]) return false;                 // the same committed content on both sides
        var m = await Git(a.Root, ["merge-tree", "--write-tree", "--name-only", "--no-messages", a.Head!, b.Head!], ct, 256 * 1024);
        if (m.TimedOut || m.Truncated) return null;
        return m.ExitCode switch
        {
            0 => false,
            1 => m.Stdout.Split('\n').Skip(1).TakeWhile(l => l.Length > 0).Contains(path),
            _ => null,
        };
    }

    private Task<GitResult> Git(string root, List<string> args, CancellationToken ct, int maxOutput) =>
        git.RunAsync(root, args, ct, maxOutput, _s.GitTimeout);
}
