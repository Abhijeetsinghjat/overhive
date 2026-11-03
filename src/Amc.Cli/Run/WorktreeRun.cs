using System.Diagnostics;
using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;

namespace Amc.Cli.Run;

/// <summary>
/// <c>agentctl run --worktree BRANCH [--base REF] [--path DIR] [--existing] [--fetch] [--setup] -- cmd</c>: asks the
/// daemon to create a git worktree of the repository at the current directory (same validation and records as the
/// dashboard's "+ New session" → "a new worktree"), optionally runs the repository's configured setup command there in
/// this terminal, and returns the worktree's path for the PTY run. Nothing is ever deleted: when a later step fails the
/// worktree stays and its path is printed.
/// </summary>
public static class WorktreeRun
{
    /// <summary>Test hook: runs the setup command (args, cwd) and returns its exit code; default: inherit this terminal.</summary>
    public static Func<IReadOnlyList<string>, string, int>? SetupRunner { get; set; }

    public static async Task<string> PrepareAsync(CliContext ctx, ParsedArgs args, string terminalId, CancellationToken ct)
    {
        var branch = args.Opt("worktree")!;
        if (args.Rest.Count == 0) throw new UsageException("nothing to run. Usage: agentctl run --worktree BRANCH -- <cmd> [args...]");
        if (args.Flag("existing") && args.Opt("base") is not null) throw new UsageException("--base applies to a new branch, not with --existing");
        var path = args.Opt("path") is { } p ? Path.GetFullPath(p, ctx.Cwd) : null;
        var tool = Path.GetFileNameWithoutExtension(args.Rest[0]);

        // Creating a worktree can take a while on large repositories (and --fetch talks to the remote).
        using var client = new AmcClient(ctx.Settings.DaemonUrl, ctx.Token, ctx.HttpHandler, TimeSpan.FromMinutes(8));
        var r = await client.SendAsync(HttpMethod.Post, "api/new-worktree", new
        {
            cwd = ctx.Cwd, branch, @base = args.Opt("base"), path, existingBranch = args.Flag("existing"), fetch = args.Flag("fetch"),
            terminalId, tool,
        }, ct);
        if (!r.Success) throw r.Status is 400 or 403 or 409 or 502 ? new CliException(r.ErrorText()) : AmcClient.ToException(r, "api/new-worktree");
        var node = r.Node as JsonObject ?? throw new CliException("unexpected response from api/new-worktree");
        var wtPath = (string?)node["path"] ?? throw new CliException("the daemon did not return the worktree path");
        var native = OperatingSystem.IsWindows() ? wtPath.Replace('/', '\\') : wtPath;
        var s = ctx.Style;
        var newBranch = (bool?)node["newBranch"] == true;
        ctx.Err.WriteLine($"{s.Green("✓")} Created worktree {wtPath} on branch {branch}" +
                          (newBranch ? $" (new, from {(string?)node["base"]})" : " (existing branch)"));
        foreach (var w in node["warnings"] as JsonArray ?? []) ctx.Err.WriteLine($"! {(string?)w}");

        var setup = (node["setupArgs"] as JsonArray)?.Select(a => (string?)a ?? "").ToList();
        if (args.Flag("setup"))
        {
            if (setup is not { Count: > 0 })
                ctx.Err.WriteLine("! No setup command is configured for this repository (NewWorktree:SetupCommand); skipped.");
            else
            {
                ctx.Err.WriteLine($"Running setup: {(string?)node["setupCommand"]}");
                var code = (SetupRunner ?? RunInherited)(setup, native);
                if (code != 0)
                    throw new CliException($"setup command exited with {code}. The worktree was left in place: {wtPath}");
            }
        }
        else if (setup is { Count: > 0 })
            ctx.Err.WriteLine($"Note: this repository has a setup command ({(string?)node["setupCommand"]}); add --setup to run it first.");
        return native;
    }

    /// <summary>Runs the setup command in this terminal (stdio inherited), without a shell.</summary>
    private static int RunInherited(IReadOnlyList<string> args, string cwd)
    {
        var psi = new ProcessStartInfo(Resolve(args[0])) { WorkingDirectory = cwd, UseShellExecute = false };
        foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new CliException($"could not start {args[0]}");
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new CliException($"could not start the setup command {args[0]}: {e.Message}. The worktree was left in place: {cwd}");
        }
    }

    /// <summary>On Windows an extension-less name ("npm") runs its PATHEXT variant ("npm.cmd"); elsewhere PATH lookup is the OS's.</summary>
    private static string Resolve(string command)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(command) || Path.IsPathRooted(command)) return command;
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") is { Length: > 0 } e ? e : ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
                if (File.Exists(Path.Combine(dir, command + ext))) return Path.Combine(dir, command + ext);
        return command;
    }
}
