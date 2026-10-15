using System.Runtime.InteropServices;

namespace Amc.Cli.Native;

[StructLayout(LayoutKind.Sequential)]
public struct WinSize
{
    public ushort Rows, Cols, XPixel, YPixel;
    public WinSize(int cols, int rows) { Cols = (ushort)cols; Rows = (ushort)rows; XPixel = YPixel = 0; }
    public readonly bool IsValid => Cols > 0 && Rows > 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    public int Fd;
    public short Events;
    public short Revents;
}

/// <summary>
/// Minimal libc bindings for Linux (glibc, x86_64 / arm64) and macOS. Constants that differ are chosen at
/// startup. Opaque C structs (termios, posix_spawnattr_t,
/// posix_spawn_file_actions_t, sigset_t) are allocated as generously sized byte buffers so we never
/// depend on their exact layout.
/// </summary>
internal static class Libc
{
    private const string L = "libc";

    private static readonly bool Mac = OperatingSystem.IsMacOS();

    public const int EINTR = 4, EIO = 5, ENOENT = 2, EACCES = 13;
    public static readonly int EAGAIN = Mac ? 35 : 11;
    public const short POLLIN = 0x1, POLLERR = 0x8, POLLHUP = 0x10, POLLNVAL = 0x20;
    public const int O_RDWR = 2;
    public static readonly int O_NOCTTY = Mac ? 0x20000 : 0x100;
    public const int F_SETFD = 2, FD_CLOEXEC = 1;
    public const int TCSANOW = 0;
    public static readonly nuint TIOCGWINSZ = Mac ? (nuint)0x40087468 : 0x5413, TIOCSWINSZ = Mac ? (nuint)0x80087467 : 0x5414;
    public const short POSIX_SPAWN_SETSIGDEF = 0x04, POSIX_SPAWN_SETSIGMASK = 0x08;
    public static readonly short POSIX_SPAWN_SETSID = Mac ? (short)0x400 : (short)0x80;
    public const int SIGHUP = 1, SIGINT = 2, SIGQUIT = 3, SIGKILL = 9, SIGTERM = 15;

    public const int TermiosSize = 256;          // real: 60
    public const int SpawnAttrSize = 1024;       // real: 336
    public const int FileActionsSize = 1024;     // real: 80
    public const int SigsetSize = 256;           // real: 128

    [DllImport(L, SetLastError = true)] public static extern int openpty(out int master, out int slave, IntPtr name, IntPtr termp, ref WinSize winp);
    [DllImport(L, SetLastError = true)] public static extern int ptsname_r(int fd, byte[] buf, nuint len);
    [DllImport(L, SetLastError = true)] private static extern IntPtr ptsname(int fd);

    /// <summary>
    /// Path of the PTY slave for a master fd: ptsname_r on Linux; ptsname on macOS, where ptsname_r is missing
    /// on older releases (ptsname's static buffer is safe here: only PTY spawns call it, one at a time).
    /// </summary>
    public static string? SlaveName(int master)
    {
        if (Mac)
        {
            lock (PtsnameGate) { var p = ptsname(master); return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p); }
        }
        var buf = new byte[256];
        if (ptsname_r(master, buf, (nuint)buf.Length) != 0) return null;
        var n = Array.IndexOf(buf, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
    }

    private static readonly object PtsnameGate = new();
    [DllImport(L, SetLastError = true)] public static extern int close(int fd);
    [DllImport(L, EntryPoint = "fcntl", SetLastError = true)] private static extern int fcntl_std(int fd, int cmd, int arg);
    [DllImport(L, SetLastError = true)] public static extern nint read(int fd, ref byte buf, nint count);
    [DllImport(L, SetLastError = true)] public static extern nint write(int fd, ref byte buf, nint count);
    [DllImport(L, SetLastError = true)] public static extern int poll(ref PollFd fds, nuint nfds, int timeout);
    [DllImport(L, SetLastError = true)] public static extern int isatty(int fd);
    [DllImport(L, EntryPoint = "ioctl", SetLastError = true)] private static extern int ioctl_std(int fd, nuint request, ref WinSize ws);

    // ioctl and fcntl are variadic. On Apple arm64, variadic arguments are passed on the stack, not in registers,
    // so a plain P/Invoke hands the callee garbage (and it may write through it). Filling x2..x7 with dummies
    // makes the real argument the first stack slot, which is where a variadic callee reads it.
    private static readonly bool AppleArm64 = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    [DllImport(L, EntryPoint = "ioctl", SetLastError = true)] private static extern int ioctl_va(int fd, nuint request, nint d2, nint d3, nint d4, nint d5, nint d6, nint d7, ref WinSize ws);
    [DllImport(L, EntryPoint = "fcntl", SetLastError = true)] private static extern int fcntl_va(int fd, int cmd, nint d2, nint d3, nint d4, nint d5, nint d6, nint d7, nint arg);

    public static int ioctl(int fd, nuint request, ref WinSize ws) =>
        AppleArm64 ? ioctl_va(fd, request, 0, 0, 0, 0, 0, 0, ref ws) : ioctl_std(fd, request, ref ws);

    public static int fcntl(int fd, int cmd, int arg) =>
        AppleArm64 ? fcntl_va(fd, cmd, 0, 0, 0, 0, 0, 0, arg) : fcntl_std(fd, cmd, arg);
    [DllImport(L, SetLastError = true)] public static extern int tcgetattr(int fd, byte[] termios);
    [DllImport(L, SetLastError = true)] public static extern int tcsetattr(int fd, int optionalActions, byte[] termios);
    [DllImport(L)] public static extern void cfmakeraw(byte[] termios);
    [DllImport(L, SetLastError = true)] public static extern int kill(int pid, int sig);
    [DllImport(L, SetLastError = true)] public static extern int waitpid(int pid, out int status, int options);

    [DllImport(L)] public static extern int posix_spawnattr_init(IntPtr attr);
    [DllImport(L)] public static extern int posix_spawnattr_destroy(IntPtr attr);
    [DllImport(L)] public static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
    [DllImport(L)] public static extern int posix_spawnattr_setsigmask(IntPtr attr, IntPtr sigmask);
    [DllImport(L)] public static extern int posix_spawnattr_setsigdefault(IntPtr attr, IntPtr sigdefault);
    [DllImport(L)] public static extern int sigemptyset(IntPtr set);
    [DllImport(L)] public static extern int sigfillset(IntPtr set);
    [DllImport(L)] public static extern int posix_spawn_file_actions_init(IntPtr fa);
    [DllImport(L)] public static extern int posix_spawn_file_actions_destroy(IntPtr fa);
    [DllImport(L)] public static extern int posix_spawn_file_actions_addopen(IntPtr fa, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int oflag, uint mode);
    [DllImport(L)] public static extern int posix_spawn_file_actions_adddup2(IntPtr fa, int fd, int newfd);
    [DllImport(L)] public static extern int posix_spawn_file_actions_addchdir_np(IntPtr fa, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(L)] public static extern int posix_spawnp(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, IntPtr fileActions, IntPtr attr, IntPtr[] argv, IntPtr[] envp);

    public static int Errno => Marshal.GetLastPInvokeError();

    public static bool IsTty(int fd)
    {
        try { return isatty(fd) == 1; }
        catch (DllNotFoundException) { return false; }
    }

    public static WinSize? GetWinSize(int fd)
    {
        var ws = default(WinSize);
        return ioctl(fd, TIOCGWINSZ, ref ws) == 0 && ws.IsValid ? ws : null;
    }

    public static bool SetWinSize(int fd, WinSize ws) => ioctl(fd, TIOCSWINSZ, ref ws) == 0;

    /// <summary>Writes the whole span, retrying on EINTR/EAGAIN (non-blocking fds) and partial writes.</summary>
    public static bool WriteAll(int fd, ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            var n = write(fd, ref MemoryMarshal.GetReference(data), data.Length);
            if (n > 0) { data = data[(int)n..]; continue; }
            var err = Errno;
            if (err == EINTR) continue;
            if (err == EAGAIN)
            {
                var p = new PollFd { Fd = fd, Events = 0x4 /* POLLOUT */ };
                poll(ref p, 1, 100);
                continue;
            }
            return false;
        }
        return true;
    }
}
