using System.Diagnostics;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Tools;

/// <param name="Worktree">When set, a new git worktree is created from the repository at <paramref name="Cwd"/> and the tool starts there.</param>
public sealed record LaunchRequest(string Cwd, string? Model = null, string? Prompt = null, string Tool = "claude", NewWorktreeSpec? Worktree = null);
/// <param name="WorktreePath">The worktree created for this session (also reported when a later step failed: it is left in place).</param>
public sealed record LaunchResult(bool Ok, string Detail, string? TerminalId = null, int? Pid = null, int StatusCode = 200,
    string? WorktreePath = null, string? WorktreeId = null, IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Starts a new interactive session of any configured AI tool inside a Overhive-owned PTY (`agentctl run`),
/// so the dashboard's terminal view is its screen and keyboard. The tool signs in with its own account;
/// Overhive passes no credentials. Deliberately narrow: the program is a configured tool's executable; the
/// only inputs are a model (validated, passed with the tool's declared flag), an optional first message
/// (one argv element, never a shell, only for tools that declare how), and a working directory inside the
/// user's home that is a known worktree/repository or inside a workspace root.
/// </summary>
public sealed partial class SessionLauncher(ToolRegistry tools, StateStore state, IEventSink sink, IOptions<AmcOptions> options,
    ILogger<SessionLauncher> log, NewWorktreeService? worktrees = null)
{
    private readonly AmcOptions _options = options.Value;

    /// <summary>Test hook: the agentctl to run instead of the one next to the daemon / on PATH.</summary>
    internal string? AgentctlOverride { get; set; }

    public async Task<LaunchResult> LaunchAsync(LaunchRequest req, CancellationToken ct)
    {
        if (!_options.EnableControl) return new(false, "Launching sessions is disabled (EnableControl=false)", StatusCode: 403);

        if (tools.Get(req.Tool) is not { } tool) return new(false, $"Unknown tool '{req.Tool}'", StatusCode: 400);
        string? cwd;
        if (req.Worktree is null)
        {
            cwd = ValidateCwd(req.Cwd, state, _options, out var why);
            if (cwd is null) return new(false, why, StatusCode: 400);
        }
        else
        {
            if (worktrees is null) return new(false, "Starting in a new worktree is not available", StatusCode: 501);
            if (string.IsNullOrWhiteSpace(req.Worktree.Branch)) return new(false, "Enter a branch name for the new worktree", StatusCode: 400);
            cwd = null; // the new worktree (validated with the repository folder by NewWorktreeService)
        }
        if (req.Model is { Length: > 0 } m && (tool.ModelFlag is null || !AgentTool.ValidModel(m)))
            return new(false, tool.ModelFlag is null ? $"{tool.Name} is started with its default model" : "Invalid model name", StatusCode: 400);
        var prompt = req.Prompt?.Trim();
        if (prompt is { Length: > 4000 }) return new(false, "The first message is limited to 4000 characters", StatusCode: 400);
        if (prompt is { Length: > 0 } && tool.PromptArg is null) return new(false, $"Type the first message into {tool.Name}'s terminal once it starts", StatusCode: 400);

        var exe = ToolRegistry.FindExecutable(tool.Command);
        if (exe is null) return new(false, $"{tool.Name} (`{tool.Command}`) is not installed or not on PATH", StatusCode: 409);
        var agentctl = AgentctlOverride ?? FindAgentctl();
        if (agentctl is null) return new(false, "agentctl was not found next to the daemon or on PATH (run scripts/install.sh)", StatusCode: 409);

        var terminalId = Ulid.NewId();
        // Everything that can be checked is checked before the worktree exists. From here on a failure leaves it in
        // place (never deleted automatically) and the result says where it is.
        NewWorktreeResult? wt = null;
        if (req.Worktree is { } spec)
        {
            wt = await worktrees!.CreateAsync(new CreateWorktreeRequest(req.Cwd, spec.Branch, spec.Base, spec.Path, spec.ExistingBranch,
                spec.Fetch, terminalId, tool.Id), "dashboard", ct);
            if (!wt.Ok) return new(false, wt.Error ?? "Could not create the worktree", StatusCode: wt.StatusCode);
            cwd = NewWorktreeService.Native(wt.Path!);
            if (spec.RunSetup)
            {
                var setup = await worktrees.RunSetupAsync(wt, ct);
                if (!setup.Ok) return await FailedAfterWorktreeAsync(wt, tool, terminalId, setup.Detail, ct);
            }
        }
        if (cwd is null) throw new InvalidOperationException("no working directory");
        var psi = new ProcessStartInfo(agentctl)
        {
            WorkingDirectory = cwd, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "run", "--id", terminalId, "--cols", "140", "--rows", "40", "--", exe }) psi.ArgumentList.Add(a);
        if (req.Model is { Length: > 0 } model) { psi.ArgumentList.Add(tool.ModelFlag!); psi.ArgumentList.Add(model); }
        if (prompt is { Length: > 0 })
        {
            if (tool.PromptArg != "positional") psi.ArgumentList.Add(tool.PromptArg!);
            psi.ArgumentList.Add(prompt);
        }
        psi.Environment.Remove("CLAUDECODE");                  // never look like a child of another Claude session
        psi.Environment.Remove("CLAUDE_CODE_CHILD_SESSION");

        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("process did not start"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (wt is not null) return await FailedAfterWorktreeAsync(wt, tool, terminalId, "Could not start the session: " + ex.Message, ct);
            return new(false, "Could not start the session: " + ex.Message, StatusCode: 500);
        }
        proc.StandardInput.Close();                            // no local keyboard: input comes from the dashboard
        proc.OutputDataReceived += (_, _) => { };              // the screen is mirrored to the daemon; drain the local copy
        proc.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) log.LogDebug("launched session {Id}: {Line}", terminalId, d); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => proc.Dispose();

        await sink.PublishAsync(new AgentEvent
        {
            Type = EventTypes.AuditControl, Source = EventSources.User, Evidence = Evidence.Observed,
            WorktreeId = wt?.WorktreeId,
            Data = Json.ToObject(new AuditData("launch", cwd, "ok",
                $"{tool.Command}{(req.Model is { Length: > 0 } ? $" {tool.ModelFlag} {req.Model}" : "")} in terminal {terminalId}" +
                (wt is not null ? $"; created {wt.Describe()}" : ""))),
        }, ct);
        log.LogInformation("Launched {Tool} in {Cwd} (terminal {Terminal}, pid {Pid})", tool.Name, NewWorktreeService.LogSafe(cwd), terminalId, proc.Id);
        var detail = $"{tool.Name} is starting in its terminal; it appears in the agent tree within a few seconds";
        if (wt is not null) detail = $"Created {wt.Describe()}. " + detail;
        return new(true, detail, terminalId, proc.Id, 202, wt?.Path, wt?.WorktreeId, wt?.Warnings);
    }

    /// <summary>A step after the worktree was created failed: audit it and report the worktree, which stays.</summary>
    private async Task<LaunchResult> FailedAfterWorktreeAsync(NewWorktreeResult wt, AgentTool tool, string terminalId, string why, CancellationToken ct)
    {
        await sink.PublishAsync(new AgentEvent
        {
            Type = EventTypes.AuditControl, Source = EventSources.User, Evidence = Evidence.Observed, WorktreeId = wt.WorktreeId,
            Data = Json.ToObject(new AuditData("launch", wt.Path!, "failed", $"{tool.Command} in terminal {terminalId}: {why}; created {wt.Describe()} (left in place)")),
        }, ct);
        log.LogWarning("Session start in new worktree {Path} failed: {Why}", NewWorktreeService.LogSafe(wt.Path), NewWorktreeService.LogSafe(why));
        return new(false, $"{why}. The worktree was created and left in place: {wt.Path} (branch {wt.Branch}).", StatusCode: 500,
            WorktreePath: wt.Path, WorktreeId: wt.WorktreeId, Warnings: wt.Warnings);
    }

    /// <summary>Inside the user's home directory or the temp directory (the only places sessions and new worktrees may live).</summary>
    internal static bool InsideAllowedRoot(string path)
    {
        if (PathText.IsUnder(AmcOptions.Home, path)) return true;
        if (!OperatingSystem.IsWindows() && PathText.IsUnder("/tmp", path)) return true;
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        return PathText.IsUnder(temp, path) || Collectors.Git.GitPaths.RealPath(temp) is { } realTemp && PathText.IsUnder(realTemp, path);
    }

    /// <summary>Real, existing directory under $HOME that is a known worktree/repo root or inside a workspace root.</summary>
    internal static string? ValidateCwd(string? raw, StateStore state, AmcOptions options, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\0') || !PathText.IsAbsolute(raw)) { why = "Working directory must be an absolute path"; return null; }
        string real;
        try
        {
            var di = new DirectoryInfo(raw);
            if (!di.Exists) { why = "Working directory does not exist"; return null; }
            real = (di.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo)?.FullName ?? di.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { why = "Working directory is not accessible"; return null; }
        real = Path.TrimEndingDirectorySeparator(real);
        static bool Inside(string root, string p) => PathText.IsUnder(root, p);
        if (!InsideAllowedRoot(real)) { why = "Working directory must be inside your home directory"; return null; }
        var known = state.Read(s => s.Worktrees.Values.Where(w => !w.Removed).Select(w => w.Root).ToList());
        if (known.Any(r => Inside(r, real)) || options.EffectiveWorkspaceRoots.Any(r => Inside(r, real)))
            return real;
        why = "Working directory must be a known repository/worktree or inside a workspace root";
        return null;
    }

    private static string? FindAgentctl()
    {
        var sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "cli", "agentctl"));
        return ToolRegistry.Executables(sibling).FirstOrDefault(File.Exists) ?? ToolRegistry.FindExecutable("agentctl");
    }

}
