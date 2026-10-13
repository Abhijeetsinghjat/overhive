using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Cli.Native;

namespace Amc.Cli.Run;

/// <summary>
/// Fallback when the native PTY path is unavailable: `script -qfec "&lt;cmd&gt;" /dev/null` allocates the
/// PTY and we talk to it over pipes. Limitations: the PTY size is fixed at start (set via stty inside
/// the script command; SIGWINCH/resizes are not propagated), and exit codes come from script(1).
/// </summary>
internal static class ScriptFallback
{
    public static async Task<int> RunAsync(string[] argv, RunOptions o, string terminalId, string cwd,
        IDictionary<string, string?> env, WinSize size, CancellationToken ct)
    {
        var inner = $"stty rows {size.Rows} cols {size.Cols} 2>/dev/null; exec {ShellQuote(argv)}";
        var psi = new ProcessStartInfo("script")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = false,
            UseShellExecute = false, WorkingDirectory = cwd,
        };
        foreach (var a in new[] { "-q", "-f", "-e", "-c", inner, "/dev/null" }) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("cannot start script(1)");
        var interactive = o.Output is null && o.Input is null && Libc.IsTty(0);
        using var raw = interactive ? RawModeGuard.Enter(0) : null;
        var stdin = proc.StandardInput.BaseStream;
        var stdinLock = new SemaphoreSlim(1, 1);
        async Task WriteIn(byte[] b)
        {
            await stdinLock.WaitAsync();
            try { await stdin.WriteAsync(b); await stdin.FlushAsync(); }
            catch (IOException) { }
            finally { stdinLock.Release(); }
        }

        DaemonLink? link = null;
        if (!string.IsNullOrEmpty(o.DaemonUrl))
        {
            link = new DaemonLink(DaemonLink.AttachUri(o.DaemonUrl, terminalId), o.Token,
                () => new JsonObject
                {
                    ["id"] = terminalId, ["command"] = RunCommand.Display(argv), ["cwd"] = cwd, ["pid"] = proc.Id,
                    ["cols"] = (int)size.Cols, ["rows"] = (int)size.Rows,
                },
                b => _ = WriteIn(b), (_, _) => { },
                msg => Libc.WriteAll(2, Encoding.UTF8.GetBytes($"\x1b[2m[agentctl] {msg}\x1b[0m\r\n")));
            link.Start();
        }

        using var stop = new CancellationTokenSource();
        var output = Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            var src = proc.StandardOutput.BaseStream;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                if (o.Output is not null) o.Output.Write(buf, 0, n); else Libc.WriteAll(1, buf.AsSpan(0, n));
                link?.EnqueueOutput(buf.AsSpan(0, n));
            }
        });
        _ = Task.Run(async () =>
        {
            var buf = new byte[16 * 1024];
            try
            {
                if (o.Input is not null)
                {
                    int n;
                    while ((n = await o.Input.ReadAsync(buf, stop.Token)) > 0) await WriteIn(buf[..n]);
                }
                else
                {
                    using var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(0, false), FileAccess.Read);
                    int n;
                    while (!stop.IsCancellationRequested && (n = fs.Read(buf)) > 0) await WriteIn(buf[..n]);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        });

        using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        await proc.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAny(output, Task.Delay(1000));
        await stop.CancelAsync();
        raw?.Restore();
        if (link is not null)
        {
            await link.CompleteAsync(proc.ExitCode, TimeSpan.FromSeconds(1));
            await link.DisposeAsync();
        }
        return proc.ExitCode;
    }

    internal static string ShellQuote(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => "'" + a.Replace("'", "'\\''") + "'"));
}
