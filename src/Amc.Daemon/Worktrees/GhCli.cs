using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Amc.Daemon.Worktrees;

/// <param name="NotInstalled">gh could not be started (not on PATH).</param>
public sealed record GhResult(int ExitCode, string Stdout, string Stderr, bool NotInstalled = false)
{
    public bool Ok => ExitCode == 0 && !NotInstalled;
}

/// <summary>Runs the GitHub CLI. An interface so tests can stand in for GitHub.</summary>
public interface IGitHubCli
{
    Task<GhResult> RunAsync(string workDir, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>
/// Runs <c>gh</c> (<c>gh.exe</c> on Windows, resolved through PATH) without a shell, never prompting, with a timeout
/// and an output cap. Authentication is entirely gh's own: Overhive never reads, passes or stores a GitHub token.
/// </summary>
public sealed class GhCli(ILogger<GhCli> log) : IGitHubCli
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int MaxOutput = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public async Task<GhResult> RunAsync(string workDir, IReadOnlyList<string> args, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await RunCoreAsync(workDir, args, ct); }
        finally { _gate.Release(); }
    }

    private async Task<GhResult> RunCoreAsync(string workDir, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh")
        {
            WorkingDirectory = workDir, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        psi.Environment["GH_SPINNER_DISABLED"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["GH_PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var v in new[] { "GIT_DIR", "GIT_WORK_TREE" }) psi.Environment.Remove(v);

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!Directory.Exists(workDir)) return new GhResult(-1, "", $"directory not found: {workDir}");
            proc.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new GhResult(-1, "", ex.Message, NotInstalled: true);
        }
        proc.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var stdout = ReadCappedAsync(proc.StandardOutput, MaxOutput, cts.Token);
            var stderr = ReadCappedAsync(proc.StandardError, 64 * 1024, cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return new GhResult(proc.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            if (ct.IsCancellationRequested) throw;
            log.LogWarning("gh {Command} timed out after {Timeout}s", string.Join(' ', args.Take(2)), Timeout.TotalSeconds);
            return new GhResult(-1, "", "timed out");
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader r, int cap, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        int n;
        while ((n = await r.ReadAsync(buf, ct)) > 0)
            if (sb.Length < cap) sb.Append(buf, 0, Math.Min(n, cap - sb.Length));
        return sb.ToString();
    }
}
