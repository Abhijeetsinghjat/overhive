using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Amc.Daemon.Platform;

/// <summary>macOS file helpers.</summary>
[SupportedOSPlatform("macos")]
public static class MacFile
{
    private const string Lib = "/usr/lib/libSystem.dylib";
    private const int F_GETPATH = 50, MaxPath = 1024;

    // fcntl is variadic: on Apple arm64 the variadic argument goes on the stack, after x0..x7 (see Amc.Cli Libc).
    [DllImport(Lib, EntryPoint = "fcntl", SetLastError = true)] private static extern int fcntl_std(int fd, int cmd, byte[] buf);
    [DllImport(Lib, EntryPoint = "fcntl", SetLastError = true)] private static extern int fcntl_va(int fd, int cmd, nint d2, nint d3, nint d4, nint d5, nint d6, nint d7, byte[] buf);

    /// <summary>The path an open file descriptor refers to now (F_GETPATH), or null.</summary>
    public static string? PathOf(SafeHandle handle)
    {
        var buf = new byte[MaxPath];
        var fd = (int)handle.DangerousGetHandle();
        var rc = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? fcntl_va(fd, F_GETPATH, 0, 0, 0, 0, 0, 0, buf)
            : fcntl_std(fd, F_GETPATH, buf);
        if (rc == -1) return null;
        var n = Array.IndexOf(buf, (byte)0);
        return n <= 0 ? null : Encoding.UTF8.GetString(buf, 0, n);
    }
}
