namespace Amc.Daemon.Infrastructure;

/// <summary>
/// Daemon configuration. Loaded from ~/.config/agent-mission-control/config.json and
/// environment variables prefixed AMC_ (e.g. AMC_Port=47821). No hard-coded repo paths:
/// workspace roots default to ~/dev only if that exists.
/// </summary>
public sealed class AmcOptions
{
    public const int DefaultPort = 47821;

    public int Port { get; set; } = DefaultPort;
    /// <summary>Loopback only unless explicitly changed (then the token is mandatory, which it always is).</summary>
    public string BindAddress { get; set; } = "127.0.0.1";
    public string DataDir { get; set; } = DefaultDataDir();
    public string ClaudeHome { get; set; } = Path.Combine(Home, ".claude");
    /// <summary>Configured roots; empty means the default (~/dev if it exists). Configuration replaces the default.</summary>
    public List<string> WorkspaceRoots { get; set; } = [];
    public IReadOnlyList<string> EffectiveWorkspaceRoots => WorkspaceRoots.Count > 0 ? WorkspaceRoots : DefaultRoots();
    public int WorkspaceScanDepth { get; set; } = 3;
    /// <summary>How far back transcripts are backfilled on first start.</summary>
    public double BackfillHours { get; set; } = 24;
    public int SnapshotEveryEvents { get; set; } = 2000;
    /// <summary>Detail events (tool/command calls, process start/exit, outputs, superseded git status) are kept this long.</summary>
    public int DetailRetentionDays { get; set; } = 7;
    /// <summary>History events (agent lifecycle, prompts, usage, commits, messages, audit) are kept this long.</summary>
    public int HistoryRetentionDays { get; set; } = 180;
    public double RetentionIntervalHours { get; set; } = 6;
    /// <summary>Extra or overriding AI tool definitions (merged with the built-in catalog).</summary>
    public List<Amc.Core.Domain.AgentTool> Tools { get; set; } = [];
    public int IngestQueueCapacity { get; set; } = 20_000;
    /// <summary>When true, a clearly-labelled simulated agent is started (demo/test mode only).</summary>
    public bool Simulate { get; set; }
    /// <summary>Allow UI-initiated process control (pause/resume/terminate). Message sending is always allowed.</summary>
    public bool EnableControl { get; set; } = true;
    /// <summary>
    /// Send the "Warn agents" heads-up automatically when a cross-worktree conflict has overlapping changes:
    /// once per conflict and agent, again only when the overlapping lines change. Off by default.
    /// </summary>
    public bool AutoWarnConflicts { get; set; }

    public string DbPath => Path.Combine(DataDir, "amc.db");
    public string TokenPath => Path.Combine(DataDir, "token");
    public string TerminalDir => Path.Combine(DataDir, "terminals");
    public string HookLogPath => Path.Combine(DataDir, "hook-payloads.jsonl");
    /// <summary>Unix socket for hooks/CLI. Lives in the 0700 data dir: only this user can connect, nobody can squat it.</summary>
    public string SocketPath => Path.Combine(DataDir, Amc.Core.Providers.Claude.ClaudeHooksConfig.SocketFileName);

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DefaultDataDir() =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".local", "state"),
            "agent-mission-control");

    public static string ConfigPath() =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"),
            "agent-mission-control", "config.json");

    private static List<string> DefaultRoots()
    {
        var dev = Path.Combine(Home, "dev");
        return Directory.Exists(dev) ? [dev] : [];
    }
}
