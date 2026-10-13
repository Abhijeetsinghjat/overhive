using System.Text.Json.Nodes;
using Amc.Cli.Hooks;
using Amc.Cli.Infrastructure;
using Amc.Core.Providers.Claude;

namespace Amc.Tests.Cli;

/// <summary>All tests operate on temp files; the real ~/.claude/settings.json is never touched.</summary>
public class HooksInstallTests : IDisposable
{
    private readonly CliHarness _h = new();
    private string Settings => Path.Combine(_h.Dir, ".claude", "settings.json");
    private const string Foreign = """
        {
          "model": "opus",
          "permissions": { "allow": ["Bash(ls:*)"] },
          "hooks": {
            "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "/usr/local/bin/guard.sh" } ] } ],
            "Stop": [ { "hooks": [ { "type": "command", "command": "notify-send done" } ] } ]
          }
        }
        """;

    public void Dispose() => _h.Dispose();

    private void WriteSettings(string json, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Settings, json);
        File.SetUnixFileMode(Settings, mode);
    }

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(Settings))!;
    private string[] Backups() => Directory.GetFiles(Path.GetDirectoryName(Settings)!, "settings.json.amc-backup-*");

    private static IEnumerable<string> ForeignCommands(JsonObject s) =>
        (s["hooks"] as JsonObject ?? []).SelectMany(kv => kv.Value!.AsArray()).SelectMany(g => g!["hooks"]!.AsArray())
            .Where(h => !ClaudeHooksConfig.IsOurs(h)).Select(h => h!["command"]!.ToString()).Order();

    [Fact]
    public async Task Install_creates_missing_file_without_backup()
    {
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        Assert.True(ClaudeHooksConfig.IsInstalled(Read()));
        Assert.Empty(Backups());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Settings));
        Assert.Contains("+ SessionStart", _h.Out.ToString());
    }

    [Fact]
    public async Task Install_preserves_foreign_hooks_and_settings_and_backs_up_first()
    {
        WriteSettings(Foreign, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));

        var after = Read();
        Assert.True(ClaudeHooksConfig.IsInstalled(after));
        Assert.Equal(["/usr/local/bin/guard.sh", "notify-send done"], ForeignCommands(after));
        Assert.Equal("opus", after["model"]!.ToString());
        Assert.Equal("Bash(ls:*)", after["permissions"]!["allow"]![0]!.ToString());
        // The command references the token path and port, readable (no ' escapes).
        var cmd = after["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]!.ToString();
        Assert.Equal(ClaudeHooksConfig.BuildCommand(Path.Combine(_h.Dir, "state", ClaudeHooksConfig.SocketFileName)), cmd);
        Assert.DoesNotContain("\\u0027", File.ReadAllText(Settings));

        var backup = Assert.Single(Backups());
        Assert.Equal(Foreign, File.ReadAllText(backup));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(Settings)); // mode preserved
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Settings)!, "*.amc-tmp-*", SearchOption.AllDirectories));     // no temp leftovers
        Assert.Contains("= 2 other hook(s) left untouched", _h.Out.ToString());
        Assert.Contains("backup: ", _h.Out.ToString());
    }

    [Fact]
    public async Task Install_is_idempotent()
    {
        WriteSettings(Foreign);
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        var first = File.ReadAllText(Settings);
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        Assert.Equal(first, File.ReadAllText(Settings));
        Assert.Single(Backups()); // second run wrote nothing
        Assert.Contains("already installed and up to date", _h.Out.ToString());

        // Each event carries exactly one synchronous hook of ours; each idle event exactly one asyncRewake waiter.
        static bool Rewake(System.Text.Json.Nodes.JsonNode? h) => h?["asyncRewake"]?.GetValue<bool>() == true;
        IEnumerable<System.Text.Json.Nodes.JsonNode?> Ours(string ev) =>
            Read()["hooks"]![ev]!.AsArray().SelectMany(g => g!["hooks"]!.AsArray()).Where(ClaudeHooksConfig.IsOurs);
        foreach (var ev in ClaudeHooksConfig.Events) Assert.Single(Ours(ev).Where(h => !Rewake(h)));
        foreach (var (ev, _) in ClaudeHooksConfig.RewakeEvents) Assert.Single(Ours(ev).Where(Rewake));
    }

    [Fact]
    public async Task Hook_command_uses_the_private_unix_socket_and_never_a_token()
    {
        WriteSettings(Foreign);
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        var stop = Read()["hooks"]!["Stop"]!.ToJsonString();
        Assert.Contains("--unix-socket", stop);
        Assert.Contains(ClaudeHooksConfig.SocketFileName, stop);
        Assert.DoesNotContain("X-Amc-Token", stop);
        Assert.DoesNotContain("127.0.0.1", stop);
        Assert.Equal(["/usr/local/bin/guard.sh", "notify-send done"], ForeignCommands(Read()));
    }

    [Fact]
    public void Socket_path_is_shell_quoted() =>
        Assert.Contains("--unix-socket '/tmp/it'\\''s/amcd.sock'", ClaudeHooksConfig.BuildCommand("/tmp/it's/amcd.sock"));

    [Fact]
    public async Task Uninstall_removes_only_our_entries()
    {
        WriteSettings(Foreign);
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        Assert.Equal(0, await _h.Run("hooks", "uninstall", "--settings", Settings, "--yes"));
        var after = Read();
        Assert.False(ClaudeHooksConfig.IsInstalled(after));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Foreign), after)); // back to exactly the original content
        Assert.Equal(2, Backups().Length);

        Assert.Equal(0, await _h.Run("hooks", "uninstall", "--settings", Settings, "--yes"));
        Assert.Contains("nothing to do", _h.Out.ToString());
        Assert.Equal(2, Backups().Length);
    }

    [Fact]
    public async Task Requires_confirmation_and_never_defaults_to_yes()
    {
        WriteSettings(Foreign);
        // Non-interactive without --yes: refused (usage), file untouched.
        Assert.Equal(ExitCodes.Usage, await _h.Run("", false, "hooks", "install", "--settings", Settings));
        Assert.Equal(Foreign, File.ReadAllText(Settings));
        // Interactive, user just presses enter: default is No.
        Assert.Equal(ExitCodes.Error, await _h.Run("\n", true, "hooks", "install", "--settings", Settings));
        Assert.Equal(Foreign, File.ReadAllText(Settings));
        Assert.Empty(Backups());
        // Interactive "y": applied.
        Assert.Equal(0, await _h.Run("y\n", true, "hooks", "install", "--settings", Settings));
        Assert.True(ClaudeHooksConfig.IsInstalled(Read()));
    }

    [Fact]
    public async Task Invalid_json_is_never_overwritten()
    {
        WriteSettings("{ \"model\": ");
        Assert.Equal(ExitCodes.Error, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        Assert.Equal("{ \"model\": ", File.ReadAllText(Settings));
        Assert.Contains("not valid JSON", _h.Err.ToString());
    }

    [Fact]
    public async Task Symlinked_settings_are_written_through_and_link_kept()
    {
        var realDir = Path.Combine(_h.Dir, "dotfiles");
        Directory.CreateDirectory(realDir);
        var real = Path.Combine(realDir, "claude-settings.json");
        File.WriteAllText(real, Foreign);
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.CreateSymbolicLink(Settings, real);

        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        Assert.NotNull(new FileInfo(Settings).LinkTarget);
        Assert.True(ClaudeHooksConfig.IsInstalled((JsonObject)JsonNode.Parse(File.ReadAllText(real))!));
        Assert.Single(Directory.GetFiles(realDir, "claude-settings.json.amc-backup-*"));
    }

    [Fact]
    public void Atomic_write_replaces_content_and_applies_exact_mode()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Settings, "old");
        HooksFile.WriteAtomic(Settings, "{}\n", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Assert.Equal("{}\n", File.ReadAllText(Settings));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead, File.GetUnixFileMode(Settings));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Settings)!));
    }

    [Fact]
    public void Backup_names_never_collide()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        var first = HooksFile.BackupPath(Settings, CliHarness.Now);
        File.WriteAllText(first, "x");
        var second = HooksFile.BackupPath(Settings, CliHarness.Now);
        Assert.NotEqual(first, second);
        Assert.EndsWith("settings.json.amc-backup-20261007T120000Z", first);
    }

    [Fact]
    public async Task Status_reports_per_event_and_daemon_reachability()
    {
        WriteSettings(Foreign);
        _h.Daemon.Unreachable = true;
        Assert.Equal(0, await _h.Run("hooks", "status", "--settings", Settings, "--json"));
        var j = JsonNode.Parse(_h.Out.ToString())!;
        Assert.False(j["installed"]!.GetValue<bool>());
        Assert.False(j["daemonReachable"]!.GetValue<bool>());
        Assert.False(j["events"]!["Stop"]!.GetValue<bool>());
    }
}
