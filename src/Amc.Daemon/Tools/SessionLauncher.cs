using System.Diagnostics;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Tools;

public sealed record LaunchRequest(string Cwd, string? Model = null, string? Prompt = null, string Tool = "claude");
public sealed record LaunchResult(bool Ok, string Detail, string? TerminalId = null, int? Pid = null, int StatusCode = 200);

/// <summary>
/// Starts a new interactive session of any configured AI tool inside a Overhive-owned PTY (`agentctl run`),
/// so the dashboard's terminal view is its screen and keyboard. The tool signs in with its own account;
/// Overhive passes no credentials. Deliberately narrow: the program is a configured tool's executable; the
/// only inputs are a model (validated, passed with the tool's declared flag), an optional first message
/// (one argv element, never a shell, only for tools that declare how), and a working directory inside the
/// user's home that is a known worktree/repository or inside a workspace root.
/// </summary>
public sealed partial class SessionLauncher(ToolRegistry tools, StateStore state, IEventSink sink, IOptions<AmcOptions> options,
    ILogger<SessionLauncher> log)
{
    private readonly AmcOptions _options = options.Value;

    public async Task<LaunchResult> LaunchAsync(LaunchRequest req, CancellationToken ct)
    {
        if (!_options.EnableControl) return new(false, "Launching sessions is disabled (EnableControl=false)", StatusCode: 403);

        if (tools.Get(req.Tool) is not { } tool) return new(false, $"Unknown tool '{req.Tool}'", StatusCode: 400);
        var cwd = ValidateCwd(req.Cwd, out var why);
        if (cwd is null) return new(false, why, StatusCode: 400);
        if (req.Model is { Length: > 0 } m && (tool.ModelFlag is null || !AgentTool.ValidModel(m)))
            return new(false, tool.ModelFlag is null ? $"{tool.Name} is started with its default model" : "Invalid model name", StatusCode: 400);
        var prompt = req.Prompt?.Trim();
        if (prompt is { Length: > 4000 }) return new(false, "The first message is limited to 4000 characters", StatusCode: 400);
        if (prompt is { Length: > 0 } && tool.PromptArg is null) return new(false, $"Type the first message into {tool.Name}'s terminal once it starts", StatusCode: 400);

        var exe = ToolRegistry.FindExecutable(tool.Command);
        if (exe is null) return new(false, $"{tool.Name} (`{tool.Command}`) is not installed or not on PATH", StatusCode: 409);
        var agentctl = FindAgentctl();
        if (agentctl is null) return new(false, "agentctl was not found next to the daemon or on PATH (run scripts/install.sh)", StatusCode: 409);

        var terminalId = Ulid.NewId();
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
            Data = Json.ToObject(new AuditData("launch", cwd, "ok",
                $"{tool.Command}{(req.Model is { Length: > 0 } ? $" {tool.ModelFlag} {req.Model}" : "")} in terminal {terminalId}")),
        }, ct);
        log.LogInformation("Launched {Tool} in {Cwd} (terminal {Terminal}, pid {Pid})", tool.Name, cwd, terminalId, proc.Id);
        return new(true, $"{tool.Name} is starting in its terminal; it appears in the agent tree within a few seconds", terminalId, proc.Id, 202);
    }

    /// <summary>Real, existing directory under $HOME that is a known worktree/repo root or inside a workspace root.</summary>
    private string? ValidateCwd(string? raw, out string why)
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
        if (!Inside(AmcOptions.Home, real) && !Inside(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", real)) { why = "Working directory must be inside your home directory"; return null; }
        var known = state.Read(s => s.Worktrees.Values.Where(w => !w.Removed).Select(w => w.Root).ToList());
        if (known.Any(r => Inside(r, real)) || _options.EffectiveWorkspaceRoots.Any(r => Inside(r, real)))
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
