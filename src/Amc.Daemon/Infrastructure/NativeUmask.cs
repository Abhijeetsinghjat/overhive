using System.Runtime.InteropServices;

namespace Amc.Daemon.Infrastructure;

internal static partial class NativeUmask
{
    [LibraryImport("libc", EntryPoint = "umask")]
    private static partial uint umask(uint mask);

    public static void Set(uint mask) => umask(mask);
}
