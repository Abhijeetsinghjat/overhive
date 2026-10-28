using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Amc.Daemon.Platform;

/// <summary>One process from a system-wide snapshot. Times are FILETIME units (100 ns; creation time counted from 1601).</summary>
/// <param name="Ppid">Parent pid, or 0 when the recorded parent is gone and its pid now belongs to a younger process.</param>
/// <param name="Suspended">Every thread is suspended (NtSuspendProcess, a debugger, or created suspended).</param>
public sealed record WinProcInfo(int Pid, int Ppid, int SessionId, string Name, long CreateTime, long CpuTime, long WorkingSetBytes, int Threads, bool Suspended);

/// <summary>
/// Windows process facts and control through ntdll/kernel32/advapi32, the equivalent of Linux /proc and kill(2).
/// The process list comes from one NtQuerySystemInformation(SystemProcessInformation) call, which carries pid, parent,
/// creation time, CPU, working set and thread states for every process without opening a handle. Command line, cwd,
/// environment and owner need a handle and are read per process; they return null when access is denied (another
/// user's, elevated or protected process) or the process is gone. Struct offsets are the 64-bit layouts
/// (x64 and arm64); <see cref="IsSupported"/> is false in a 32-bit process.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WinProc
{
    private const int SystemProcessInformation = 5, ProcessBasicInformation = 0, ProcessCommandLineInformation = 60, TokenUser = 1;
    private const int ProcInfoSize = 0x100, ThreadInfoSize = 0x50, ThreadStateWaiting = 5, WaitReasonSuspended = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint ProcessTerminate = 0x0001, ProcessVmRead = 0x0010, ProcessSuspendResume = 0x0800,
        ProcessQueryLimited = 0x1000, Synchronize = 0x00100000, TokenQuery = 0x0008;
    private const int ErrorAccessDenied = 5;
    private const uint WaitTimeout = 0x102;
    // PEB.ProcessParameters; RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath, .Environment, .EnvironmentSize.
    private const int PebParams = 0x20, ParamsCwd = 0x38, ParamsEnv = 0x80, ParamsEnvSize = 0x3F0;
    private const int MaxEnvBytes = 1 << 20;

    /// <summary>True in a 64-bit process, the only layout this class reads.</summary>
    public static bool IsSupported => Environment.Is64BitProcess;

    [LibraryImport("ntdll.dll")] private static unsafe partial int NtQuerySystemInformation(int cls, void* buf, uint len, out uint needed);
    [LibraryImport("ntdll.dll")] private static unsafe partial int NtQueryInformationProcess(nint h, int cls, void* buf, uint len, out uint needed);
    [LibraryImport("ntdll.dll")] private static partial int NtSuspendProcess(nint h);
    [LibraryImport("ntdll.dll")] private static partial int NtResumeProcess(nint h);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint h);
    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadProcessMemory(nint h, nint address, void* buf, nuint size, out nuint read);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint h, out long creation, out long exit, out long kernel, out long user);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TerminateProcess(nint h, uint exitCode);
    [LibraryImport("kernel32.dll")] private static partial uint WaitForSingleObject(nint h, uint ms);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenProcessToken(nint h, uint access, out nint token);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(nint token, int cls, void* buf, uint len, out uint needed);

    /// <summary>
    /// Every process, from one kernel snapshot. <paramref name="buffer"/> is reused between calls (it is pinned, so the
    /// image-name pointers inside it stay valid while parsing) and grown when the system has more processes.
    /// Parent pids are validated: a parent created after its child is a reused pid, so the child reports parent 0.
    /// </summary>
    public static unsafe List<WinProcInfo> Processes(ref byte[]? buffer)
    {
        buffer ??= GC.AllocateUninitializedArray<byte>(512 << 10, pinned: true);
        for (var attempt = 0; ; attempt++)
        {
            fixed (byte* p = buffer)
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, p, (uint)buffer.Length, out var needed);
                if ((uint)status == StatusInfoLengthMismatch && attempt < 5)
                {
                    buffer = GC.AllocateUninitializedArray<byte>((int)Math.Max(needed + (64u << 10), (uint)buffer.Length * 2), pinned: true);
                    continue;
                }
                if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");
                return WinProcText.ValidateParents(Parse(buffer, (nint)p));
            }
        }
    }

    private static List<WinProcInfo> Parse(ReadOnlySpan<byte> b, nint baseAddress)
    {
        var list = new List<WinProcInfo>();
        for (var off = 0; off + ProcInfoSize <= b.Length;)
        {
            var e = b[off..];
            var next = (int)U32(e);
            var threads = (int)U32(e[4..]);
            var pid = (int)I64(e[0x50..]);
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(e[0x38..]);
            var nameAt = (long)(nint)I64(e[0x40..]) - baseAddress;
            var name = pid == 0 ? "Idle"
                : nameLen > 0 && nameAt >= 0 && nameAt + nameLen <= b.Length ? Encoding.Unicode.GetString(b.Slice((int)nameAt, nameLen)) : "";
            var suspended = threads > 0 && ProcInfoSize + threads * ThreadInfoSize <= e.Length;
            for (var t = 0; t < threads && suspended; t++)
            {
                var th = e[(ProcInfoSize + t * ThreadInfoSize)..];
                suspended = U32(th[0x44..]) == ThreadStateWaiting && U32(th[0x48..]) == WaitReasonSuspended;
            }
            list.Add(new WinProcInfo(pid, (int)I64(e[0x58..]), (int)U32(e[0x64..]), name,
                I64(e[0x20..]), I64(e[0x28..]) + I64(e[0x30..]), I64(e[0x90..]), threads, suspended));
            if (next <= 0) break;
            off += next;
        }
        return list;
    }

    /// <summary>The command line as the process received it (one string; see <see cref="WinProcText.SplitCommandLine"/>).</summary>
    public static unsafe string? CommandLine(int pid) => WithHandle(pid, ProcessQueryLimited, h =>
    {
        var buf = new byte[2048];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            fixed (byte* p = buf)
            {
                var status = NtQueryInformationProcess(h, ProcessCommandLineInformation, p, (uint)buf.Length, out var needed);
                if ((uint)status == StatusInfoLengthMismatch || (status < 0 && needed > buf.Length)) { buf = new byte[needed + 64]; continue; }
                if (status < 0) return null;
                var len = BinaryPrimitives.ReadUInt16LittleEndian(buf);
                var at = (long)(nint)I64(buf.AsSpan(8)) - (nint)p;
                return at >= 0 && at + len <= buf.Length ? Encoding.Unicode.GetString(buf, (int)at, len) : null;
            }
        }
        return null;
    });

    /// <summary>Current directory from the process parameters in its PEB (no trailing separator except at a root).</summary>
    public static string? Cwd(int pid) => WithHandle(pid, ProcessQueryLimited | ProcessVmRead, h =>
    {
        if (ProcessParameters(h) is not { } pp) return null;
        Span<byte> us = stackalloc byte[16];
        if (!Read(h, pp + ParamsCwd, us)) return null;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(us);
        var bytes = new byte[len];
        if (len == 0 || !Read(h, (nint)I64(us[8..]), bytes)) return null;
        return Path.TrimEndingDirectorySeparator(Encoding.Unicode.GetString(bytes));
    });

    /// <summary>Environment strings ("NAME=value"; Windows' per-drive "=C:=…" entries included) from the PEB.</summary>
    public static IReadOnlyList<string>? EnvironmentOf(int pid) => WithHandle(pid, ProcessQueryLimited | ProcessVmRead, h =>
    {
        if (ProcessParameters(h) is not { } pp) return null;
        Span<byte> v = stackalloc byte[8];
        if (!Read(h, pp + ParamsEnv, v)) return null;
        var env = (nint)I64(v);
        if (env == 0) return null;
        var size = Read(h, pp + ParamsEnvSize, v) ? I64(v) : 0;
        byte[] block;
        if (size is > 0 and <= MaxEnvBytes)
        {
            block = new byte[size];
            if (!Read(h, env, block)) return null;
        }
        else block = ReadUntilDoubleNul(h, env);
        return WinProcText.ParseEnvironmentBlock(block);
    });

    /// <summary>
    /// True when the process runs as the same user (token SID) as this one; false for another user's process or when
    /// its token cannot be read (gone, elevated or protected).
    /// </summary>
    public static bool OwnedByCurrentUser(int pid) =>
        CurrentUser.Value is { } me && WithHandle(pid, ProcessQueryLimited, UserSid) is { } sid && sid.AsSpan().SequenceEqual(me);

    private static readonly Lazy<byte[]?> CurrentUser = new(() => UserSid(GetCurrentProcess()));

    private static unsafe byte[]? UserSid(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
        try
        {
            var buf = new byte[256];
            fixed (byte* p = buf)
            {
                if (!GetTokenInformation(token, TokenUser, p, (uint)buf.Length, out _)) return null;
                var at = (long)(nint)I64(buf) - (nint)p;  // TOKEN_USER.User.Sid points into the same buffer
                if (at < 0 || at + 8 > buf.Length) return null;
                var len = 8 + 4 * buf[at + 1];               // revision, sub-authority count, authority[6], sub-authorities
                return at + len <= buf.Length ? buf[(int)at..(int)(at + len)] : null;
            }
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Creation time (FILETIME) of a live process, or null.</summary>
    public static long? CreationTime(int pid) =>
        WithHandle<object>(pid, ProcessQueryLimited, h => GetProcessTimes(h, out var c, out _, out _, out _) ? c : null) as long?;

    /// <summary>NtSuspendProcess: suspends every thread (counted: each suspend needs one resume).</summary>
    public static bool Suspend(int pid, long? expectedStart = null) => Act(pid, ProcessSuspendResume, expectedStart, h => NtSuspendProcess(h) >= 0);

    /// <summary>NtResumeProcess: undoes one suspend of every thread.</summary>
    public static bool Resume(int pid, long? expectedStart = null) => Act(pid, ProcessSuspendResume, expectedStart, h => NtResumeProcess(h) >= 0);

    /// <summary>TerminateProcess: immediate, the process runs no cleanup code.</summary>
    public static bool Terminate(int pid, uint exitCode, long? expectedStart = null) => Act(pid, ProcessTerminate, expectedStart, h => TerminateProcess(h, exitCode));

    /// <summary>True while the process has not exited; also true when it exists but may not be opened (like kill's EPERM).</summary>
    public static bool IsAlive(int pid)
    {
        var h = OpenProcess(Synchronize, false, (uint)pid);
        if (h == 0) return Marshal.GetLastPInvokeError() == ErrorAccessDenied;
        try { return WaitForSingleObject(h, 0) == WaitTimeout; }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// The handle pins the process, so checking its creation time and acting on it through the same handle cannot hit
    /// a process that reused the pid.
    /// </summary>
    private static bool Act(int pid, uint access, long? expectedStart, Func<nint, bool> action) => WithHandle<object>(pid, access | ProcessQueryLimited, h =>
        (expectedStart is null || (GetProcessTimes(h, out var c, out _, out _, out _) && c == expectedStart)) && action(h)) is true;

    private static unsafe nint? ProcessParameters(nint h)
    {
        Span<byte> pbi = stackalloc byte[48];
        fixed (byte* p = pbi)
            if (NtQueryInformationProcess(h, ProcessBasicInformation, p, 48, out _) < 0) return null;
        var peb = (nint)I64(pbi[8..]);
        Span<byte> v = stackalloc byte[8];
        return peb != 0 && Read(h, peb + PebParams, v) && I64(v) != 0 ? (nint)I64(v) : null;
    }

    private static byte[] ReadUntilDoubleNul(nint h, nint at)
    {
        // Read page by page (a read across the end of the mapping fails as a whole) until an empty string ends the block.
        var block = new List<byte>();
        var page = new byte[4096];
        var first = (int)(4096 - (at & 4095));
        for (var chunk = first; block.Count < MaxEnvBytes; chunk = 4096)
        {
            if (!Read(h, at + block.Count, page.AsSpan(0, chunk))) break;
            block.AddRange(page.AsSpan(0, chunk));
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(block);
            for (var i = 0; i + 3 < span.Length; i += 2)
                if (span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 0 && span[i + 3] == 0) return span[..(i + 4)].ToArray();
        }
        return [.. block];
    }

    private static unsafe bool Read(nint h, nint address, Span<byte> dst)
    {
        fixed (byte* p = dst)
            return ReadProcessMemory(h, address, p, (nuint)dst.Length, out var n) && n == (nuint)dst.Length;
    }

    private static T? WithHandle<T>(int pid, uint access, Func<nint, T?> f) where T : class
    {
        if (pid <= 0) return null;
        var h = OpenProcess(access, false, (uint)pid);
        if (h == 0) return null;
        try { return f(h); }
        finally { CloseHandle(h); }
    }

    private static uint U32(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);
    private static long I64(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt64LittleEndian(b);
}

/// <summary>Pure helpers for <see cref="WinProc"/>, usable (and tested) on every OS.</summary>
public static class WinProcText
{
    /// <summary>A parent created after its child is a reused pid: such children report parent 0.</summary>
    public static List<WinProcInfo> ValidateParents(List<WinProcInfo> list)
    {
        var created = new Dictionary<int, long>(list.Count);
        foreach (var p in list) created.TryAdd(p.Pid, p.CreateTime);
        for (var i = 0; i < list.Count; i++)
            if (list[i].Ppid != 0 && created.TryGetValue(list[i].Ppid, out var parent) && parent > list[i].CreateTime)
                list[i] = list[i] with { Ppid = 0 };
        return list;
    }

    /// <summary>Splits a UTF-16 environment block (NUL-separated, ends with an empty string).</summary>
    public static IReadOnlyList<string> ParseEnvironmentBlock(ReadOnlySpan<byte> block)
    {
        var text = Encoding.Unicode.GetString(block[..(block.Length & ~1)]);
        var result = new List<string>();
        foreach (var s in text.Split('\0'))
        {
            if (s.Length == 0) break;
            result.Add(s);
        }
        return result;
    }

    /// <summary>
    /// Splits a command line the way the Microsoft C runtime (and CommandLineToArgvW) builds argv: the program name
    /// ends at the closing quote or first blank; afterwards 2n backslashes before a quote become n and the quote
    /// toggles quoting, 2n+1 become n plus a literal quote, and "" inside quotes is a literal quote.
    /// </summary>
    public static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        var s = commandLine.AsSpan();
        var i = 0;
        var sb = new StringBuilder();
        if (s.Length > 0 && s[0] == '"')
        {
            var end = s[1..].IndexOf('"');
            args.Add((end < 0 ? s[1..] : s.Slice(1, end)).ToString());
            i = end < 0 ? s.Length : end + 2;
        }
        else
        {
            while (i < s.Length && s[i] is not (' ' or '\t')) i++;
            if (i > 0) args.Add(s[..i].ToString());
        }
        while (true)
        {
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            if (i >= s.Length) break;
            var quoted = false;
            sb.Clear();
            for (; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '\\')
                {
                    var n = 0;
                    while (i < s.Length && s[i] == '\\') { n++; i++; }
                    if (i < s.Length && s[i] == '"')
                    {
                        sb.Append('\\', n / 2);
                        if (n % 2 == 1) { sb.Append('"'); continue; }
                    }
                    else { sb.Append('\\', n); }
                    i--;
                    continue;
                }
                if (c == '"')
                {
                    if (quoted && i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; continue; }
                    quoted = !quoted;
                    continue;
                }
                if (!quoted && c is ' ' or '\t') break;
                sb.Append(c);
            }
            args.Add(sb.ToString());
        }
        return args;
    }

    /// <summary>Image name without ".exe", matching what Linux and macOS report as comm.</summary>
    public static string Comm(string image) =>
        (image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image).Replace('\n', ' ');
}
