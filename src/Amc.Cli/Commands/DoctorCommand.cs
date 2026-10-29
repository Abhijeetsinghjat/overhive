using System.Text.Json.Nodes;
using Amc.Cli.Hooks;
using Amc.Cli.Infrastructure;
using Amc.Core.Providers;
using Amc.Core.Providers.Claude;
using Amc.Core.Security;

namespace Amc.Cli.Commands;

public sealed record Check(string Name, bool Ok, string Detail, string? Remedy = null, bool Warning = false);

public static class DoctorCommand
{
    public const int MinInotifyWatches = 8192;

    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var checks = await RunChecksAsync(ctx, args.Opt("settings"), ct);
        if (args.Json)
        {
            Api.PrintJson(ctx, new JsonArray(checks.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name, ["ok"] = c.Ok, ["warning"] = c.Warning, ["detail"] = c.Detail, ["remedy"] = c.Remedy,
            }).ToArray()));
        }
        else
        {
            var s = ctx.Style;
            foreach (var c in checks)
            {
                var mark = c.Ok ? s.Green("✓") : c.Warning ? s.Yellow("!") : s.Red("✗");
                ctx.Out.WriteLine($"{mark} {c.Name,-22} {c.Detail}");
                if (!c.Ok && c.Remedy is not null) ctx.Out.WriteLine(s.Dim($"  {"",-22} → {c.Remedy}"));
            }
            var failed = checks.Count(c => !c.Ok && !c.Warning);
            var warned = checks.Count(c => !c.Ok && c.Warning);
            ctx.Out.WriteLine(failed == 0 ? s.Green($"All essential checks passed{(warned > 0 ? $" ({warned} warning(s))" : "")}.") : s.Red($"{failed} check(s) failed."));
        }
        return checks.Any(c => !c.Ok && !c.Warning) ? ExitCodes.Error : ExitCodes.Ok;
    }

    public static async Task<List<Check>> RunChecksAsync(CliContext ctx, string? settingsOverride, CancellationToken ct)
    {
        var checks = new List<Check>();
        var settings = ctx.Settings;

        // Token file
        var tokenPath = settings.TokenPath;
        if (ctx.TokenOverride is null && !File.Exists(tokenPath))
            checks.Add(new("token file", false, $"{tokenPath} missing", "start amcd once; it creates the token"));
        else if (ctx.TokenOverride is null && OperatingSystem.IsWindows())
        {
            var others = OwnerOnly.OthersWithAccess(tokenPath);
            checks.Add(new("token file", others.Count == 0,
                others.Count == 0 ? $"{tokenPath} owner-only" : $"{tokenPath} readable by {string.Join(", ", others.Select(OwnerOnly.Name))}",
                $"icacls \"{tokenPath}\" /inheritance:r /grant:r *{OwnerOnly.CurrentUserSid}:F /remove {string.Join(' ', others.Select(o => "*" + o))}"));
        }
        else if (ctx.TokenOverride is null)
        {
            var mode = File.GetUnixFileMode(tokenPath);
            var tooOpen = (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                   UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0;
            checks.Add(new("token file", !tooOpen, $"{tokenPath} mode {ModeString(mode)}", $"chmod 600 {tokenPath}"));
        }
        else checks.Add(new("token file", true, "provided"));

        // Daemon + db + collectors + clock
        JsonNode? health = null;
        try
        {
            health = await Api.Health(ctx, ct);
            checks.Add(new("daemon reachable", true, $"{settings.DaemonUrl} (seq {health["seq"]})"));
        }
        catch (CliException ex)
        {
            checks.Add(new("daemon reachable", false, ex.Message, "start amcd (e.g. `systemctl --user start amcd` or run Amc.Daemon)"));
        }
        if (health is not null)
        {
            var dbOk = health["database"]?["ok"]?.GetValue<bool>() == true;
            checks.Add(new("database", dbOk, dbOk ? $"ok, {health["database"]?["events"]} events" : "database ping failed", "check disk space and the daemon log; the db lives in " + settings.DataDir));

            var comps = (health["components"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var bad = comps.Where(c => c["state"]?.ToString() is "failed" or "degraded").ToList();
            checks.Add(new("collectors", bad.Count == 0,
                bad.Count == 0 ? $"{comps.Count} component(s) healthy" : string.Join("; ", bad.Select(c => $"{c["name"]}: {c["state"]} {c["detail"]}")),
                "see `agentctl status` and the daemon log"));
            var dropped = health["queue"]?["dropped"]?.GetValue<long>() ?? 0;
            if (dropped > 0) checks.Add(new("ingest drops", false, $"{dropped} event(s) dropped by the ingest queue", "the daemon was overloaded; consider raising IngestQueueCapacity", Warning: true));

            if (health["serverTime"] is JsonValue tv && tv.TryGetValue<DateTimeOffset>(out var serverTime))
            {
                var skew = (ctx.Now() - serverTime).Duration();
                checks.Add(new("clock", skew < TimeSpan.FromSeconds(5), $"skew vs daemon {skew.TotalMilliseconds:0} ms, local {ctx.Now():yyyy-MM-dd HH:mm:ss zzz}",
                    "daemon and CLI clocks disagree; check NTP (timedatectl)"));
            }
        }
        else checks.Add(new("clock", true, $"local {ctx.Now():yyyy-MM-dd HH:mm:ss zzz} (daemon not reachable to compare)", Warning: true));

        // Hooks
        var settingsPath = settingsOverride ?? HooksFile.DefaultSettingsPath(ctx);
        try
        {
            var (exists, json) = HooksFile.Load(settingsPath);
            var installed = ClaudeHooksConfig.IsInstalled(json);
            checks.Add(new("claude hooks", installed, installed ? $"installed in {settingsPath}" : exists ? $"not installed in {settingsPath}" : $"{settingsPath} does not exist",
                "agentctl hooks install"));
        }
        catch (CliException ex)
        {
            checks.Add(new("claude hooks", false, ex.Message, "fix the JSON syntax in settings.json"));
        }

        // Claude registry
        var sessions = Path.Combine(settings.ClaudeHome, "sessions");
        checks.Add(new("claude registry", Directory.Exists(sessions), Directory.Exists(sessions) ? sessions : $"{sessions} not found",
            "install/run Claude Code once, or set ClaudeHome in " + settings.ConfigPath));

        // inotify
        const string watchesPath = "/proc/sys/fs/inotify/max_user_watches";
        if (File.Exists(watchesPath) && int.TryParse(File.ReadAllText(watchesPath).Trim(), out var watches))
            checks.Add(new("inotify watches", watches >= MinInotifyWatches, $"max_user_watches = {watches}",
                "sudo sysctl fs.inotify.max_user_watches=524288 (persist in /etc/sysctl.d/)"));
        else checks.Add(new("inotify watches", true, "not applicable on this platform", Warning: true));

        // Tools
        var git = FindOnPath(ctx, "git");
        checks.Add(new("git", git is not null, git ?? "not found on PATH", "install git (worktree/diff tracking needs it)"));
        // What the hooks run to reach amcd: curl, or on Windows agentctl itself (hook-forward).
        var (transport, transportPath) = HooksCommand.Transport(ctx);
        checks.Add(transport == "curl"
            ? new("curl", transportPath is not null, transportPath ?? "not found on PATH", "install curl (the Claude Code hooks use it to reach amcd)")
            : new("hook forwarder", transportPath is not null,
                transportPath is not null ? $"{transportPath} {HookForwarder.Subcommand}" : $"{ctx.Forwarder.Executable} not found",
                "reinstall agentctl, then run `agentctl hooks install` again so the hooks point at it"));
        return checks;
    }

    /// <summary>First executable <paramref name="exe"/> on PATH; on Windows also "git.exe", "x.cmd" … per PATHEXT.</summary>
    public static string? FindOnPath(CliContext ctx, string exe) => FindOnPath(ctx, exe, OperatingSystem.IsWindows());

    /// <summary>Same lookup with the Windows rules (PATHEXT, ';' separator) chosen explicitly; for tests on any OS.</summary>
    public static string? FindOnPath(CliContext ctx, string exe, bool windows)
    {
        string[] names = windows && !Path.HasExtension(exe)
            ? [.. (ctx.Env("PATHEXT") is { Length: > 0 } e ? e : ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ext => exe + ext.ToLowerInvariant())]
            : [exe];
        foreach (var dir in (ctx.Env("PATH") ?? "").Split(windows ? ';' : Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (!File.Exists(candidate)) continue;
                if (windows || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    return candidate;
            }
        return null;
    }

    public static string ModeString(UnixFileMode m) => Convert.ToString((int)m & 0x1FF, 8).PadLeft(4, '0');
}
