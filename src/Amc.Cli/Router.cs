using Amc.Cli.Commands;
using Amc.Cli.Infrastructure;

namespace Amc.Cli;

/// <summary>Command table and dispatch. Maps exceptions to exit codes (0 ok, 1 error, 2 usage, 3 daemon unreachable).</summary>
public static class Router
{
    private static readonly string[] AgentFilter = ["agent"];

    public static readonly IReadOnlyList<CommandSpec> Commands =
    [
        new("status", "status [--json]", "Daemon health, agent counts and conflicts", StatusCommand.RunAsync),
        new("agents", "agents [--all] [--json]", "Agent tree (finished agents older than 1h hidden)", AgentsCommand.RunAsync, Flags: ["all"]),
        new("attention", "attention [--all] [--json]", "What needs you: agents waiting or blocked, failures, conflicts (all tools)",
            AttentionCommand.RunAsync, Flags: ["all"]),
        new("inspect", "inspect <agent> [--json]", "Full detail of one agent", InspectCommand.RunAsync, 1, 1),
        new("worktrees", "worktrees [--all] [--json]", "Repositories' worktrees, state and owners", WorktreesCommand.RunAsync, Flags: ["all"]),
        new("emit", "emit <kind> --tool T --agent ID [--parent ID] [--title X] [--cwd DIR] [--pid N] [--model M] [--data JSON | stdin]",
            "Report an agent event through the open protocol (for any AI tool's hooks or wrappers)", EmitCommand.RunAsync, 1, 1,
            Values: ["tool", "agent", "parent", "title", "cwd", "pid", "model", "type", "data", "status", "text", "path", "change", "reason"]),
        new("inbox", "inbox --tool T --agent ID [--wait SECONDS] [--json]", "Print messages queued for an agent (open protocol)",
            InboxCommand.RunAsync, Values: ["tool", "agent", "wait"]),
        new("usage", "usage [--agents] [--json]", "Token usage per session (incl. sub-agents), per agent and per model", UsageCommand.RunAsync, Flags: ["agents"]),
        new("files", "files [--agent A] [--worktree W] [--json]", "Changed files with attribution", FilesCommand.RunAsync, Values: ["agent", "worktree"]),
        new("events", "events [--agent A] [--type T]... [--file F] [--worktree W] [--since 10m] [--limit N] [-f]", "Event log (follow with -f)",
            EventsCommand.RunAsync, Flags: ["follow"], Values: ["agent", "type", "file", "worktree", "since", "limit"]),
        new("logs", "logs <agent> [-f] [--since 10m] [--limit N] [--lines N]", "Command/tool activity incl. stdout/stderr",
            LogsCommand.RunAsync, 1, 1, Flags: ["follow"], Values: ["since", "limit", "lines"]),
        new("diff", "diff <agent> | diff --worktree W [--path P]", "Diff of an agent's files or of a worktree", DiffCommand.RunAsync, 0, 1, Values: ["worktree", "path"]),
        new("message", "message <agent> \"text\"  (text '-' reads stdin)", "Send a message to an agent", MessageCommand.RunAsync, 2, int.MaxValue),
        new("pause", "pause <agent>", "SIGSTOP the agent's process group", ControlCommand.RunAsync, 1, 1),
        new("resume", "resume <agent>", "SIGCONT a paused agent", ControlCommand.RunAsync, 1, 1),
        new("interrupt", "interrupt <agent>", "Interrupt the current turn (PTY sessions only)", ControlCommand.RunAsync, 1, 1),
        new("stop", "stop <agent> [--yes]", "Terminate (SIGTERM) — asks for confirmation", ControlCommand.RunAsync, 1, 1, Flags: ["yes"]),
        new("kill", "kill <agent> [--yes]", "Kill (SIGKILL) — asks for confirmation", ControlCommand.RunAsync, 1, 1, Flags: ["yes"]),
        new("hooks", "hooks status|install|uninstall [--tool claude|codex|gemini|grok|cursor|copilot] [--settings PATH] [--yes]",
            "Manage an AI tool's hooks so it reports to Overhive (default tool: claude)",
            HooksCommand.RunAsync, 0, 1, Flags: ["yes"], Values: ["settings", "port", "tool"]),
        new("doctor", "doctor [--settings PATH] [--json]", "Diagnose the installation", DoctorCommand.RunAsync, Values: ["settings"]),
        new("run", "run [--id ID] [--cols N] [--rows N] -- <cmd> [args...]", "Run an agent under a PTY owned by Overhive", RunAsync,
            AcceptsRest: true, Values: ["id", "cols", "rows"]),
        new("help", "help [command]", "Show help", HelpAsync, 0, 1),
        new("version", "version", "Print the version", VersionAsync),
    ];

    public static CommandSpec? Find(string name) =>
        Commands.FirstOrDefault(c => c.Name == name);

    public static async Task<int> RunAsync(string[] argv, CliContext ctx, CancellationToken ct)
    {
        try
        {
            var args = ArgParser.Parse(argv, Find);
            if (args.Flag("no-color")) ctx.Color = false;
            if (args.Opt("url") is { } url) ctx.UrlOverride = url;
            if (args.Command.Length == 0)
            {
                WriteHelp(ctx, null);
                return args.Flag("help") ? ExitCodes.Ok : ExitCodes.Usage;
            }
            var spec = Find(args.Command)!;
            if (args.Flag("help"))
            {
                WriteHelp(ctx, spec);
                return ExitCodes.Ok;
            }
            return await spec.Handler(ctx, args, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ExitCodes.Interrupted;
        }
        catch (CliException ex)
        {
            ctx.Err.WriteLine($"agentctl: {ex.Message}");
            if (ex is UsageException && !ex.Message.Contains('\n')) ctx.Err.WriteLine("Run 'agentctl help' for usage.");
            return ex.ExitCode;
        }
    }

    private static Task<int> HelpAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        CommandSpec? spec = null;
        if (args.Positionals.Count > 0) spec = Find(args.Positionals[0]) ?? throw new UsageException($"unknown command '{args.Positionals[0]}'");
        WriteHelp(ctx, spec);
        return Task.FromResult(ExitCodes.Ok);
    }

    private static Task<int> VersionAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        ctx.Out.WriteLine($"{Amc.Core.Product.Name} {Amc.Core.Product.Describe(typeof(Router).Assembly.GetName().Version)}");
        return Task.FromResult(ExitCodes.Ok);
    }

    private static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        if (args.Rest.Count == 0) throw new UsageException("nothing to run. Usage: agentctl run -- <cmd> [args...]");
        var options = new Run.RunOptions { DaemonUrl = ctx.Settings.DaemonUrl.ToString().TrimEnd('/'), Token = ctx.Token, Cwd = ctx.Cwd };
        if (args.Opt("id") is { } id)
            options.TerminalId = System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$") ? id : throw new UsageException("invalid --id");
        if (args.Opt("cols") is { } c) options.Cols = int.TryParse(c, out var cols) && cols is > 10 and < 1000 ? cols : throw new UsageException("invalid --cols");
        if (args.Opt("rows") is { } r) options.Rows = int.TryParse(r, out var rows) && rows is > 5 and < 500 ? rows : throw new UsageException("invalid --rows");
        return await Run.RunCommand.RunAsync(args.Rest.ToArray(), options, ct);
    }

    public static void WriteHelp(CliContext ctx, CommandSpec? spec)
    {
        var w = ctx.Out;
        if (spec is not null)
        {
            w.WriteLine($"usage: agentctl {spec.Usage}");
            w.WriteLine();
            w.WriteLine("  " + spec.Summary);
            w.WriteLine();
            w.WriteLine("Agents may be referenced by short id, any unique id prefix, or a unique title substring.");
            return;
        }
        w.WriteLine("agentctl — command line for Overhive (amcd)");
        w.WriteLine();
        w.WriteLine("usage: agentctl <command> [options]");
        w.WriteLine();
        foreach (var c in Commands) w.WriteLine($"  {c.Name,-10} {c.Summary}");
        w.WriteLine();
        w.WriteLine("Global options: --json (raw JSON), --no-color, --url URL, -h/--help");
        w.WriteLine("Environment:    AMC_URL (daemon URL), NO_COLOR, XDG_STATE_HOME / XDG_CONFIG_HOME");
        w.WriteLine("Exit codes:     0 ok, 1 error, 2 usage, 3 daemon unreachable");
    }
}
