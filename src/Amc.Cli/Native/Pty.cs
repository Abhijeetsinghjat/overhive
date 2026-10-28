using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

namespace Amc.Cli.Native;

public sealed class PtySpawnException(string message, int errno, int? commandExitCode = null) : Exception(message)
{
    public int Errno { get; } = errno;
    /// <summary>Shell-style exit code when the command itself could not be run: 127 not found, 126 not executable.</summary>
    public int? CommandExitCode { get; } = commandExitCode;
}

/// <summary>
/// A child process running on a fresh pseudo-terminal.
///
/// No fork() from managed code: the child is created with posix_spawnp, using
/// POSIX_SPAWN_SETSID (new session, no controlling tty) and a file action that opens the slave
/// by path as fd 0 — the first tty a session leader opens without O_NOCTTY becomes its
/// controlling terminal (and its process group the tty's foreground group) — then dup2 0→1, 0→2.
/// Signal dispositions are reset to default and the mask cleared, because the .NET runtime ignores
/// SIGPIPE and ignored dispositions survive exec.
/// </summary>
public sealed class PtyProcess : IPtyProcess
{
    private readonly object _writeLock = new();
    private int _master;

    public int MasterFd => _master;
    public int Pid { get; }
    public string SlavePath { get; }

    private PtyProcess(int master, int pid, string slavePath) { _master = master; Pid = pid; SlavePath = slavePath; }

    public static PtyProcess Spawn(IReadOnlyList<string> argv, string? cwd, IDictionary<string, string?> envOverrides, WinSize size)
    {
        if (argv.Count == 0) throw new ArgumentException("empty command", nameof(argv));
        if (!size.IsValid) size = new WinSize(80, 24);
        if (Libc.openpty(out var master, out var slave, IntPtr.Zero, IntPtr.Zero, ref size) != 0)
            throw new PtySpawnException("openpty failed", Libc.Errno);

        IntPtr attr = IntPtr.Zero, fa = IntPtr.Zero, sigmask = IntPtr.Zero, sigdef = IntPtr.Zero;
        var allocated = new List<IntPtr>();
        try
        {
            Libc.fcntl(master, Libc.F_SETFD, Libc.FD_CLOEXEC);
            Libc.fcntl(slave, Libc.F_SETFD, Libc.FD_CLOEXEC);
            var slavePath = Libc.SlaveName(master) ?? throw new PtySpawnException("cannot get the PTY slave name", Libc.Errno);

            attr = Zeroed(Libc.SpawnAttrSize);
            fa = Zeroed(Libc.FileActionsSize);
            sigmask = Zeroed(Libc.SigsetSize);
            sigdef = Zeroed(Libc.SigsetSize);
            Check(Libc.posix_spawnattr_init(attr), "posix_spawnattr_init");
            Check(Libc.posix_spawn_file_actions_init(fa), "posix_spawn_file_actions_init");
            Libc.sigemptyset(sigmask);
            Libc.sigfillset(sigdef);
            Check(Libc.posix_spawnattr_setsigmask(attr, sigmask), "setsigmask");
            Check(Libc.posix_spawnattr_setsigdefault(attr, sigdef), "setsigdefault");
            Check(Libc.posix_spawnattr_setflags(attr,
                (short)(Libc.POSIX_SPAWN_SETSID | Libc.POSIX_SPAWN_SETSIGMASK | Libc.POSIX_SPAWN_SETSIGDEF)), "setflags");
            // setsid happens before file actions (glibc and macOS), so this open acquires the controlling tty.
            Check(Libc.posix_spawn_file_actions_addopen(fa, 0, slavePath, Libc.O_RDWR, 0), "addopen");
            Check(Libc.posix_spawn_file_actions_adddup2(fa, 0, 1), "adddup2");
            Check(Libc.posix_spawn_file_actions_adddup2(fa, 0, 2), "adddup2");
            if (!string.IsNullOrEmpty(cwd)) Check(Libc.posix_spawn_file_actions_addchdir_np(fa, cwd), "addchdir");

            var argvPtrs = argv.Select(a => Track(Marshal.StringToCoTaskMemUTF8(a), allocated)).Append(IntPtr.Zero).ToArray();
            var env = BuildEnv(envOverrides);
            var envPtrs = env.Select(e => Track(Marshal.StringToCoTaskMemUTF8(e), allocated)).Append(IntPtr.Zero).ToArray();

            var rc = Libc.posix_spawnp(out var pid, argv[0], fa, attr, argvPtrs, envPtrs);
            if (rc != 0)
                throw new PtySpawnException($"cannot execute '{argv[0]}': {Marshal.GetPInvokeErrorMessage(rc)}", rc,
                    rc == Libc.ENOENT ? 127 : rc == Libc.EACCES ? 126 : null);
            // posix_spawn returns after the child exec'd (glibc: CLONE_VFORK; macOS: in-kernel), so the slave was already opened.
            Libc.close(slave);
            slave = -1;
            return new PtyProcess(master, pid, slavePath);
        }
        catch
        {
            Libc.close(master);
            throw;
        }
        finally
        {
            if (slave >= 0) Libc.close(slave);
            if (attr != IntPtr.Zero) { Libc.posix_spawnattr_destroy(attr); Marshal.FreeHGlobal(attr); }
            if (fa != IntPtr.Zero) { Libc.posix_spawn_file_actions_destroy(fa); Marshal.FreeHGlobal(fa); }
            if (sigmask != IntPtr.Zero) Marshal.FreeHGlobal(sigmask);
            if (sigdef != IntPtr.Zero) Marshal.FreeHGlobal(sigdef);
            foreach (var p in allocated) Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>Thread-safe write of input bytes into the PTY (user keystrokes and injected input).</summary>
    public bool Write(ReadOnlySpan<byte> data)
    {
        lock (_writeLock) return _master >= 0 && Libc.WriteAll(_master, data);
    }

    public bool Resize(WinSize size) => size.IsValid && _master >= 0 && Libc.SetWinSize(_master, size);

    /// <summary>A background grandchild may keep the slave open after the child exited; stop after 300 ms of quiet.</summary>
    public int IdleAfterExitMs => 300;

    /// <summary>poll + read on the master; EIO/EOF (all slave fds closed) is -1.</summary>
    public int Read(Span<byte> buffer, int timeoutMs)
    {
        var p = new PollFd { Fd = _master, Events = Libc.POLLIN };
        var pr = Libc.poll(ref p, 1, timeoutMs);
        if (pr < 0) return Libc.Errno == Libc.EINTR ? 0 : -1;
        if (pr == 0) return 0;
        var n = Libc.read(_master, ref MemoryMarshal.GetReference(buffer), buffer.Length);
        if (n > 0) return (int)n;
        if (n < 0 && (Libc.Errno is var e && (e == Libc.EINTR || e == Libc.EAGAIN))) return 0;
        return -1;
    }

    public void Hangup() => SignalGroup(Libc.SIGHUP);

    /// <summary>Blocks until the child exits. Returns the shell-style exit code (128+signal when killed).</summary>
    public int WaitForExit()
    {
        while (true)
        {
            var r = Libc.waitpid(Pid, out var status, 0);
            if (r == Pid) return DecodeStatus(status);
            if (r < 0 && Libc.Errno == Libc.EINTR) continue;
            return 255; // ECHILD: reaped elsewhere
        }
    }

    public static int DecodeStatus(int status)
    {
        var sig = status & 0x7f;
        return sig == 0 ? (status >> 8) & 0xff : 128 + sig;
    }

    /// <summary>Signals the child's process group (the child is a session + group leader).</summary>
    public bool SignalGroup(int sig) => Libc.kill(-Pid, sig) == 0 || Libc.kill(Pid, sig) == 0;

    public void Dispose()
    {
        var fd = Interlocked.Exchange(ref _master, -1);
        if (fd >= 0) lock (_writeLock) Libc.close(fd);
    }

    private static List<string> BuildEnv(IDictionary<string, string?> overrides)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        foreach (var (k, v) in overrides)
            if (v is null) env.Remove(k); else env[k] = v;
        return env.Select(kv => kv.Key + "=" + kv.Value).ToList();
    }

    private static IntPtr Zeroed(int size)
    {
        var p = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, p, size);
        return p;
    }

    private static IntPtr Track(IntPtr p, List<IntPtr> list) { list.Add(p); return p; }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new PtySpawnException(what + " failed", rc);
    }
}

/// <summary>Puts the local terminal in raw mode and guarantees restoration (idempotent, callable from any thread).</summary>
public sealed class RawModeGuard : IDisposable
{
    private Action? _restore;

    internal RawModeGuard(Action restore) => _restore = restore;

    public static RawModeGuard? Enter(int fd)
    {
        if (!Libc.IsTty(fd)) return null;
        var saved = new byte[Libc.TermiosSize];
        if (Libc.tcgetattr(fd, saved) != 0) return null;
        var raw = (byte[])saved.Clone();
        Libc.cfmakeraw(raw);
        if (Libc.tcsetattr(fd, Libc.TCSANOW, raw) != 0) return null;
        return new RawModeGuard(() => Libc.tcsetattr(fd, Libc.TCSANOW, saved));
    }

    public void Restore() => Interlocked.Exchange(ref _restore, null)?.Invoke();

    public void Dispose() => Restore();
}
