using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Amc.Cli.Native;

/// <summary>
/// A child process on a Windows pseudo console (ConPTY, Windows 10 1809+).
///
/// Two anonymous pipes connect us to the pseudo console (conhost); the child is created suspended with
/// PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, put in a kill-on-close job object (so its whole tree dies with us, however we
/// exit) and resumed. Std handles are explicitly null so a redirected parent's handles are never inherited and the
/// console's own are used. ConPTY repaints rather than passing bytes through, so output carries VT sequences of its own.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtyProcess : IPtyProcess
{
    private const int CloseAfterQuietMs = 200;

    private readonly object _writeLock = new();
    private readonly SafeFileHandle _input, _output, _process, _thread, _job;
    private nint _hpc;
    private int _quietAfterExit;
    private volatile bool _closing;

    public int Pid { get; }

    /// <summary>Backstop only: output normally ends at EOF once the console is closed after the child's exit.</summary>
    public int IdleAfterExitMs => 5000;

    private ConPtyProcess(nint hpc, SafeFileHandle input, SafeFileHandle output, SafeFileHandle process, SafeFileHandle thread, SafeFileHandle job, int pid)
    {
        _hpc = hpc; _input = input; _output = output; _process = process; _thread = thread; _job = job; Pid = pid;
    }

    public static unsafe ConPtyProcess Spawn(IReadOnlyList<string> argv, string? cwd, IDictionary<string, string?> envOverrides, WinSize size)
    {
        if (argv.Count == 0) throw new ArgumentException("empty command", nameof(argv));
        if (!size.IsValid) size = new WinSize(80, 24);
        cwd = string.IsNullOrEmpty(cwd) ? Environment.CurrentDirectory : cwd;
        if (!Directory.Exists(cwd)) throw new PtySpawnException($"cannot change to '{cwd}': no such directory", Kernel32.ERROR_DIRECTORY, 127);
        var (app, commandLine) = WinCommandLine.Build(argv, cwd, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"));
        var envBlock = WinCommandLine.EnvironmentBlock(envOverrides);

        SafeFileHandle? inRead = null, inWrite = null, outRead = null, outWrite = null, job = null;
        nint hpc = 0, attrs = 0;
        var ok = false;
        try
        {
            if (!Kernel32.CreatePipe(out inRead, out inWrite, 0, 0) || !Kernel32.CreatePipe(out outRead, out outWrite, 0, 0))
                throw Win32("CreatePipe");
            var hr = Kernel32.CreatePseudoConsole(new Kernel32.Coord { X = (short)size.Cols, Y = (short)size.Rows }, inRead, outWrite, 0, out hpc);
            if (hr != 0) throw new PtySpawnException($"CreatePseudoConsole failed (0x{hr:x8})", hr);
            // The pseudo console holds its own copies; ours must go or the output pipe never reports EOF.
            inRead.Dispose();
            outWrite.Dispose();

            job = Kernel32.CreateJobObjectW(0, null);
            if (job.IsInvalid) throw Win32("CreateJobObject");
            var limits = new Kernel32.JobObjectExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = Kernel32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!Kernel32.SetInformationJobObject(job, Kernel32.JobObjectExtendedLimitInformationClass, &limits, sizeof(Kernel32.JobObjectExtendedLimitInformation)))
                throw Win32("SetInformationJobObject");

            nint attrSize = 0;
            Kernel32.InitializeProcThreadAttributeList(0, 1, 0, ref attrSize);
            attrs = Marshal.AllocHGlobal(attrSize);
            if (!Kernel32.InitializeProcThreadAttributeList(attrs, 1, 0, ref attrSize)) { Marshal.FreeHGlobal(attrs); attrs = 0; throw Win32("InitializeProcThreadAttributeList"); }
            if (!Kernel32.UpdateProcThreadAttribute(attrs, 0, Kernel32.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, sizeof(nint), 0, 0))
                throw Win32("UpdateProcThreadAttribute");

            var si = new Kernel32.StartupInfoExW { lpAttributeList = attrs };
            si.StartupInfo.cb = sizeof(Kernel32.StartupInfoExW);
            si.StartupInfo.dwFlags = Kernel32.STARTF_USESTDHANDLES;
            var pi = new Kernel32.ProcessInformation();
            var cmd = (commandLine + '\0').ToCharArray(); // CreateProcessW may write to it
            bool created;
            fixed (char* pCmd = cmd)
            fixed (char* pEnv = envBlock)
                created = Kernel32.CreateProcessW(app, pCmd, 0, 0, false,
                    Kernel32.EXTENDED_STARTUPINFO_PRESENT | Kernel32.CREATE_UNICODE_ENVIRONMENT | Kernel32.CREATE_SUSPENDED,
                    pEnv, cwd, &si, &pi);
            if (!created)
            {
                var err = Marshal.GetLastPInvokeError();
                throw new PtySpawnException($"cannot execute '{argv[0]}': {Marshal.GetPInvokeErrorMessage(err)}", err, err switch
                {
                    Kernel32.ERROR_FILE_NOT_FOUND or Kernel32.ERROR_PATH_NOT_FOUND or Kernel32.ERROR_DIRECTORY => 127,
                    Kernel32.ERROR_ACCESS_DENIED or Kernel32.ERROR_BAD_EXE_FORMAT => 126,
                    _ => null,
                });
            }
            var process = new SafeFileHandle(pi.hProcess, true);
            var thread = new SafeFileHandle(pi.hThread, true);
            // Assigned before the first instruction runs, so every descendant is in the job too. Failure (exotic
            // nested-job setups) leaves the child running without the kill-on-close guarantee rather than refusing to run.
            Kernel32.AssignProcessToJobObject(job, process);
            Kernel32.ResumeThread(thread);
            ok = true;
            return new ConPtyProcess(hpc, inWrite, outRead, process, thread, job, pi.dwProcessId);
        }
        finally
        {
            if (attrs != 0) { Kernel32.DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }
            if (!ok)
            {
                inWrite?.Dispose();
                outRead?.Dispose();
                if (hpc != 0) Kernel32.ClosePseudoConsole(hpc);
                inRead?.Dispose();
                outWrite?.Dispose();
                job?.Dispose();
            }
        }
    }

    /// <summary>
    /// Polls the output pipe. Once the child has exited and output stayed quiet for a moment, the console is closed
    /// (flushing its final frame), which ends the pipe: then -1.
    /// </summary>
    public unsafe int Read(Span<byte> buffer, int timeoutMs)
    {
        var waited = 0;
        while (true)
        {
            uint avail;
            try { if (!Kernel32.PeekNamedPipe(_output, 0, 0, 0, out avail, 0)) return -1; }
            catch (ObjectDisposedException) { return -1; }
            if (avail > 0)
            {
                int n;
                fixed (byte* p = buffer)
                    if (!Kernel32.ReadFile(_output.DangerousGetHandle(), p, (int)Math.Min(avail, (uint)buffer.Length), out n, 0) || n <= 0) return -1;
                _quietAfterExit = 0;
                return n;
            }
            if (waited >= timeoutMs) break;
            Thread.Sleep(10);
            waited += 10;
        }
        if (!_closing && HasExited() && (_quietAfterExit += timeoutMs) >= CloseAfterQuietMs) BeginClose();
        return 0;
    }

    public bool Write(ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            if (_input.IsClosed) return false;
            return Kernel32.WriteAll(_input.DangerousGetHandle(), data);
        }
    }

    public bool Resize(WinSize size)
    {
        var h = _hpc;
        return size.IsValid && h != 0 && Kernel32.ResizePseudoConsole(h, new Kernel32.Coord { X = (short)size.Cols, Y = (short)size.Rows }) == 0;
    }

    /// <summary>Blocks until the child exits. Its 32-bit exit code is returned as is (e.g. 0xC000013A after ^C).</summary>
    public int WaitForExit()
    {
        Kernel32.WaitForSingleObject(_process, Kernel32.INFINITE);
        return Kernel32.GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : 255;
    }

    /// <summary>
    /// Closes the console (clients get CTRL_CLOSE_EVENT, the Windows hangup), then ends whatever is left of the tree.
    /// </summary>
    public void Hangup()
    {
        BeginClose();
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            try { if (!HasExited()) Kernel32.TerminateJobObject(_job, 1); }
            catch (ObjectDisposedException) { }
        }, TaskScheduler.Default);
    }

    private bool HasExited()
    {
        try { return Kernel32.WaitForSingleObject(_process, 0) == Kernel32.WAIT_OBJECT_0; }
        catch (ObjectDisposedException) { return true; }
    }

    // ClosePseudoConsole can block until the final frame is read (before Windows 11 24H2), so never on the reader's thread.
    private void BeginClose()
    {
        _closing = true;
        _ = Task.Run(ClosePseudoConsole);
    }

    private void ClosePseudoConsole()
    {
        var h = Interlocked.Exchange(ref _hpc, 0);
        if (h != 0) Kernel32.ClosePseudoConsole(h);
    }

    public void Dispose()
    {
        lock (_writeLock) _input.Dispose();
        _output.Dispose(); // first, so a blocking ClosePseudoConsole cannot wait on an unread pipe
        ClosePseudoConsole();
        _thread.Dispose();
        _process.Dispose();
        _job.Dispose(); // kill-on-close: anything still running in the tree ends here
    }

    private static PtySpawnException Win32(string what)
    {
        var err = Marshal.GetLastPInvokeError();
        return new PtySpawnException($"{what} failed: {Marshal.GetPInvokeErrorMessage(err)}", err);
    }
}

/// <summary>Windows program lookup, command-line quoting and environment blocks for CreateProcessW.</summary>
public static class WinCommandLine
{
    private static readonly string[] Runnable = [".exe", ".com", ".bat", ".cmd"];

    /// <summary>
    /// Resolves argv[0] (PATH + PATHEXT like cmd.exe, but never the current directory; a name with a separator is
    /// taken relative to <paramref name="cwd"/>) and builds the command line. Batch files run through cmd.exe.
    /// Returns the image to start and its command line. Throws <see cref="PtySpawnException"/> (127) when not found.
    /// </summary>
    public static (string Application, string CommandLine) Build(IReadOnlyList<string> argv, string cwd, string? path, string? pathExt)
    {
        var program = Resolve(argv[0], cwd, path, pathExt)
            ?? throw new PtySpawnException($"cannot execute '{argv[0]}': command not found", 2, 127);
        var ext = Path.GetExtension(program);
        if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            // /s: cmd strips exactly the outer quotes and runs the rest verbatim.
            var inner = "\"" + program + "\"" + string.Concat(argv.Skip(1).Select(a => " " + QuoteForBatch(a)));
            return (cmdExe, "cmd.exe /d /e:on /v:off /s /c \"" + inner + "\"");
        }
        return (program, string.Join(' ', argv.Select((a, i) => i == 0 ? QuoteProgram(a) : Quote(a))));
    }

    /// <summary>Full path of the program, or null. Only .exe/.com/.bat/.cmd can be started.</summary>
    public static string? Resolve(string name, string cwd, string? path, string? pathExt)
    {
        if (name.Length == 0 || name.IndexOfAny(['"', '\0', '*', '?', '<', '>', '|']) >= 0) return null;
        var exts = (string.IsNullOrEmpty(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => Runnable.Contains(e, StringComparer.OrdinalIgnoreCase)).ToArray();
        IEnumerable<string> dirs = name.IndexOfAny(['\\', '/', ':']) >= 0
            ? [""]
            : (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => d.Trim('"'));
        foreach (var dir in dirs)
        {
            string full;
            try { full = Path.GetFullPath(dir.Length == 0 ? Path.Combine(cwd, name) : Path.Combine(dir, name), cwd); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (Runnable.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) && File.Exists(full)) return full;
            foreach (var e in exts)
                if (File.Exists(full + e.ToLowerInvariant())) return full + e.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Quotes one argument so CommandLineToArgvW / the MSVC runtime give it back unchanged: backslashes are literal
    /// unless they precede a quote, where they are doubled and the quote escaped.
    /// </summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        for (var i = 0; ; i++)
        {
            var slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', slashes * 2); break; }
            if (arg[i] == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(arg[i]);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>argv[0] is split on whitespace and quotes only (no backslash escapes), so it is just wrapped.</summary>
    private static string QuoteProgram(string name) => name.IndexOfAny([' ', '\t']) >= 0 ? "\"" + name + "\"" : name;

    /// <summary>
    /// cmd.exe re-parses a batch file's arguments with its own rules, which cannot express every string safely
    /// (CVE-2024-24576 class). Plain arguments are quoted when needed; ones cmd would expand or split are refused.
    /// </summary>
    public static string QuoteForBatch(string arg)
    {
        if (arg.IndexOfAny(['"', '%', '!', '\r', '\n', '\0']) >= 0)
            throw new PtySpawnException($"argument '{arg}' cannot be passed safely to a batch file", 87);
        return arg.Length == 0 || arg.IndexOfAny([' ', '\t', '&', '|', '<', '>', '^', '(', ')', ',', ';', '=']) >= 0 || arg.EndsWith('\\')
            ? "\"" + arg + (arg.EndsWith('\\') ? "\\" : "") + "\""
            : arg;
    }

    /// <summary>
    /// Our environment with <paramref name="overrides"/> applied (null removes), as a CreateProcessW Unicode block:
    /// NAME=value\0 … \0, names compared case-insensitively and sorted the way Windows expects.
    /// </summary>
    public static string EnvironmentBlock(IDictionary<string, string?> overrides)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v && k.Length > 0) env[k] = v;
        foreach (var (k, v) in overrides)
            if (v is null) env.Remove(k); else env[k] = v;
        var sb = new StringBuilder();
        foreach (var (k, v) in env) sb.Append(k).Append('=').Append(v).Append('\0');
        if (env.Count == 0) sb.Append('\0');
        return sb.Append('\0').ToString();
    }
}
