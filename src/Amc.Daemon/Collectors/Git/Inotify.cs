using System.Runtime.InteropServices;
using System.Text;

namespace Amc.Daemon.Collectors.Git;

/// <summary>
/// One inotify instance for all Git metadata directories. FileSystemWatcher is not used:
/// on Linux each watcher consumes one inotify *instance* (default limit 128 per user,
/// shared with editors and other tools). Watches are non-recursive directory watches.
/// </summary>
public sealed class Inotify : IDirWatcher
{
    public const uint Modify = 0x2, CloseWrite = 0x8, MovedFrom = 0x40, MovedTo = 0x80, Create = 0x100, Delete = 0x200,
        DeleteSelf = 0x400, MoveSelf = 0x800, Overflow = 0x4000, Ignored = 0x8000, OnlyDir = 0x1000000, IsDir = 0x40000000;
    public const uint DefaultMask = CloseWrite | MovedFrom | MovedTo | Create | Delete | DeleteSelf | MoveSelf | OnlyDir;

    /// <summary>(directory, entry name or null for the directory itself, mask). Overflow is reported with directory "".</summary>
    public event Action<string, string?, uint>? Changed;

    private readonly int _fd;
    private readonly Dictionary<int, string> _byWd = [];
    private readonly Dictionary<string, int> _byPath = new(StringComparer.Ordinal);
    private readonly Thread _thread;
    private volatile bool _disposed;

    private Inotify(int fd)
    {
        _fd = fd;
        _thread = new Thread(Loop) { IsBackground = true, Name = "amc-git-inotify" };
        _thread.Start();
    }

    /// <summary>Null when inotify is unavailable (non-Linux, or instance limit reached).</summary>
    public static Inotify? TryCreate(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsLinux()) { error = "inotify requires Linux"; return null; }
        var fd = inotify_init1(O_NONBLOCK | O_CLOEXEC);
        if (fd < 0) { error = $"inotify_init1 failed (errno {Marshal.GetLastPInvokeError()})"; return null; }
        return new Inotify(fd);
    }

    public int Count { get { lock (_byWd) return _byWd.Count; } }

    public bool Add(string dir) => Add(dir, DefaultMask);

    public bool Add(string dir, uint mask)
    {
        lock (_byWd)
        {
            if (_disposed) return false;
            if (_byPath.ContainsKey(dir)) return true;
            var wd = inotify_add_watch(_fd, dir, mask);
            if (wd < 0) return false;
            if (_byWd.TryGetValue(wd, out var other)) _byPath.Remove(other); // same inode under another name
            _byWd[wd] = dir;
            _byPath[dir] = wd;
            return true;
        }
    }

    public void Remove(string dir)
    {
        lock (_byWd)
        {
            if (!_byPath.Remove(dir, out var wd)) return;
            _byWd.Remove(wd);
            if (!_disposed) inotify_rm_watch(_fd, wd);
        }
    }

    public bool IsWatched(string dir) { lock (_byWd) return _byPath.ContainsKey(dir); }

    private void Loop()
    {
        var buf = new byte[64 * 1024];
        var pfd = new PollFd { Fd = _fd, Events = POLLIN };
        while (!_disposed)
        {
            pfd.Revents = 0;
            if (poll(ref pfd, 1, 500) <= 0) continue;
            var n = (long)read(_fd, buf, buf.Length);
            if (n <= 0) continue;
            var off = 0;
            while (off + 16 <= n)
            {
                var wd = BitConverter.ToInt32(buf, off);
                var mask = BitConverter.ToUInt32(buf, off + 4);
                var len = (int)BitConverter.ToUInt32(buf, off + 12);
                string? name = null;
                if (len > 0)
                {
                    var span = buf.AsSpan(off + 16, len);
                    var z = span.IndexOf((byte)0);
                    name = Encoding.UTF8.GetString(z < 0 ? span : span[..z]);
                }
                off += 16 + len;
                string? dir;
                lock (_byWd)
                {
                    _byWd.TryGetValue(wd, out dir);
                    if ((mask & Ignored) != 0 && dir is not null) { _byWd.Remove(wd); _byPath.Remove(dir); }
                }
                if ((mask & Overflow) != 0) dir = "";
                if (dir is null) continue;
                try { Changed?.Invoke(dir, name, mask); } catch { /* handlers must not kill the reader */ }
            }
        }
    }

    public void Dispose()
    {
        lock (_byWd)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _thread.Join(TimeSpan.FromSeconds(2));
        close(_fd);
    }

    private const int O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
    private const short POLLIN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int Fd; public short Events; public short Revents; }

    [DllImport("libc", SetLastError = true)] private static extern int inotify_init1(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int inotify_add_watch(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mask);
    [DllImport("libc", SetLastError = true)] private static extern int inotify_rm_watch(int fd, int wd);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buf, nint count);
    [DllImport("libc", SetLastError = true)] private static extern int poll(ref PollFd fds, nuint nfds, int timeout);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
}
