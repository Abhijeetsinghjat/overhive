using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Cli.Native;
using Amc.Core.Events;

namespace Amc.Cli.Run;

public sealed class RunOptions
{
    /// <summary>e.g. http://127.0.0.1:47821. Null = run locally without mirroring.</summary>
    public string? DaemonUrl { get; set; }
    public string? Token { get; set; }
    /// <summary>Working directory for the child (default: current directory).</summary>
    public string? Cwd { get; set; }

    // ---- optional; for embedding and tests ---------------------------------------------
    /// <summary>Terminal id (default: a new ULID). Exported to the child as AMC_TERMINAL_ID.</summary>
    public string? TerminalId { get; set; }
    /// <summary>When set, PTY output goes here instead of fd 1 and the local terminal is left untouched (no raw mode).</summary>
    public Stream? Output { get; set; }
    /// <summary>When set, input is read from here instead of fd 0.</summary>
    public Stream? Input { get; set; }
    /// <summary>PTY size when it cannot be taken from the local terminal.</summary>
    public int Cols { get; set; } = 80;
    public int Rows { get; set; } = 24;
    /// <summary>Force the `script(1)` fallback instead of the native PTY (also: AMC_PTY_MODE=script).</summary>
    public bool UseScriptFallback { get; set; }
}

/// <summary>
/// `agentctl run -- &lt;cmd&gt; [args]`: runs the command on a PTY owned by us, relays it to the user's
/// terminal (raw mode, always restored) and mirrors it to the daemon so the UI can watch and type.
/// </summary>
public static class RunCommand
{
    private const int StdIn = 0, StdOut = 1, StdErr = 2;

    public static async Task<int> RunAsync(string[] commandAndArgs, RunOptions o, CancellationToken ct)
    {
        if (commandAndArgs is ["--", .. var rest]) commandAndArgs = rest; // tolerate an unstripped separator
        if (commandAndArgs.Length == 0)
        {
            WriteErr("usage: agentctl run -- <command> [args...]\n");
            return 2;
        }
        var terminalId = o.TerminalId ?? Ulid.NewId();
        var cwd = o.Cwd ?? Environment.CurrentDirectory;
        var interactive = o.Output is null && o.Input is null && Libc.IsTty(StdIn);
        var size = (interactive ? Libc.GetWinSize(StdIn) ?? Libc.GetWinSize(StdOut) : null) ?? new WinSize(o.Cols, o.Rows);
        var env = new Dictionary<string, string?> { ["AMC_TERMINAL_ID"] = terminalId };
        if (Environment.GetEnvironmentVariable("TERM") is null or "") env["TERM"] = "xterm-256color";

        if (o.UseScriptFallback || Environment.GetEnvironmentVariable("AMC_PTY_MODE") == "script")
            return await ScriptFallback.RunAsync(commandAndArgs, o, terminalId, cwd, env, size, ct);

        PtyProcess pty;
        try { pty = PtyProcess.Spawn(commandAndArgs, cwd, env, size); }
        catch (PtySpawnException e) when (e.Errno is Libc.ENOENT or Libc.EACCES)
        {
            WriteErr($"agentctl: {e.Message}\n");
            return e.Errno == Libc.ENOENT ? 127 : 126;
        }
        catch (Exception e) when (e is PtySpawnException or DllNotFoundException or EntryPointNotFoundException)
        {
            WriteErr($"agentctl: native PTY unavailable ({e.Message}); falling back to script(1)\n");
            return await ScriptFallback.RunAsync(commandAndArgs, o, terminalId, cwd, env, size, ct);
        }

        using var _ = pty;
        RawModeGuard? raw = interactive ? RawModeGuard.Enter(StdIn) : null;
        void Restore() => raw?.Restore();
        EventHandler onExit = (_, _) => Restore();
        UnhandledExceptionEventHandler onCrash = (_, _) => Restore();
        AppDomain.CurrentDomain.ProcessExit += onExit;
        AppDomain.CurrentDomain.UnhandledException += onCrash;

        var sizeBox = new System.Runtime.CompilerServices.StrongBox<WinSize>(size);
        DaemonLink? link = null;
        if (!string.IsNullOrEmpty(o.DaemonUrl))
        {
            link = new DaemonLink(
                DaemonLink.AttachUri(o.DaemonUrl, terminalId), o.Token,
                () => new JsonObject
                {
                    ["id"] = terminalId, ["command"] = Display(commandAndArgs), ["cwd"] = cwd, ["pid"] = pty.Pid,
                    ["cols"] = (int)sizeBox.Value.Cols, ["rows"] = (int)sizeBox.Value.Rows,
                },
                input => pty.Write(input),
                (cols, rows) => pty.Resize(new WinSize(cols, rows)),
                msg => WriteErr($"\x1b[2m[agentctl] {msg}\x1b[0m" + (raw is null ? "\n" : "\r\n")));
            link.Start();
        }

        var signals = new List<PosixSignalRegistration>();
        if (interactive && !OperatingSystem.IsWindows())
        {
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ =>
            {
                if (Libc.GetWinSize(StdIn) is { } ws && pty.Resize(ws))
                {
                    sizeBox.Value = ws;
                    link?.SendResize(ws.Cols, ws.Rows);
                }
            }));
            // In raw mode ^C/^\ reach the child as bytes; these only arrive from kill(1) or a hangup — forward them.
            foreach (var (ps, num) in new[] { (PosixSignal.SIGINT, Libc.SIGINT), (PosixSignal.SIGTERM, Libc.SIGTERM),
                         (PosixSignal.SIGHUP, Libc.SIGHUP), (PosixSignal.SIGQUIT, Libc.SIGQUIT) })
                signals.Add(PosixSignalRegistration.Create(ps, ctx => { ctx.Cancel = true; pty.SignalGroup(num); }));
        }

        using var stopInput = new CancellationTokenSource();
        var exitTask = Task.Factory.StartNew(pty.WaitForExit, TaskCreationOptions.LongRunning);
        using var ctReg = ct.Register(() => pty.SignalGroup(Libc.SIGHUP));

        var outputSink = o.Output;
        var outputThread = new Thread(() => PumpOutput(pty, exitTask, data =>
        {
            if (outputSink is not null) outputSink.Write(data);
            else Libc.WriteAll(StdOut, data);
            link?.EnqueueOutput(data);
        })) { IsBackground = true, Name = "pty-output" };
        outputThread.Start();

        Task inputTask = o.Input is not null
            ? PumpStream(o.Input, pty, stopInput.Token)
            : Task.Factory.StartNew(() => PumpFd(StdIn, pty, stopInput.Token), TaskCreationOptions.LongRunning);

        int code;
        try
        {
            code = await exitTask;
            await Task.Run(outputThread.Join);
        }
        finally
        {
            await stopInput.CancelAsync();
            foreach (var s in signals) s.Dispose();
            Restore();
            AppDomain.CurrentDomain.ProcessExit -= onExit;
            AppDomain.CurrentDomain.UnhandledException -= onCrash;
        }
        await Task.WhenAny(inputTask, Task.Delay(500));
        outputSink?.Flush();
        if (link is not null)
        {
            await link.CompleteAsync(code, TimeSpan.FromSeconds(1));
            await link.DisposeAsync();
        }
        return code;
    }

    /// <summary>
    /// PTY → output. Ends on EIO/EOF (all slave fds closed) or, once the child has exited, after
    /// 300 ms without output (a background grandchild may keep the slave open).
    /// </summary>
    private static void PumpOutput(PtyProcess pty, Task exited, Action<ReadOnlySpan<byte>> sink)
    {
        var buf = new byte[64 * 1024];
        var idleAfterExit = 0;
        while (true)
        {
            var p = new PollFd { Fd = pty.MasterFd, Events = Libc.POLLIN };
            var pr = Libc.poll(ref p, 1, 100);
            if (pr < 0) { if (Libc.Errno == Libc.EINTR) continue; break; }
            if (pr == 0)
            {
                if (exited.IsCompleted && (idleAfterExit += 100) >= 300) break;
                continue;
            }
            var n = Libc.read(pty.MasterFd, ref buf[0], buf.Length);
            if (n > 0) { idleAfterExit = 0; sink(buf.AsSpan(0, (int)n)); continue; }
            if (n < 0 && (Libc.Errno is var e && (e == Libc.EINTR || e == Libc.EAGAIN))) continue;
            break; // 0 or EIO: slave side fully closed
        }
    }

    private static void PumpFd(int fd, PtyProcess pty, CancellationToken stop)
    {
        var buf = new byte[16 * 1024];
        while (!stop.IsCancellationRequested)
        {
            var p = new PollFd { Fd = fd, Events = Libc.POLLIN };
            var pr = Libc.poll(ref p, 1, 200);
            if (pr < 0) { if (Libc.Errno == Libc.EINTR) continue; return; }
            if (pr == 0) continue;
            if ((p.Revents & Libc.POLLNVAL) != 0) return;
            var n = Libc.read(fd, ref buf[0], buf.Length);
            if (n > 0) { if (!pty.Write(buf.AsSpan(0, (int)n))) return; continue; }
            if (n < 0 && (Libc.Errno is var e && (e == Libc.EINTR || e == Libc.EAGAIN))) continue;
            return; // EOF on stdin: stop relaying, leave the PTY open (like script(1) does)
        }
    }

    private static async Task PumpStream(Stream input, PtyProcess pty, CancellationToken stop)
    {
        var buf = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await input.ReadAsync(buf, stop)) > 0)
                if (!pty.Write(buf.AsSpan(0, n))) return;
        }
        catch (OperationCanceledException) { }
    }

    internal static string Display(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length > 0 && a.All(c => char.IsLetterOrDigit(c) || "-_./=:,@%+".Contains(c))
            ? a : "'" + a.Replace("'", "'\\''") + "'"));

    private static void WriteErr(string s) => Libc.WriteAll(StdErr, Encoding.UTF8.GetBytes(s));
}
