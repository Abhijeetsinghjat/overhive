using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Amc.Daemon.Infrastructure;

namespace Amc.Daemon.Collectors.Git;

public sealed record GitResult(int ExitCode, string Stdout, string Stderr, bool Truncated = false, bool TimedOut = false)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Runs git without a shell (ArgumentList only), never taking optional locks (so an agent's
/// own git commands are never blocked by our index.lock), never prompting, with a timeout,
/// an output cap and a global concurrency limit.
/// </summary>
public sealed class GitCli(HealthRegistry health, ILogger<GitCli> log)
{
    public const string HealthName = "git";
    public const int DefaultMaxOutput = 16 * 1024 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] Prefix =
        ["--no-pager", "-c", "core.fsmonitor=false", "-c", "core.quotepath=false", "-c", "color.ui=false"];

    /// <summary>Inherited variables that would redirect git away from the directory we ask about.</summary>
    private static readonly string[] Scrubbed =
        ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_NAMESPACE",
         "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CEILING_DIRECTORIES", "GIT_EXTERNAL_DIFF", "GIT_PAGER", "PAGER"];

    private readonly SemaphoreSlim _gate = new(4, 4);

    public async Task<GitResult> RunAsync(string workDir, IReadOnlyList<string> args, CancellationToken ct = default,
        int maxOutput = DefaultMaxOutput, TimeSpan? timeout = null)
    {
        await _gate.WaitAsync(ct);
        try { return await RunCoreAsync(workDir, args, maxOutput, timeout ?? DefaultTimeout, ct); }
        finally { _gate.Release(); }
    }

    private async Task<GitResult> RunCoreAsync(string workDir, IReadOnlyList<string> args, int maxOutput, TimeSpan timeout, CancellationToken ct)
    {
        health.Increment(HealthName, "git_calls");
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in Prefix) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var v in Scrubbed) psi.Environment.Remove(v);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["LC_ALL"] = "C";

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!Directory.Exists(workDir)) return new GitResult(-1, "", $"directory not found: {workDir}");
            proc.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            health.Increment(HealthName, "git_errors");
            log.LogWarning("git could not be started in {Dir}: {Error}", workDir, ex.Message);
            return new GitResult(-1, "", ex.Message);
        }
        proc.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var truncated = false;
        var stderrTask = ReadCappedAsync(proc.StandardError, 64 * 1024, cts.Token);
        var stdout = new StringBuilder();
        try
        {
            var buf = new char[16 * 1024];
            int n;
            while ((n = await proc.StandardOutput.ReadAsync(buf, cts.Token)) > 0)
            {
                if (stdout.Length + n > maxOutput)
                {
                    stdout.Append(buf, 0, maxOutput - stdout.Length);
                    truncated = true;
                    Kill(proc);
                    break;
                }
                stdout.Append(buf, 0, n);
            }
            var stderr = await stderrTask;
            await proc.WaitForExitAsync(cts.Token);
            return new GitResult(truncated ? 0 : proc.ExitCode, stdout.ToString(), stderr, truncated);
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            if (ct.IsCancellationRequested) throw;
            health.Increment(HealthName, "git_errors");
            log.LogWarning("git {Command} timed out after {Timeout}s in {Dir}", args.FirstOrDefault(), timeout.TotalSeconds, workDir);
            return new GitResult(-1, stdout.ToString(), "timed out", truncated, TimedOut: true);
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader r, int cap, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        int n;
        while ((n = await r.ReadAsync(buf, ct)) > 0)
            if (sb.Length < cap) sb.Append(buf, 0, Math.Min(n, cap - sb.Length));
        return sb.ToString();
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
    }
}
