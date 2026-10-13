using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Amc.Core.Events;
using Amc.Core.State;
using Amc.Daemon;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Claude;

/// <summary>Records every published event; <see cref="Stored"/> applies dedupe-key semantics (first wins) like the event store.</summary>
public sealed class CollectingSink : IEventSink
{
    private readonly HashSet<string> _keys = [];
    public List<AgentEvent> All { get; } = [];
    public List<AgentEvent> Stored { get; } = [];

    private AgentEvent? Add(AgentEvent e)
    {
        lock (All)
        {
            All.Add(e);
            if (e.DedupeKey is not null && !_keys.Add(e.DedupeKey)) return null;
            var stored = e with { Seq = Stored.Count + 1 };
            Stored.Add(stored);
            return stored;
        }
    }

    public bool TryPublish(AgentEvent e) { Add(e); return true; }
    public ValueTask PublishAsync(AgentEvent e, CancellationToken ct = default) { Add(e); return ValueTask.CompletedTask; }
    public Task<AgentEvent?> PublishAndWaitAsync(AgentEvent e, CancellationToken ct = default) => Task.FromResult(Add(e));

    public List<AgentEvent> OfType(string type) => Stored.Where(e => e.Type == type).ToList();

    public WorldState Fold()
    {
        var s = new WorldState();
        Reducer.ApplyBatch(s, Stored.Select(e => e with { Seq = 0 }));
        return s;
    }
}

public sealed class FakeWorktreeIndex : IWorktreeIndex
{
    public Dictionary<string, string> Roots { get; } = new() { ["/work/repo"] = "repo-1", ["/work/wt-feature"] = "repo-1" };
    public ConcurrentQueue<WorktreeActivity> Activity { get; } = new();

    public ValueTask<WorktreeHit?> ResolveAsync(string absolutePath, CancellationToken ct = default)
    {
        var root = Roots.Keys.Where(r => absolutePath == r || absolutePath.StartsWith(r + "/", StringComparison.Ordinal))
            .OrderByDescending(r => r.Length).FirstOrDefault();
        return ValueTask.FromResult(root is null ? null :
            new WorktreeHit(WtId(root), "repo:" + Roots[root], root, absolutePath == root ? "" : Path.GetRelativePath(root, absolutePath)));
    }

    public static string WtId(string root) => "wt:" + root.Replace('/', '_');
    public void NotifyActivity(WorktreeActivity activity) => Activity.Enqueue(activity);
}

public sealed class FakeTerminalHub : ITerminalHub
{
    public HashSet<string> Attached { get; } = [];
    public List<(string Id, string Text)> Writes { get; } = [];
    public bool IsAttached(string terminalId) => Attached.Contains(terminalId);
    public Task<bool> WriteAsync(string terminalId, ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        if (!Attached.Contains(terminalId)) return Task.FromResult(false);
        Writes.Add((terminalId, System.Text.Encoding.UTF8.GetString(input.Span)));
        return Task.FromResult(true);
    }
    public byte[] Buffer(string terminalId) => [];
}

public sealed class FakeLiveProcesses : ILiveProcesses
{
    public Dictionary<int, List<int>> Children { get; } = [];
    public IReadOnlyList<LiveProcess> Snapshot() => [];
    public LiveProcess? Get(int pid) => null;
    public IReadOnlyList<int> Tree(int pid) => [pid, .. Children.GetValueOrDefault(pid) ?? []];
}

public sealed class FakeProcFs : IProcFs
{
    public ConcurrentDictionary<int, (string Start, char State)> Procs { get; } = new();
    public string? ProcStart(int pid) => Procs.TryGetValue(pid, out var p) ? p.Start : null;
    public char? State(int pid) => Procs.TryGetValue(pid, out var p) ? p.State : null;
}

/// <summary>Applies signal effects to <see cref="FakeProcFs"/> like a kernel would.</summary>
public sealed class FakeSignals(FakeProcFs proc) : ISignalSender
{
    public List<(int Pid, int Signal)> Sent { get; } = [];
    public bool IgnoreTerm { get; set; }
    public int Send(int pid, int signal)
    {
        Sent.Add((pid, signal));
        if (!proc.Procs.TryGetValue(pid, out var p)) return 3; // ESRCH
        switch (signal)
        {
            case Signals.SigStop: proc.Procs[pid] = (p.Start, 'T'); break;
            case Signals.SigCont: proc.Procs[pid] = (p.Start, 'S'); break;
            case Signals.SigTerm when !IgnoreTerm:
            case Signals.SigKill: proc.Procs.TryRemove(pid, out _); break;
        }
        return 0;
    }
}

public sealed class InMemoryCursors : ICursorStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new();
    public string? Get(string key) => Values.GetValueOrDefault(key);
    public void Set(string key, string value) => Values[key] = value;
}

public sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Wires the Claude module's classes with fakes and a temp ~/.claude.</summary>
public sealed class ClaudeHarness : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("amc-claude-").FullName;
    public AmcOptions Options { get; }
    public CollectingSink Sink { get; } = new();
    public FakeWorktreeIndex Worktrees { get; } = new();
    public FakeTerminalHub Terminals { get; } = new();
    public FakeLiveProcesses Live { get; } = new();
    public FakeProcFs Proc { get; } = new();
    public FakeSignals Signals { get; }
    public InMemoryCursors Cursors { get; } = new();
    public FakeTime Time { get; } = new(DateTimeOffset.UtcNow);
    public HealthRegistry Health { get; } = new();
    public StateStore State { get; } = new();
    public Mailbox Mailbox { get; } = new();
    public ClaudeContext Context { get; }
    public ClaudeEventMapper Mapper { get; }
    public ClaudeHooksState Hooks { get; }

    public ClaudeHarness()
    {
        Options = new AmcOptions { ClaudeHome = Path.Combine(Root, ".claude"), DataDir = Path.Combine(Root, "data"), BackfillHours = 24 };
        Directory.CreateDirectory(Options.ClaudeHome);
        Signals = new FakeSignals(Proc);
        Context = new ClaudeContext(State);
        Mapper = new ClaudeEventMapper(Context, Worktrees, Time);
        Hooks = new ClaudeHooksState(Path.Combine(Options.ClaudeHome, "settings.json"));
    }

    public string ProjectsDir => Path.Combine(Options.ClaudeHome, "projects");
    public const string SessionId = "5e55a0aa-0000-4000-8000-000000000001";
    public string SessionDir => Path.Combine(ProjectsDir, "-work-repo");
    public string MainTranscript => Path.Combine(SessionDir, SessionId + ".jsonl");

    public TranscriptIngestor Ingestor() => new(Mapper, Sink, Cursors, Health, Microsoft.Extensions.Options.Options.Create(Options), Time);

    public ClaudeHookReceiver HookReceiver() => new(Mapper, Mailbox, Sink, Health, Microsoft.Extensions.Options.Options.Create(Options), Time,
        NullLogger<ClaudeHookReceiver>.Instance);

    public ClaudeRegistryCollector Registry() => new(Context, Sink, Proc, State, Health, Microsoft.Extensions.Options.Options.Create(Options), Time,
        NullLogger<ClaudeRegistryCollector>.Instance);

    public ClaudeProvider Provider() => new(Terminals, Live, Proc, Signals, Hooks, Mailbox, Context, Sink,
        Microsoft.Extensions.Options.Options.Create(Options), Time);

    /// <summary>Copies tests/Amc.Tests/Fixtures/Claude/&lt;sub&gt; into the temp ~/.claude.</summary>
    public void CopyFixtures(string sub = "projects")
    {
        var src = Path.Combine(FixtureRoot(), sub);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(Options.ClaudeHome, sub, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            File.SetLastWriteTimeUtc(dest, Time.Now.UtcDateTime); // fixtures are "fresh": inside the backfill window whatever the checkout date
        }
    }

    public void InstallHooks() =>
        File.WriteAllText(Hooks.SettingsPath, Amc.Core.Providers.Claude.ClaudeHooksConfig
            .Install(new JsonObject(), Amc.Core.Providers.Claude.ClaudeHooksConfig.BuildCommand("/tmp/amcd.sock")).ToJsonString());

    public static string FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "Amc.Tests", "Fixtures", "Claude"))) dir = dir.Parent;
        return dir is null ? throw new DirectoryNotFoundException("Fixtures/Claude not found")
            : Path.Combine(dir.FullName, "tests", "Amc.Tests", "Fixtures", "Claude");
    }

    public static JsonObject Hook(string evt, string sessionId = SessionId, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject
        {
            ["session_id"] = sessionId, ["transcript_path"] = $"/home/u/.claude/projects/-work-repo/{sessionId}.jsonl",
            ["cwd"] = "/work/repo", ["permission_mode"] = "default", ["hook_event_name"] = evt,
        };
        foreach (var (k, v) in fields) o[k] = v;
        return o;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
