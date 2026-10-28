using System.Collections.Concurrent;
using System.Diagnostics;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Collectors.Git;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Git;

/// <summary>Records every published event and forwards it to the real pipeline (SQLite + reducer) so StateStore reflects it.</summary>
public sealed class RecordingSink(IEventSink inner) : IEventSink
{
    public ConcurrentQueue<AgentEvent> Events { get; } = new();
    public bool TryPublish(AgentEvent e) { Events.Enqueue(e); return inner.TryPublish(e); }
    public ValueTask PublishAsync(AgentEvent e, CancellationToken ct = default) { Events.Enqueue(e); return inner.PublishAsync(e, ct); }
    public Task<AgentEvent?> PublishAndWaitAsync(AgentEvent e, CancellationToken ct = default) { Events.Enqueue(e); return inner.PublishAndWaitAsync(e, ct); }

    public List<AgentEvent> OfType(string type) => Events.Where(e => e.Type == type).ToList();
    public void Clear() => Events.Clear();
}

/// <summary>Temp directory with real git repositories, a real event pipeline and a <see cref="GitCollector"/>.</summary>
public sealed class GitTestEnv : IAsyncDisposable
{
    public string Dir { get; }
    public HealthRegistry Health { get; } = new();
    public StateStore State { get; } = new();
    public EventStore Store { get; }
    public EventPipeline Pipeline { get; }
    public RecordingSink Sink { get; }
    public GitCli Git { get; }
    public GitCollector Collector { get; }
    public AmcOptions Options { get; }

    private GitTestEnv(GitSettings settings, Func<string, List<string>> roots)
    {
        Dir = GitPaths.RealPath(Path.Combine(Path.GetTempPath(), "amc-git-tests", Guid.NewGuid().ToString("N")))!;
        Directory.CreateDirectory(Dir);
        Options = new AmcOptions { DataDir = Path.Combine(Dir, "data"), WorkspaceRoots = roots(Dir), WorkspaceScanDepth = 3 };
        Store = new EventStore(Options.DbPath);
        Pipeline = new EventPipeline(Store, State, new Broadcaster(State, Health), Health, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<EventPipeline>.Instance);
        Sink = new RecordingSink(Pipeline);
        Git = new GitCli(Health, NullLogger<GitCli>.Instance);
        Collector = new GitCollector(Git, Sink, State, Health, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<GitCollector>.Instance, settings);
    }

    public static async Task<GitTestEnv> CreateAsync(GitSettings? settings = null, Func<string, List<string>>? roots = null)
    {
        var env = new GitTestEnv(settings ?? new GitSettings { ScanOnStart = false, Watch = false }, roots ?? (d => [Path.Combine(d, "ws")]));
        env.Pipeline.Restore();
        await env.Pipeline.StartAsync(CancellationToken.None);
        return env;
    }

    /// <summary>Creates a repository with one commit containing the given files; returns its root in <see cref="PathText"/> form (as git reports it).</summary>
    public string InitRepo(string relDir, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(Dir, relDir);
        Directory.CreateDirectory(dir);
        RunGit(dir, "init", "-q", "-b", "main");
        foreach (var (p, c) in files) Write(dir, p, c);
        if (files.Length == 0) Write(dir, "README.md", "readme\n");
        RunGit(dir, "add", "-A");
        RunGit(dir, "commit", "-q", "-m", "initial");
        return PathText.Normalize(dir);
    }

    public static void Write(string dir, string rel, string content)
    {
        var full = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Runs git as a user/agent would (isolated from the machine's global config).</summary>
    public static (int Code, string Out) RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment.Remove("GIT_DIR");
        psi.Environment.Remove("GIT_WORK_TREE");
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr);
    }

    public static string Head(string dir) => RunGit(dir, "rev-parse", "HEAD").Out.Trim();

    /// <summary>Discovers the repository at <paramref name="dir"/> on demand and returns its worktree id.</summary>
    public async Task<string> DiscoverAsync(string dir)
    {
        var hit = await Collector.ResolveAsync(Path.Combine(dir, "x"));
        Assert.NotNull(hit);
        await Collector.RefreshAsync(hit.WorktreeId, CancellationToken.None); // baseline (seeds from state)
        await Applied();
        return hit.WorktreeId;
    }

    /// <summary>Waits until everything published so far is applied to the state.</summary>
    public async Task Applied() =>
        await Sink.PublishAndWaitAsync(new AgentEvent { Type = "test.barrier", Source = EventSources.System });

    public static async Task Eventually(Func<bool> condition, string because, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Timed out waiting: {because}");
            await Task.Delay(50);
        }
    }

    public List<GitFileStatusData> FileEvents(string path) =>
        Sink.OfType(EventTypes.GitFileStatus).Where(e => e.FilePath == path).Select(e => e.Payload<GitFileStatusData>()!).ToList();

    public async ValueTask DisposeAsync()
    {
        Collector.Dispose();
        await Pipeline.StopAsync(CancellationToken.None);
        Store.Dispose();
        Fixtures.TempDirs.Delete(Dir);
    }
}
