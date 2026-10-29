using System.Text.Json.Nodes;
using Amc.Cli.Hooks;
using Amc.Cli.Infrastructure;
using Amc.Core.Providers;
using Amc.Core.Providers.Claude;

namespace Amc.Cli.Commands;

public static class HooksCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var sub = args.Positionals.Count > 0 ? args.Positionals[0] : "status";
        var tool = args.Opt("tool") ?? "claude";
        if (tool != "claude") return await OtherToolAsync(ctx, args, sub, tool, ct);
        var settingsPath = args.Opt("settings") ?? HooksFile.DefaultSettingsPath(ctx);
        var socket = Path.Combine(ctx.Settings.DataDir, ClaudeHooksConfig.SocketFileName);
        var (command, rewakeCommand) = Commands(ctx, socket);
        return sub switch
        {
            "status" => await StatusAsync(ctx, args, settingsPath, command, rewakeCommand, ct),
            "install" => Change(ctx, args, HooksFile.PlanInstall(settingsPath, command, rewakeCommand), "install"),
            "uninstall" => Change(ctx, args, HooksFile.PlanUninstall(settingsPath), "uninstall"),
            _ => throw new UsageException($"unknown hooks subcommand '{sub}' (status | install | uninstall)"),
        };
    }

    /// <summary>
    /// The Claude Code hook and rewake waiter: curl one-liners on Linux/macOS, agentctl hook-forward in exec form
    /// (no shell) on Windows.
    /// </summary>
    public static (ClaudeHooksConfig.HookCommand Command, ClaudeHooksConfig.HookCommand Rewake) Commands(CliContext ctx, string socket) =>
        ctx.IsWindows
            ? (ClaudeHooksConfig.BuildWindowsCommand(ctx.Forwarder, socket), ClaudeHooksConfig.BuildWindowsRewakeCommand(ctx.Forwarder, socket))
            : (ClaudeHooksConfig.BuildCommand(socket), ClaudeHooksConfig.BuildRewakeCommand(socket));

    /// <summary>What the hooks need to reach amcd: curl, or on Windows the agentctl they run (null when missing).</summary>
    public static (string Name, string? Path) Transport(CliContext ctx)
    {
        if (!ctx.IsWindows) return ("curl", DoctorCommand.FindOnPath(ctx, "curl"));
        var exe = ctx.Forwarder.Executable;
        return ("forwarder", File.Exists(exe) ? exe : null);
    }

    /// <summary>`agentctl hooks status|install|uninstall --tool codex|gemini|grok|cursor|copilot`.</summary>
    private static Task<int> OtherToolAsync(CliContext ctx, ParsedArgs args, string sub, string tool, CancellationToken ct)
    {
        if (!ToolHooksConfig.Profiles.TryGetValue(tool, out var profile))
            throw new UsageException($"no hook integration for '{tool}' (claude, {string.Join(", ", ToolHooksConfig.Profiles.Keys)})");
        var path = args.Opt("settings") ?? Path.Combine(ctx.Home, profile.UserFile);
        var socket = Path.Combine(ctx.Settings.DataDir, ClaudeHooksConfig.SocketFileName);
        switch (sub)
        {
            case "status":
            {
                var (exists, settings) = HooksFile.Load(path);
                var on = ToolHooksConfig.IsInstalled(profile, settings);
                ctx.Out.WriteLine($"tool       {tool}");
                ctx.Out.WriteLine($"settings   {path}{(exists ? "" : ctx.Style.Dim(" (does not exist)"))}");
                ctx.Out.WriteLine($"installed  {(on ? ctx.Style.Green("yes") : ctx.Style.Red("no"))}");
                if (profile.Note is { } note) ctx.Out.WriteLine(ctx.Style.Dim(note));
                return Task.FromResult(ExitCodes.Ok);
            }
            case "install":
            {
                var forwarder = ctx.IsWindows ? ctx.Forwarder : null;
                HooksPlan plan;
                try { plan = HooksFile.PlanTool(path, s => ToolHooksConfig.Install(profile, s, socket, forwarder)); }
                catch (ArgumentException ex) { throw new CliException($"cannot build a {profile.WindowsShell} hook command: {ex.Message}"); }
                var rc = Change(ctx, args, plan, "install", tool);
                if (rc == ExitCodes.Ok && profile.Note is { } note) ctx.Out.WriteLine(ctx.Style.Yellow("Next: ") + note);
                return Task.FromResult(rc);
            }
            case "uninstall":
                return Task.FromResult(Change(ctx, args, HooksFile.PlanTool(path, s => ToolHooksConfig.Uninstall(profile, s)), "uninstall", tool));
            default:
                throw new UsageException($"unknown hooks subcommand '{sub}' (status | install | uninstall)");
        }
    }

    private static int Change(CliContext ctx, ParsedArgs args, HooksPlan plan, string verb, string tool = "claude")
    {
        var s = ctx.Style;
        var effective = plan.Changes.Where(l => l[0] is '+' or '-' or '~' or '!').ToList();
        if (!plan.Changed || effective.Count == 0)
        {
            ctx.Out.WriteLine(verb == "install"
                ? $"{s.Green("✓")} hooks already installed and up to date in {plan.SettingsPath}"
                : $"{s.Green("✓")} no agent-mission-control hooks in {plan.SettingsPath}; nothing to do");
            return ExitCodes.Ok;
        }

        ctx.Out.WriteLine($"{(verb == "install" ? "Installing" : "Removing")} {(tool == "claude" ? "Claude Code" : tool)} hooks in {s.Bold(plan.SettingsPath)}" +
                          (plan.TargetPath != Path.GetFullPath(plan.SettingsPath) ? s.Dim($" (→ {plan.TargetPath})") : "") +
                          (plan.Exists ? "" : s.Dim(" (file will be created)")));
        foreach (var line in plan.Changes)
            ctx.Out.WriteLine("  " + (line[0] switch { '+' => s.Green(line), '-' => s.Red(line), '~' => s.Yellow(line), '!' => s.Red(line), _ => s.Dim(line) }));
        if (verb == "install")
            if (tool == "claude") ctx.Out.WriteLine(s.Dim("  hook command: " + Fmt.Trunc(ClaudeHooksConfig.IsInstalled(plan.After) ? FirstOurCommand(plan.After) : "", 200)));

        if (!args.Flag("yes"))
        {
            if (!ctx.Interactive) throw new UsageException("refusing to modify settings without confirmation in a non-interactive session (pass --yes)");
            if (!ctx.Confirm("Apply these changes?"))
            {
                ctx.Err.WriteLine("Aborted; nothing was written.");
                return ExitCodes.Error;
            }
        }

        var backup = HooksFile.Apply(plan, ctx.Now());
        if (backup is not null) ctx.Out.WriteLine(s.Dim($"  backup: {backup}"));
        ctx.Out.WriteLine($"{s.Green("✓")} {verb} complete." + (verb == "install" ? " New sessions pick the hooks up; running sessions may need a restart." : ""));
        return ExitCodes.Ok;
    }

    private static string FirstOurCommand(JsonObject settings) =>
        settings["hooks"] is JsonObject hooks
            ? hooks.Select(kv => kv.Value).OfType<JsonArray>().SelectMany(g => g.OfType<JsonObject>())
                .SelectMany(g => g["hooks"] as JsonArray ?? []).FirstOrDefault(ClaudeHooksConfig.IsOurs) is { } h
                ? string.Join(' ', new[] { h["command"]?.ToString() ?? "" }.Concat((h["args"] as JsonArray ?? []).Select(a => a?.ToString() ?? "")))
                : ""
            : "";

    private static async Task<int> StatusAsync(CliContext ctx, ParsedArgs args, string settingsPath,
        ClaudeHooksConfig.HookCommand expectedCommand, ClaudeHooksConfig.HookCommand expectedRewake, CancellationToken ct)
    {
        var s = ctx.Style;
        var (exists, settings) = HooksFile.Load(settingsPath);
        var hooks = settings["hooks"] as JsonObject;
        var perEvent = ClaudeHooksConfig.Events.ToDictionary(ev => ev, ev =>
            (hooks?[ev] as JsonArray)?.OfType<JsonObject>().SelectMany(g => g["hooks"] as JsonArray ?? [])
            .Where(ClaudeHooksConfig.IsOurs).ToList() ?? []);
        var installed = ClaudeHooksConfig.IsInstalled(settings);
        var stale = perEvent.Values.SelectMany(x => x).Any(h => !expectedCommand.Matches(h) && !expectedRewake.Matches(h))
                    || !ClaudeHooksConfig.IsRewakeInstalled(settings);

        bool reachable;
        string reachDetail;
        try
        {
            var h = await Api.Health(ctx, ct);
            reachable = true;
            reachDetail = $"{ctx.Settings.DaemonUrl} ({h["overall"]})";
        }
        catch (CliException ex)
        {
            reachable = false;
            reachDetail = ex.Message;
        }
        var (transport, transportPath) = Transport(ctx);

        if (args.Json)
        {
            var events = new JsonObject();
            foreach (var (ev, cmds) in perEvent) events[ev] = cmds.Count > 0;
            Api.PrintJson(ctx, new JsonObject
            {
                ["settingsPath"] = settingsPath, ["exists"] = exists, ["installed"] = installed, ["upToDate"] = installed && !stale,
                ["events"] = events, ["daemonReachable"] = reachable, [transport] = transportPath,
            });
            return ExitCodes.Ok;
        }

        ctx.Out.WriteLine($"settings   {settingsPath}{(exists ? "" : s.Dim(" (does not exist)"))}");
        ctx.Out.WriteLine($"installed  {(installed ? s.Green("yes") + (stale ? s.Yellow(" (outdated command — run `agentctl hooks install`)") : "") : perEvent.Values.Any(v => v.Count > 0) ? s.Yellow("partially") : s.Red("no"))}");
        ctx.Out.WriteLine("events     " + string.Join(" ", perEvent.Select(kv => kv.Value.Count > 0 ? s.Green(kv.Key) : s.Dim(kv.Key + "✗"))));
        ctx.Out.WriteLine($"daemon     {s.Ok(reachable)} {reachDetail}");
        ctx.Out.WriteLine($"{transport,-10} {s.Ok(transportPath is not null)} {transportPath ?? (transport == "curl"
            ? "not found on PATH — hooks cannot reach the daemon"
            : $"{ctx.Forwarder.Executable} not found — hooks cannot reach the daemon")}");
        return ExitCodes.Ok;
    }
}
