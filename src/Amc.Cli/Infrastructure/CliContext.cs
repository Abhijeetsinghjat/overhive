using System.Text.Json.Nodes;

namespace Amc.Cli.Infrastructure;

/// <summary>
/// Where the daemon lives and where its token is. Mirrors the daemon's own resolution
/// (Amc.Daemon.Infrastructure.AmcOptions): ~/.config/agent-mission-control/config.json,
/// then AMC_* environment variables. AMC_URL overrides the URL entirely.
/// </summary>
public sealed record CliSettings(Uri DaemonUrl, int Port, string DataDir, string TokenPath, string ConfigPath, string ClaudeHome)
{
    public const int DefaultPort = 47821;

    public static CliSettings Load(Func<string, string?> env, string home, string? urlOverride = null)
    {
        var configHome = env("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        var configPath = Path.Combine(configHome, "agent-mission-control", "config.json");
        var stateHome = env("XDG_STATE_HOME") is { Length: > 0 } s ? s : Path.Combine(home, ".local", "state");

        JsonObject? config = null;
        try
        {
            if (File.Exists(configPath)) config = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken config is reported by `doctor`; fall back to defaults here.
        }

        string? Setting(string key) =>
            EnvCI(env, "AMC_" + key) ??
            config?.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value?.ToString();

        var port = int.TryParse(Setting("Port"), out var p) && p is > 0 and < 65536 ? p : DefaultPort;
        var dataDir = Setting("DataDir") is { Length: > 0 } d ? d : Path.Combine(stateHome, "agent-mission-control");
        var claudeHome = Setting("ClaudeHome") is { Length: > 0 } c ? c
            : env("CLAUDE_CONFIG_DIR") is { Length: > 0 } cc ? cc : Path.Combine(home, ".claude");

        var urlText = urlOverride ?? env("AMC_URL");
        Uri url;
        if (urlText is { Length: > 0 })
        {
            if (!Uri.TryCreate(urlText.TrimEnd('/') + "/", UriKind.Absolute, out url!) || (url.Scheme != "http" && url.Scheme != "https"))
                throw new UsageException($"invalid daemon URL '{urlText}' (expected http://127.0.0.1:{DefaultPort})");
        }
        else url = new Uri($"http://127.0.0.1:{port}/");

        return new CliSettings(url, url.Port, dataDir, Path.Combine(dataDir, "token"), configPath, claudeHome);
    }

    private static string? EnvCI(Func<string, string?> env, string key) =>
        env(key) is { Length: > 0 } v ? v : env(key.ToUpperInvariant()) is { Length: > 0 } u ? u : null;

    /// <summary>Reads the API token. Missing file is the classic "daemon never started" situation.</summary>
    public string ReadToken()
    {
        try
        {
            var t = File.ReadAllText(TokenPath).Trim();
            if (t.Length == 0) throw new CliException($"token file {TokenPath} is empty — restart amcd to regenerate it");
            return t;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new DaemonUnreachableException($"token file not found at {TokenPath} — daemon never started? Start amcd first.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new CliException($"cannot read token file {TokenPath} (permission denied) — run agentctl as the user that runs amcd");
        }
    }
}

/// <summary>Everything a command needs from the outside world; replaceable in tests.</summary>
public sealed class CliContext
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public TextReader In { get; init; } = Console.In;
    /// <summary>ANSI colour on stdout (TTY and NO_COLOR unset, and no --no-color).</summary>
    public bool Color { get; set; }
    /// <summary>True when a human can answer confirmation prompts (stdin is a terminal).</summary>
    public bool Interactive { get; init; }
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;
    public string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string Cwd { get; init; } = Environment.CurrentDirectory;
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
    /// <summary>Test hook: when set, HTTP goes through this handler instead of the network.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
    /// <summary>Test hook: settings to use instead of loading from env/config.</summary>
    public CliSettings? SettingsOverride { get; init; }
    /// <summary>Test hook: token to use instead of reading the token file.</summary>
    public string? TokenOverride { get; init; }
    public string? UrlOverride { get; set; }

    private CliSettings? _settings;
    private AmcClient? _client;

    public CliSettings Settings => _settings ??= SettingsOverride ?? CliSettings.Load(Env, Home, UrlOverride);

    public string Token => TokenOverride ?? Settings.ReadToken();

    public AmcClient Client => _client ??= new AmcClient(Settings.DaemonUrl, Token, HttpHandler);

    public Style Style => new(Color);

    public static CliContext ForConsole()
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 };
        return new CliContext
        {
            Color = !Console.IsOutputRedirected && !noColor && Environment.GetEnvironmentVariable("TERM") != "dumb",
            Interactive = !Console.IsInputRedirected,
        };
    }

    /// <summary>Asks a yes/no question; default is always No. Non-interactive sessions get No.</summary>
    public bool Confirm(string question)
    {
        if (!Interactive) return false;
        Err.Write($"{question} [y/N] ");
        Err.Flush();
        var answer = In.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
