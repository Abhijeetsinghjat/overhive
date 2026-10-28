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
    /// <summary>Force the `script(1)` fallback instead of the native PTY (also: AMC_PTY_MODE=script). Ignored on Windows.</summary>
    public bool UseScriptFallback { get; set; }
}

/// <summary>
/// `agentctl run -- &lt;cmd&gt; [args]`: runs the command on a PTY owned by us (ConPTY on Windows), relays it to the
/// user's terminal (raw mode, always restored) and mirrors it to the daemon so the UI can watch and type.
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
        var windows = OperatingSystem.IsWindows();
        var interactive = o.Output is null && o.Input is null && Term.IsTerminal(StdIn);
        var size = (interactive ? Term.GetSize(StdIn) ?? Term.GetSize(StdOut) : null) ?? new WinSize(o.Cols, o.Rows);
        var env = new Dictionary<string, string?> { ["AMC_TERMINAL_ID"] = terminalId };
        // Windows programs do not consult TERM (and some, like Git for Windows, change behaviour when it is set).
        if (!windows && Environment.GetEnvironmentVariable("TERM") is null or "") env["TERM"] = "xterm-256color";

        if (!windows && (o.UseScriptFallback || Environment.GetEnvironmentVariable("AMC_PTY_MODE") == "script"))
            return await ScriptFallback.RunAsync(commandAndArgs, o, terminalId, cwd, env, size, ct);

        IPtyProcess pty;
        try { pty = Pty.Spawn(commandAndArgs, cwd, env, size); }
        catch (PtySpawnException e) when (e.CommandExitCode is { } notRunnable)
        {
            WriteErr($"agentctl: {e.Message}\n");
            return notRunnable;
        }
        catch (Exception e) when (windows && e is PtySpawnException or EntryPointNotFoundException)
        {
            WriteErr($"agentctl: cannot start a Windows pseudo console ({e.Message}); Windows 10 1809 or later is required\n");
            return 1;
        }
        catch (Exception e) when (e is PtySpawnException or DllNotFoundException or EntryPointNotFoundException)
        {
            WriteErr($"agentctl: native PTY unavailable ({e.Message}); falling back to script(1)\n");
            return await ScriptFallback.RunAsync(commandAndArgs, o, terminalId, cwd, env, size, ct);
        }

        using var _ = pty;
        RawModeGuard? raw = !windows ? (interactive ? RawModeGuard.Enter(StdIn) : null)
            : o.Output is null ? Term.EnterWindowsConsole(rawInput: interactive) : null;
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
        Timer? resizePoll = null;
        if (interactive && windows)
        {
            // No SIGWINCH on Windows: follow the console window size by polling.
            resizePoll = new Timer(_ =>
            {
                if (Term.GetSize(StdOut) is { } ws && (ws.Cols != sizeBox.Value.Cols || ws.Rows != sizeBox.Value.Rows) && pty.Resize(ws))
                {
                    sizeBox.Value = ws;
                    link?.SendResize(ws.Cols, ws.Rows);
                }
            }, null, 250, 250);
        }
        if (windows && o.Output is null)
        {
            // In raw mode ^C reaches the child as a byte (ConPTY turns it into CTRL_C_EVENT). Console control events only
            // arrive when input is not raw (stdin redirected) or from outside: relay ^C/^Break, hang up on close/shutdown.
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; pty.Write([0x03]); }));
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGQUIT, ctx => { ctx.Cancel = true; pty.Write([0x03]); }));
            foreach (var ps in new[] { PosixSignal.SIGHUP, PosixSignal.SIGTERM })
                signals.Add(PosixSignalRegistration.Create(ps, _ => pty.Hangup()));
        }
        if (interactive && !windows)
        {
            var unix = (PtyProcess)pty;
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ =>
            {
                if (Term.GetSize(StdIn) is { } ws && pty.Resize(ws))
                {
                    sizeBox.Value = ws;
                    link?.SendResize(ws.Cols, ws.Rows);
                }
            }));
            // In raw mode ^C/^\ reach the child as bytes; these only arrive from kill(1) or a hangup — forward them.
            foreach (var (ps, num) in new[] { (PosixSignal.SIGINT, Libc.SIGINT), (PosixSignal.SIGTERM, Libc.SIGTERM),
                         (PosixSignal.SIGHUP, Libc.SIGHUP), (PosixSignal.SIGQUIT, Libc.SIGQUIT) })
                signals.Add(PosixSignalRegistration.Create(ps, ctx => { ctx.Cancel = true; unix.SignalGroup(num); }));
        }

        using var stopInput = new CancellationTokenSource();
        var exitTask = Task.Factory.StartNew(pty.WaitForExit, TaskCreationOptions.LongRunning);
        using var ctReg = ct.Register(pty.Hangup);

        var outputSink = o.Output;
        var outputThread = new Thread(() => PumpOutput(pty, exitTask, data =>
        {
            if (outputSink is not null) outputSink.Write(data);
            else Term.WriteAll(StdOut, data);
            link?.EnqueueOutput(data);
        })) { IsBackground = true, Name = "pty-output" };
        outputThread.Start();

        Task inputTask = o.Input is not null
            ? PumpStream(o.Input, pty, stopInput.Token)
            : windows ? Task.Factory.StartNew(() => PumpWindowsStdIn(pty, stopInput.Token), TaskCreationOptions.LongRunning)
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
            if (resizePoll is not null) await resizePoll.DisposeAsync();
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
    /// PTY → output. Ends when the terminal closes (Unix: all slave fds closed; Windows: console closed after the
    /// child's exit) or, once the child has exited, after <see cref="IPtyProcess.IdleAfterExitMs"/> without output.
    /// </summary>
    private static void PumpOutput(IPtyProcess pty, Task exited, Action<ReadOnlySpan<byte>> sink)
    {
        var buf = new byte[64 * 1024];
        var idleAfterExit = 0;
        while (true)
        {
            var n = pty.Read(buf, 100);
            if (n > 0) { idleAfterExit = 0; sink(buf.AsSpan(0, n)); continue; }
            if (n < 0) break;
            if (exited.IsCompleted && (idleAfterExit += 100) >= pty.IdleAfterExitMs) break;
        }
    }

    /// <summary>Console (or redirected) stdin → PTY on Windows. A blocked console read is abandoned at exit.</summary>
    private static void PumpWindowsStdIn(IPtyProcess pty, CancellationToken stop)
    {
        var buf = new byte[16 * 1024];
        while (!stop.IsCancellationRequested)
        {
            var n = Term.ReadStdIn(buf);
            if (n <= 0 || stop.IsCancellationRequested) return;
            if (!pty.Write(buf.AsSpan(0, n))) return;
        }
    }

    private static void PumpFd(int fd, IPtyProcess pty, CancellationToken stop)
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

    private static async Task PumpStream(Stream input, IPtyProcess pty, CancellationToken stop)
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

    private static void WriteErr(string s) => Term.WriteAll(StdErr, Encoding.UTF8.GetBytes(s));
}
