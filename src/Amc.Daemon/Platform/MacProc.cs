using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Amc.Daemon.Platform;

/// <summary>Basic facts about one process, the same on every platform.</summary>
/// <param name="StartId">Opaque start stamp: together with the pid it identifies one process, so a reused pid is not mistaken for it.</param>
/// <param name="State">'R' running, 'S' sleeping, 'T' stopped, 'Z' zombie, '?' unknown.</param>
/// <param name="CpuTime">User plus system CPU time; null when the OS does not tell (another user's process).</param>
public sealed record ProcBasics(int Pid, int Ppid, string Name, char State, string StartId, DateTimeOffset StartedAt, TimeSpan? CpuTime, long RssBytes);

/// <summary>
/// macOS process facts through libproc and sysctl, the equivalent of Linux /proc. Struct offsets follow
/// &lt;sys/proc_info.h&gt;. Every call tolerates the process vanishing (returns null or empty).
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacProc
{
    private const string Lib = "/usr/lib/libSystem.dylib";
    private const int PROC_PIDTBSDINFO = 3, BsdInfoSize = 136;
    private const int PROC_PIDTASKINFO = 4, TaskInfoSize = 96;
    private const int PROC_PIDVNODEPATHINFO = 9, VnodePathInfoSize = 2352, VnodeInfoSize = 152, MaxPath = 1024;
    private const int CTL_KERN = 1, KERN_ARGMAX = 8, KERN_PROCARGS2 = 49;

    [DllImport(Lib)] private static extern int proc_listallpids(int[]? buffer, int buffersize);
    [DllImport(Lib)] private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[] buffer, int buffersize);
    [DllImport(Lib, SetLastError = true)] private static extern int sysctl(int[] name, uint namelen, byte[]? oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);
    [DllImport(Lib)] private static extern int mach_timebase_info(out Timebase info);

    [StructLayout(LayoutKind.Sequential)] private struct Timebase { public uint Numer, Denom; }

    private static readonly Lazy<(uint Numer, uint Denom)> TimebaseInfo = new(() =>
        mach_timebase_info(out var t) == 0 && t.Denom != 0 ? (t.Numer, t.Denom) : (1u, 1u));

    public static int[] ListPids()
    {
        var n = proc_listallpids(null, 0);
        if (n <= 0) return [];
        var buf = new int[n + 256];
        n = proc_listallpids(buf, buf.Length * sizeof(int));
        return n <= 0 ? [] : buf[..Math.Min(n, buf.Length)];
    }

    public static ProcBasics? Basics(int pid)
    {
        var b = new byte[BsdInfoSize];
        if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, b, b.Length) != BsdInfoSize) return null;
        var s = b.AsSpan();
        var status = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        var ppid = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
        var comm = CString(s.Slice(48, 16));
        var name = CString(s.Slice(64, 32));
        var sec = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[120..]);
        var usec = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[128..]);
        var state = status switch { 1 or 2 => 'R', 3 => 'S', 4 => 'T', 5 => 'Z', _ => '?' };

        TimeSpan? cpu = null; long rss = 0;
        var t = new byte[TaskInfoSize];
        if (proc_pidinfo(pid, PROC_PIDTASKINFO, 0, t, t.Length) == TaskInfoSize)
        {
            var ts = t.AsSpan();
            rss = (long)BinaryPrimitives.ReadUInt64LittleEndian(ts[8..]);
            var machUnits = BinaryPrimitives.ReadUInt64LittleEndian(ts[16..]) + BinaryPrimitives.ReadUInt64LittleEndian(ts[24..]);
            var (numer, denom) = TimebaseInfo.Value;
            cpu = TimeSpan.FromTicks((long)(machUnits * (double)numer / denom / 100)); // ns → 100 ns ticks
        }
        return new ProcBasics(pid, ppid, name.Length > 0 ? name : comm, state,
            $"{sec}.{usec:D6}", DateTimeOffset.FromUnixTimeSeconds(sec).AddTicks(usec * 10), cpu, rss);
    }

    /// <summary>Current working directory; null when not permitted (another user's process) or gone.</summary>
    public static string? Cwd(int pid)
    {
        var b = new byte[VnodePathInfoSize];
        if (proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, b, b.Length) != VnodePathInfoSize) return null;
        var path = CString(b.AsSpan(VnodeInfoSize, MaxPath));
        return path.Length > 0 ? path : null;
    }

    /// <summary>argv and the environment block (KERN_PROCARGS2); null when not permitted or gone.</summary>
    public static (IReadOnlyList<string> Argv, IReadOnlyList<string> Env)? Args(int pid)
    {
        var max = new byte[4];
        nuint len = 4;
        if (sysctl([CTL_KERN, KERN_ARGMAX], 2, max, ref len, IntPtr.Zero, 0) != 0) return null;
        var size = BinaryPrimitives.ReadInt32LittleEndian(max);
        if (size <= 0 || size > 16 << 20) size = 1 << 20;
        var buf = new byte[size];
        len = (nuint)size;
        if (sysctl([CTL_KERN, KERN_PROCARGS2, pid], 3, buf, ref len, IntPtr.Zero, 0) != 0 || len < 4) return null;
        return ParseProcArgs(buf.AsSpan(0, (int)len));
    }

    /// <summary>Layout: int argc, exec path, NUL padding, argc strings, then environment strings.</summary>
    public static (IReadOnlyList<string> Argv, IReadOnlyList<string> Env) ParseProcArgs(ReadOnlySpan<byte> b)
    {
        var argc = BinaryPrimitives.ReadInt32LittleEndian(b);
        var i = 4;
        while (i < b.Length && b[i] != 0) i++;   // exec path
        while (i < b.Length && b[i] == 0) i++;   // padding
        var argv = new List<string>(Math.Max(argc, 0));
        var env = new List<string>();
        while (i < b.Length)
        {
            var end = b[i..].IndexOf((byte)0);
            if (end < 0) end = b.Length - i;
            if (end == 0) { if (argv.Count >= argc) break; i++; continue; }
            var str = Encoding.UTF8.GetString(b.Slice(i, end));
            if (argv.Count < argc) argv.Add(str); else env.Add(str);
            i += end + 1;
        }
        return (argv, env);
    }

    private static string CString(ReadOnlySpan<byte> b)
    {
        var n = b.IndexOf((byte)0);
        return Encoding.UTF8.GetString(n < 0 ? b : b[..n]);
    }
}
