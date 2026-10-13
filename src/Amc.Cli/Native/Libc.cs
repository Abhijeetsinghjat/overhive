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
/// Minimal glibc bindings (Linux x86_64 / arm64). Opaque C structs (termios, posix_spawnattr_t,
/// posix_spawn_file_actions_t, sigset_t) are allocated as generously sized byte buffers so we never
/// depend on their exact layout.
/// </summary>
internal static class Libc
{
    private const string L = "libc";

    public const int EINTR = 4, EIO = 5, EAGAIN = 11, ENOENT = 2, EACCES = 13;
    public const short POLLIN = 0x1, POLLERR = 0x8, POLLHUP = 0x10, POLLNVAL = 0x20;
    public const int O_RDWR = 2, O_NOCTTY = 0x100;
    public const int F_SETFD = 2, FD_CLOEXEC = 1;
    public const int TCSANOW = 0;
    public const nuint TIOCGWINSZ = 0x5413, TIOCSWINSZ = 0x5414;
    public const short POSIX_SPAWN_SETSIGDEF = 0x04, POSIX_SPAWN_SETSIGMASK = 0x08, POSIX_SPAWN_SETSID = 0x80;
    public const int SIGHUP = 1, SIGINT = 2, SIGQUIT = 3, SIGKILL = 9, SIGTERM = 15;

    public const int TermiosSize = 256;          // real: 60
    public const int SpawnAttrSize = 1024;       // real: 336
    public const int FileActionsSize = 1024;     // real: 80
    public const int SigsetSize = 256;           // real: 128

    [DllImport(L, SetLastError = true)] public static extern int openpty(out int master, out int slave, IntPtr name, IntPtr termp, ref WinSize winp);
    [DllImport(L, SetLastError = true)] public static extern int ptsname_r(int fd, byte[] buf, nuint len);
    [DllImport(L, SetLastError = true)] public static extern int close(int fd);
    [DllImport(L, SetLastError = true)] public static extern int fcntl(int fd, int cmd, int arg);
    [DllImport(L, SetLastError = true)] public static extern nint read(int fd, ref byte buf, nint count);
    [DllImport(L, SetLastError = true)] public static extern nint write(int fd, ref byte buf, nint count);
    [DllImport(L, SetLastError = true)] public static extern int poll(ref PollFd fds, nuint nfds, int timeout);
    [DllImport(L, SetLastError = true)] public static extern int isatty(int fd);
    [DllImport(L, SetLastError = true)] public static extern int ioctl(int fd, nuint request, ref WinSize ws);
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
