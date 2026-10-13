using System.Collections.Concurrent;
using System.Threading.Channels;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Collectors.Git;

public sealed class GitSettings
{
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(750);
    /// <summary>Continuous triggers cannot postpone a refresh longer than this.</summary>
    public TimeSpan MaxDebounce { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ActiveRescan { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleRescan { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ActiveWindow { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan NegativeCacheTtl { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxFileEventsPerRefresh { get; init; } = 5000;
    public int MaxUntrackedLineCounts { get; init; } = 500;
    public long MaxUntrackedLineCountBytes { get; init; } = 256 * 1024;
    public int MaxCommitsPerRefresh { get; init; } = 50;
    public bool Watch { get; init; } = true;
    public bool ScanOnStart { get; init; } = true;
}

/// <summary>
/// Git / worktree collector: discovers repositories (workspace roots + on demand), watches only
/// Git metadata (one inotify instance, non-recursive), refreshes status on debounced triggers and
/// emits deltas (git.file_status, worktree.*, git.*), attributing them (inferred) to an agent
/// only when it was the single agent active in that worktree. Also the <see cref="IWorktreeIndex"/>.
/// </summary>
public sealed class GitCollector : BackgroundService, IWorktreeIndex
{
    private sealed class RepoInfo(string commonDir)
    {
        public string CommonDir { get; } = commonDir;
        public string Id { get; } = Ids.Repository(commonDir);
        public string RefsHeads => Path.Combine(CommonDir, "refs", "heads");
        public string WorktreesDir => Path.Combine(CommonDir, "worktrees");
        public DateTimeOffset LastListAt;
    }

    private sealed class Tracker
    {
        public required string Id { get; init; }
        public required string Root { get; init; }
        public required RepoInfo Repo { get; init; }
        public string? GitDir;
        public bool IsMain, Locked, Prunable, Deleted;
        public readonly SemaphoreSlim Gate = new(1, 1);
        /// <summary>Last emitted per-path state (dirty paths only); null until seeded from WorldState.</summary>
        public Dictionary<string, FileState>? Files;
        public WorktreeData? Summary;
        public HashSet<string> Unmerged = [];
        public DateTimeOffset LastRefreshAt = DateTimeOffset.UtcNow;
    }

    private static readonly HashSet<string> SkipDirs = new(StringComparer.Ordinal)
        { "node_modules", "bin", "obj", "target", "dist", "build", "out", "vendor", "packages", "__pycache__", "venv" };

    private readonly GitCli _git;
    private readonly IEventSink _sink;
    private readonly StateStore _state;
    private readonly HealthRegistry _health;
    private readonly AmcOptions _options;
    private readonly GitSettings _settings;
    private readonly ILogger<GitCollector> _log;
    private readonly ActivityWindows _activity = new();
    private readonly ConcurrentDictionary<string, RepoInfo> _repos = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Tracker> _trackers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _discovering = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<string?>> _probing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _negative = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Timer Timer, DateTimeOffset First)> _pending = new(StringComparer.Ordinal);
    private readonly Channel<string> _work = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _stopping = new();
    private Inotify? _inotify;
    private long _refreshes;

    public GitCollector(GitCli git, IEventSink sink, StateStore state, HealthRegistry health, IOptions<AmcOptions> options,
        ILogger<GitCollector> log, GitSettings? settings = null)
    {
        _git = git; _sink = sink; _state = state; _health = health; _options = options.Value; _log = log;
        _settings = settings ?? new GitSettings();
        _health.Register(GitCli.HealthName, _settings.Tick * 3 + TimeSpan.FromSeconds(30));
        if (_settings.Watch)
        {
            _inotify = Inotify.TryCreate(out var error);
            if (_inotify is null) _log.LogWarning("Git metadata watching unavailable ({Error}); relying on periodic rescans", error);
            else _inotify.Changed += OnFsEvent;
        }
    }

    public ActivityWindows Activity => _activity;
    public int WatchCount => _inotify?.Count ?? 0;

    // ---- IWorktreeIndex -------------------------------------------------------------

    public async ValueTask<WorktreeHit?> ResolveAsync(string absolutePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(absolutePath) || !absolutePath.StartsWith('/') || absolutePath.Contains('\0')) return null;
        var path = WorktreeResolver.Normalize(absolutePath);
        if (Lookup(path) is { } hit) return hit;

        var dir = NearestExistingDirectory(path);
        if (dir is null) return null;
        if (_negative.TryGetValue(dir, out var until) && until > DateTimeOffset.UtcNow) return null;

        var probe = _probing.GetOrAdd(dir, d => ProbeAsync(d, _stopping.Token));
        string? commonDir;
        try { commonDir = await probe.WaitAsync(ct); }
        finally { _probing.TryRemove(KeyValuePair.Create(dir, probe)); }
        if (commonDir is null) { _negative[dir] = DateTimeOffset.UtcNow + _settings.NegativeCacheTtl; return null; }

        await DiscoverRepositoryAsync(commonDir, ct);
        if (Lookup(path) is { } h1) return h1;
        // The path may reach the worktree through a symlink: resolve and retry.
        if (GitPaths.RealPath(path) is { } real && real != path && Lookup(real) is { } h2) return h2;
        _negative[dir] = DateTimeOffset.UtcNow + _settings.NegativeCacheTtl;
        return null;
    }

    public void NotifyActivity(WorktreeActivity activity)
    {
        _activity.Record(activity);
        if (activity.Kind is ActivityKinds.Write or ActivityKinds.Shell && _trackers.ContainsKey(activity.WorktreeId))
            Schedule(activity.WorktreeId, _settings.Debounce);
    }

    private WorktreeHit? Lookup(string path)
    {
        if (_state.Resolver.Resolve(path) is not { } r || r.Worktree.Removed) return null;
        return new WorktreeHit(r.Worktree.Id, r.Worktree.RepositoryId, r.Worktree.Root, r.RelativePath);
    }

    private static string? NearestExistingDirectory(string path)
    {
        for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if (Directory.Exists(p)) return p;
        return null;
    }

    /// <summary>Returns the absolute common git dir of the repository containing a working-tree directory, or null.</summary>
    private async Task<string?> ProbeAsync(string dir, CancellationToken ct)
    {
        var r = await _git.RunAsync(dir, ["rev-parse", "--path-format=absolute", "--git-common-dir", "--show-toplevel"], ct);
        if (!r.Ok) return null;
        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length >= 2 ? WorktreeResolver.Normalize(lines[0]) : null;
    }

    // ---- lifecycle --------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => _stopping.Cancel());
        var workers = Enumerable.Range(0, 3).Select(_ => WorkerAsync(ct)).ToList();
        try
        {
            if (_settings.ScanOnStart) await ScanAsync(ct);
            _health.Ok(GitCli.HealthName, Detail());
            using var timer = new PeriodicTimer(_settings.Tick);
            while (await timer.WaitForNextTickAsync(ct)) Tick();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Git collector failed");
            _health.Failed(GitCli.HealthName, ex.Message);
        }
        await Task.WhenAll(workers);
    }

    public override void Dispose()
    {
        _stopping.Cancel();
        _inotify?.Dispose();
        lock (_pending) { foreach (var (t, _) in _pending.Values) t.Dispose(); _pending.Clear(); }
        base.Dispose();
    }

    /// <summary>Discovers repositories under the workspace roots and re-checks those already known from the event log.</summary>
    public async Task ScanAsync(CancellationToken ct)
    {
        var commonDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (repoDir, existed) in _state.Read(s => s.Repositories.Values.Select(r => (r.CommonDir, Directory.Exists(r.CommonDir))).ToList()))
        {
            if (existed) commonDirs.Add(repoDir);
            else await MarkRepositoryGoneAsync(repoDir, ct);
        }
        foreach (var dir in FindWorkingTrees(_options.EffectiveWorkspaceRoots, _options.WorkspaceScanDepth))
            if (await ProbeAsync(dir, ct) is { } cd) commonDirs.Add(cd);
        foreach (var cd in commonDirs) await DiscoverRepositoryAsync(cd, ct);
        _log.LogInformation("Git discovery: {Repos} repositories, {Worktrees} worktrees, {Watches} watches",
            _repos.Count, _trackers.Values.Count(t => !t.Deleted), WatchCount);
    }

    /// <summary>Directories (within depth) that contain a .git entry. Does not descend into found worktrees, hidden or build dirs.</summary>
    public static IEnumerable<string> FindWorkingTrees(IEnumerable<string> roots, int depth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        foreach (var r in roots) if (Directory.Exists(r)) queue.Enqueue((Path.GetFullPath(r), 0));
        while (queue.Count > 0)
        {
            var (dir, d) = queue.Dequeue();
            if (Path.Exists(Path.Combine(dir, ".git"))) { yield return dir; continue; }
            if (d >= depth) continue;
            IEnumerable<DirectoryInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions
                    { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var c in children)
                if (!c.Name.StartsWith('.') && !SkipDirs.Contains(c.Name)) queue.Enqueue((c.FullName, d + 1));
        }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var key in _work.Reader.ReadAllAsync(ct))
            {
                try
                {
                    if (key.StartsWith("list:", StringComparison.Ordinal)) await DiscoverRepositoryAsync(key[5..], ct);
                    else await RefreshAsync(key, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _health.Increment(GitCli.HealthName, "git_errors");
                    _log.LogWarning(ex, "Git work item {Key} failed", key);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        var since = now - _settings.ActiveWindow;
        var activeByAgents = _state.Read(s => s.Worktrees.Values
            .Where(w => w.Agents.Any(kv => kv.Value >= since && s.Agents.TryGetValue(kv.Key, out var a) && !a.IsTerminal))
            .Select(w => w.Id).ToHashSet());
        foreach (var t in _trackers.Values.Where(t => !t.Deleted))
        {
            var active = activeByAgents.Contains(t.Id) || _activity.LastActivity(t.Id) >= since;
            if (now - t.LastRefreshAt >= (active ? _settings.ActiveRescan : _settings.IdleRescan)) Schedule(t.Id, TimeSpan.Zero);
        }
        foreach (var r in _repos.Values)
            if (now - r.LastListAt >= _settings.IdleRescan) Schedule("list:" + r.CommonDir, TimeSpan.Zero);
        foreach (var (dir, until) in _negative) if (until <= now) _negative.TryRemove(dir, out _);
        _activity.Prune(now);
        _health.Ok(GitCli.HealthName, Detail());
    }

    private string Detail()
    {
        var wts = _trackers.Values.Count(t => !t.Deleted);
        _health.SetCounter(GitCli.HealthName, "repos", _repos.Count);
        _health.SetCounter(GitCli.HealthName, "worktrees", wts);
        _health.SetCounter(GitCli.HealthName, "refreshes", Interlocked.Read(ref _refreshes));
        _health.SetCounter(GitCli.HealthName, "watches", WatchCount);
        return $"{_repos.Count} repositories, {wts} worktrees" + (_inotify is null ? " (polling)" : "");
    }

    // ---- scheduling (per-key trailing debounce with a max delay) ----------------------

    private void Schedule(string key, TimeSpan delay)
    {
        lock (_pending)
        {
            if (_pending.TryGetValue(key, out var p))
            {
                if (DateTimeOffset.UtcNow - p.First + delay < _settings.MaxDebounce) p.Timer.Change(delay, Timeout.InfiniteTimeSpan);
                return;
            }
            var timer = new Timer(_ => Fire(key));
            _pending[key] = (timer, DateTimeOffset.UtcNow);
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire(string key)
    {
        lock (_pending) { if (_pending.Remove(key, out var p)) p.Timer.Dispose(); }
        _work.Writer.TryWrite(key);
    }

    // ---- watching ---------------------------------------------------------------------

    private void Watch(RepoInfo repo, Tracker t)
    {
        if (_inotify is null) return;
        if (t.GitDir is not null) _inotify.Add(t.GitDir);
        _inotify.Add(repo.CommonDir);
        if (Directory.Exists(repo.WorktreesDir)) _inotify.Add(repo.WorktreesDir);
        if (!_inotify.IsWatched(repo.RefsHeads) && Directory.Exists(repo.RefsHeads))
        {
            _inotify.Add(repo.RefsHeads);
            foreach (var d in SafeSubdirs(repo.RefsHeads)) _inotify.Add(d);
        }
    }

    private static IEnumerable<string> SafeSubdirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool Relevant(string? name) =>
        name is not null && !name.EndsWith(".lock", StringComparison.Ordinal) &&
        (name is "HEAD" or "index" or "packed-refs" or "BISECT_LOG" or "sequencer" || name.EndsWith("_HEAD", StringComparison.Ordinal) || name.StartsWith("rebase-", StringComparison.Ordinal));

    private void OnFsEvent(string dir, string? name, uint mask)
    {
        if (dir.Length == 0) // queue overflow: refresh everything
        {
            foreach (var t in _trackers.Values.Where(t => !t.Deleted)) Schedule(t.Id, _settings.Debounce);
            return;
        }
        var selfGone = (mask & (Inotify.DeleteSelf | Inotify.MoveSelf | Inotify.Ignored)) != 0;
        foreach (var r in _repos.Values)
        {
            if (dir == r.CommonDir && name is "worktrees" or "packed-refs" || dir == r.WorktreesDir)
            {
                if (dir == r.CommonDir && name == "packed-refs") ScheduleRepo(r);
                else
                {
                    if (dir == r.CommonDir && name == "worktrees" && Directory.Exists(r.WorktreesDir)) _inotify?.Add(r.WorktreesDir);
                    Schedule("list:" + r.CommonDir, _settings.Debounce);
                }
            }
            else if (dir == r.RefsHeads || dir.StartsWith(r.RefsHeads + "/", StringComparison.Ordinal))
            {
                if (name is null) continue;
                var full = Path.Combine(dir, name);
                if ((mask & Inotify.IsDir) != 0)
                {
                    if ((mask & (Inotify.Create | Inotify.MovedTo)) != 0) { _inotify?.Add(full); foreach (var d in SafeSubdirs(full)) _inotify?.Add(d); }
                    continue;
                }
                if (name.EndsWith(".lock", StringComparison.Ordinal)) continue;
                var branch = Path.GetRelativePath(r.RefsHeads, full);
                foreach (var t in _trackers.Values.Where(t => t.Repo == r && !t.Deleted && t.Summary?.Branch == branch)) Schedule(t.Id, _settings.Debounce);
            }
        }
        foreach (var t in _trackers.Values.Where(t => t.GitDir == dir && !t.Deleted))
        {
            if (selfGone) Schedule("list:" + t.Repo.CommonDir, _settings.Debounce);
            else if (Relevant(name)) Schedule(t.Id, _settings.Debounce);
        }
    }

    private void ScheduleRepo(RepoInfo r)
    {
        foreach (var t in _trackers.Values.Where(t => t.Repo == r && !t.Deleted)) Schedule(t.Id, _settings.Debounce);
    }

    // ---- discovery ----------------------------------------------------------------------

    /// <summary>Lists the repository's worktrees, emitting repository/worktree events (and waiting until they are applied).</summary>
    public async Task DiscoverRepositoryAsync(string commonDir, CancellationToken ct)
    {
        var task = _discovering.GetOrAdd(commonDir, cd => Task.Run(() => DiscoverCoreAsync(cd, _stopping.Token)));
        try { await task.WaitAsync(ct); }
        finally { _discovering.TryRemove(KeyValuePair.Create(commonDir, task)); }
    }

    private async Task DiscoverCoreAsync(string commonDir, CancellationToken ct)
    {
        var list = await _git.RunAsync(commonDir, ["worktree", "list", "--porcelain", "-z"], ct);
        if (!list.Ok)
        {
            if (!Directory.Exists(commonDir)) await MarkRepositoryGoneAsync(commonDir, ct);
            else
            {
                _health.Increment(GitCli.HealthName, "git_errors");
                _log.LogWarning("git worktree list failed in {CommonDir}: {Error}", commonDir, list.Stderr.Trim());
            }
            return;
        }
        var entries = GitParsers.ParseWorktreeList(list.Stdout);
        var repo = _repos.GetOrAdd(commonDir, cd => new RepoInfo(cd));
        repo.LastListAt = DateTimeOffset.UtcNow;
        var main = entries.FirstOrDefault() is { Bare: false } m ? m : null;
        var name = main is not null ? Path.GetFileName(main.Root) : BareName(commonDir);

        var remote = await _git.RunAsync(commonDir, ["config", "--get", "remote.origin.url"], ct);
        var webUrl = remote.Ok ? GitWebUrl.FromRemote(remote.Stdout) : null;
        var known = _state.Read(s => s.Repositories.TryGetValue(repo.Id, out var r) && r.Name == name && r.MainWorktree == main?.Root && r.WebUrl == webUrl);
        if (!known)
            await _sink.PublishAndWaitAsync(new AgentEvent
            {
                Type = EventTypes.RepositoryDiscovered, Source = EventSources.Git, RepositoryId = repo.Id,
                Data = Json.ToObject(new RepositoryData(name, commonDir, main?.Root, webUrl)),
            }, ct);

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Bare || (e.Prunable && !Directory.Exists(e.Root))) continue;
            var id = Ids.Worktree(e.Root);
            live.Add(id);
            var isNew = false;
            var t = _trackers.GetOrAdd(id, _ => { isNew = true; return new Tracker { Id = id, Root = e.Root, Repo = repo }; });
            if (t.Deleted) { t.Deleted = false; t.Files = null; t.Summary = null; isNew = true; }
            t.IsMain = e == main; t.Locked = e.Locked; t.Prunable = e.Prunable;
            t.GitDir ??= ResolveGitDir(e.Root);
            if (!_state.Read(s => s.Worktrees.TryGetValue(id, out var w) && !w.Removed))
                await _sink.PublishAndWaitAsync(new AgentEvent
                {
                    Type = EventTypes.WorktreeCreated, Source = EventSources.Git, RepositoryId = repo.Id, WorktreeId = id,
                    Data = Json.ToObject(new WorktreeData(e.Root, repo.Id, e.Branch, e.Head, e.Detached || e.Branch is null, t.IsMain, e.Locked, e.Prunable)),
                }, ct);
            Watch(repo, t);
            if (isNew) Schedule(id, TimeSpan.Zero);
        }

        var gone = _trackers.Values.Where(t => t.Repo == repo && !t.Deleted && !live.Contains(t.Id)).Select(t => t.Id)
            .Concat(_state.Read(s => s.Worktrees.Values.Where(w => w.RepositoryId == repo.Id && !w.Removed && !live.Contains(w.Id)).Select(w => w.Id).ToList()))
            .Distinct().ToList();
        foreach (var id in gone) await MarkWorktreeDeletedAsync(id, repo.Id, ct);
    }

    private static string BareName(string commonDir)
    {
        var n = Path.GetFileName(commonDir);
        if (n == ".git") n = Path.GetFileName(Path.GetDirectoryName(commonDir)) ?? n;
        return n.EndsWith(".git", StringComparison.Ordinal) && n.Length > 4 ? n[..^4] : n;
    }

    private static string? ResolveGitDir(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        try
        {
            if (!File.Exists(dotGit)) return null;
            var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            return line is null ? null : WorktreeResolver.Normalize(Path.GetFullPath(line[7..].Trim(), root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task MarkRepositoryGoneAsync(string commonDir, CancellationToken ct)
    {
        var repoId = Ids.Repository(commonDir);
        var ids = _state.Read(s => s.Worktrees.Values.Where(w => w.RepositoryId == repoId && !w.Removed).Select(w => w.Id).ToList());
        foreach (var id in ids.Concat(_trackers.Values.Where(t => t.Repo.Id == repoId && !t.Deleted).Select(t => t.Id)).Distinct())
            await MarkWorktreeDeletedAsync(id, repoId, ct);
        _repos.TryRemove(commonDir, out _);
    }

    private async Task MarkWorktreeDeletedAsync(string id, string repoId, CancellationToken ct)
    {
        string? root = null;
        if (_trackers.TryGetValue(id, out var t))
        {
            t.Deleted = true; t.Files = null; t.Summary = null; t.Unmerged = [];
            root = t.Root;
            if (t.GitDir is not null && !_trackers.Values.Any(o => o != t && !o.Deleted && o.GitDir == t.GitDir)) _inotify?.Remove(t.GitDir);
        }
        var known = _state.Read(s => s.Worktrees.TryGetValue(id, out var w) && !w.Removed ? w.Root : null);
        if (known is null) return;
        _log.LogInformation("Worktree {Root} was removed", known);
        await _sink.PublishAndWaitAsync(new AgentEvent
        {
            Type = EventTypes.WorktreeDeleted, Source = EventSources.Git, RepositoryId = repoId, WorktreeId = id,
            Data = Json.ToObject(new WorktreeData(root ?? known, repoId)),
        }, ct);
    }

    // ---- refresh ------------------------------------------------------------------------

    /// <summary>Runs git status for one worktree and emits the delta against what was last emitted.</summary>
    public async Task RefreshAsync(string worktreeId, CancellationToken ct)
    {
        if (!_trackers.TryGetValue(worktreeId, out var t) || t.Deleted) return;
        await t.Gate.WaitAsync(ct);
        try { await RefreshCoreAsync(t, ct); }
        finally { t.Gate.Release(); }
    }

    private async Task RefreshCoreAsync(Tracker t, CancellationToken ct)
    {
        if (t.Deleted) return;
        if (!Directory.Exists(t.Root)) { Schedule("list:" + t.Repo.CommonDir, TimeSpan.Zero); return; }
        var started = DateTimeOffset.UtcNow;
        var st = await _git.RunAsync(t.Root,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=normal", "--ignore-submodules=dirty"], ct);
        if (!st.Ok)
        {
            _health.Increment(GitCli.HealthName, "git_errors");
            _log.LogWarning("git status failed in {Root}: {Error}", t.Root, st.Stderr.Trim());
            Schedule("list:" + t.Repo.CommonDir, TimeSpan.Zero);
            return;
        }
        var status = GitParsers.ParseStatus(st.Stdout);
        var files = await WithLineCountsAsync(t, status, ct);
        var op = DetectOperation(t.GitDir);
        Interlocked.Increment(ref _refreshes);

        var seeded = t.Files is not null;
        t.Files ??= SeedFiles(t.Id);
        t.Summary ??= SeedSummary(t.Id);
        var actor = seeded ? _activity.SoleAgent(t.Id, t.LastRefreshAt, started) : null;
        var evidence = actor is null ? Evidence.Observed : Evidence.Inferred;

        // 1. Per-file deltas.
        var changes = files.Where(kv => !t.Files.TryGetValue(kv.Key, out var old) || old != kv.Value)
            .Select(kv => (Path: kv.Key, State: kv.Value))
            .Concat(t.Files.Keys.Where(p => !files.ContainsKey(p)).Select(p => (Path: p, State: new FileState("clean"))))
            .ToList();
        if (changes.Count > _settings.MaxFileEventsPerRefresh)
            _log.LogWarning("{Count} changed paths in {Root}; emitting the first {Max} now", changes.Count, t.Root, _settings.MaxFileEventsPerRefresh);
        foreach (var (path, fs) in changes.Take(_settings.MaxFileEventsPerRefresh))
        {
            await _sink.PublishAsync(new AgentEvent
            {
                Type = EventTypes.GitFileStatus, Source = EventSources.Git, Evidence = evidence, AgentId = actor,
                RepositoryId = t.Repo.Id, WorktreeId = t.Id, FilePath = path,
                Data = Json.ToObject(new GitFileStatusData(fs.Status, fs.Added, fs.Removed, fs.OldPath, actor)),
            }, ct);
            if (fs.Status == "clean") t.Files.Remove(path); else t.Files[path] = fs;
        }

        // 2. Summary, refs, operation, conflicts.
        var prev = t.Summary;
        var cur = new WorktreeData(t.Root, t.Repo.Id, status.Branch, status.Head, status.Detached, t.IsMain, t.Locked, t.Prunable,
            Modified: files.Values.Count(f => f.Status is "M" or "R"), Added: files.Values.Count(f => f.Status == "A"),
            Deleted: files.Values.Count(f => f.Status == "D"), Untracked: files.Values.Count(f => f.Status == "?"),
            Conflicted: files.Values.Count(f => f.Status == "U"), Ahead: status.Ahead, Behind: status.Behind,
            Upstream: status.Upstream, Operation: op);
        if (prev is not null) await EmitRefEventsAsync(t, prev, cur, actor, ct);

        var unmerged = files.Where(kv => kv.Value.Status == "U").Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        if (unmerged.Except(t.Unmerged).Any())
            await Publish(t, EventTypes.GitConflict, new GitConflictData(unmerged.Order(StringComparer.Ordinal).ToList(), op), ct);
        t.Unmerged = unmerged;

        if (cur != prev)
        {
            await Publish(t, EventTypes.WorktreeChanged, cur, ct);
            t.Summary = cur;
        }
        t.LastRefreshAt = started;
    }

    private Task Publish<T>(Tracker t, string type, T data, CancellationToken ct, string? agentId = null, string? dedupe = null) where T : class =>
        _sink.PublishAsync(new AgentEvent
        {
            Type = type, Source = EventSources.Git, Evidence = agentId is null ? Evidence.Observed : Evidence.Inferred, AgentId = agentId,
            RepositoryId = t.Repo.Id, WorktreeId = t.Id, DedupeKey = dedupe, Data = Json.ToObject(data),
        }, ct).AsTask();

    private async Task<Dictionary<string, FileState>> WithLineCountsAsync(Tracker t, StatusSnapshot status, CancellationToken ct)
    {
        var files = status.Files;
        if (files.Values.Any(f => f.Status is not "?"))
        {
            var baseRev = status.Head ?? await EmptyTreeAsync(t.Root, ct);
            var ns = baseRev is null ? null : await _git.RunAsync(t.Root, ["diff", "--numstat", "-z", "--no-ext-diff", baseRev, "--"], ct);
            if (ns is { Ok: true })
                foreach (var (path, (added, removed)) in GitParsers.ParseNumstat(ns.Stdout))
                    if (files.TryGetValue(path, out var f) && f.Status != "?") files[path] = f with { Added = added, Removed = removed };
        }
        var counted = 0;
        foreach (var (path, f) in files.Where(kv => kv.Value.Status == "?" && !kv.Key.EndsWith('/')).ToList())
        {
            if (++counted > _settings.MaxUntrackedLineCounts) break;
            if (GitParsers.CountLines(Path.Combine(t.Root, path), _settings.MaxUntrackedLineCountBytes) is { } lines)
                files[path] = f with { Added = lines, Removed = 0 };
        }
        return files;
    }

    private async Task<string?> EmptyTreeAsync(string root, CancellationToken ct)
    {
        var r = await _git.RunAsync(root, ["hash-object", "-t", "tree", "/dev/null"], ct);
        return r.Ok ? r.Stdout.Trim() : null;
    }

    public static string? DetectOperation(string? gitDir)
    {
        if (gitDir is null) return null;
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            return File.Exists(Path.Combine(gitDir, "rebase-apply", "applying")) ? "am" : "rebase";
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return "merge";
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return "cherry-pick";
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return "revert";
        if (File.Exists(Path.Combine(gitDir, "BISECT_LOG"))) return "bisect";
        return null;
    }

    private async Task EmitRefEventsAsync(Tracker t, WorktreeData prev, WorktreeData cur, string? actor, CancellationToken ct)
    {
        if (prev.Operation != cur.Operation)
        {
            if (prev.Operation is "merge" or "rebase")
                await Publish(t, prev.Operation == "merge" ? EventTypes.GitMerge : EventTypes.GitRebase,
                    new GitRefData(cur.Branch, cur.Head, prev.Branch, prev.Head, "finished"), ct);
            if (cur.Operation is "merge" or "rebase")
                await Publish(t, cur.Operation == "merge" ? EventTypes.GitMerge : EventTypes.GitRebase,
                    new GitRefData(cur.Branch, cur.Head, prev.Branch, prev.Head, "started"), ct);
        }
        if (prev.Branch == cur.Branch && prev.Head == cur.Head) return;
        var rebasing = prev.Operation is "rebase" or "am" || cur.Operation is "rebase" or "am";
        var refData = new GitRefData(cur.Branch, cur.Head, prev.Branch, prev.Head);

        if (prev.Branch != cur.Branch || cur.Branch is null)
        {
            // Branch switch (or detached HEAD moved). HEAD movement inside a rebase is the rebase itself.
            if (!rebasing) await Publish(t, EventTypes.GitCheckout, refData, ct, actor);
            return;
        }
        if (cur.Head is null) return;
        // Same branch, new head: commits, or history rewritten (amend/reset/rebase).
        if (prev.Head is not null)
        {
            var anc = await _git.RunAsync(t.Root, ["merge-base", "--is-ancestor", prev.Head, cur.Head], ct);
            if (anc.ExitCode != 0)
                await Publish(t, EventTypes.GitBranchChanged, refData with { Detail = anc.ExitCode == 1 ? "rewritten" : "moved" }, ct, actor);
        }
        if (rebasing) return;
        var range = prev.Head is null ? cur.Head : $"{prev.Head}..{cur.Head}";
        var log = await _git.RunAsync(t.Root,
            ["log", "--first-parent", "-n", _settings.MaxCommitsPerRefresh.ToString(), GitParsers.LogFormat, "--shortstat", range, "--"], ct);
        if (!log.Ok) return;
        foreach (var c in GitParsers.ParseLog(log.Stdout).AsEnumerable().Reverse())
            await Publish(t, EventTypes.GitCommitCreated, new GitCommitData(c.Sha, c.Subject, c.Author, c.CommittedAt, cur.Branch, c.FilesChanged),
                ct, actor, $"commit:{t.Id}:{c.Sha}");
    }

    private Dictionary<string, FileState> SeedFiles(string worktreeId) => _state.Read(s => s.Files.Values
        .Where(f => f.WorktreeId == worktreeId && f.GitStatus is not null and not "clean")
        .ToDictionary(f => f.Path, f => new FileState(f.GitStatus!, f.Added, f.Removed, f.GitStatus == "R" ? f.OldPath : null), StringComparer.Ordinal));

    private WorktreeData? SeedSummary(string worktreeId) => _state.Read(s => s.Worktrees.TryGetValue(worktreeId, out var w) && !w.Removed
        ? new WorktreeData(w.Root, w.RepositoryId, w.Branch, w.Head, w.Detached, w.IsMain, w.Locked, w.Prunable, w.Modified, w.Added,
            w.DeletedFiles, w.Untracked, w.Conflicted, w.Ahead, w.Behind, w.Upstream, w.Operation)
        : null);
}
