using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Amc.Cli.Native;

/// <summary>Minimal kernel32 bindings for ConPTY, job objects and the local console (Windows 10 1809+).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Kernel32
{
    private const string K = "kernel32.dll";

    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400, CREATE_SUSPENDED = 0x00000004;
    public const int STARTF_USESTDHANDLES = 0x00000100;
    public static readonly nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    public const int JobObjectExtendedLimitInformationClass = 9;
    public const uint INFINITE = 0xFFFFFFFF, WAIT_OBJECT_0 = 0;
    public const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;
    public const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5, ERROR_BAD_EXE_FORMAT = 193, ERROR_DIRECTORY = 267;

    // Console modes.
    public const uint ENABLE_PROCESSED_INPUT = 0x1, ENABLE_LINE_INPUT = 0x2, ENABLE_ECHO_INPUT = 0x4, ENABLE_VIRTUAL_TERMINAL_INPUT = 0x200;
    public const uint ENABLE_PROCESSED_OUTPUT = 0x1, ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x4, DISABLE_NEWLINE_AUTO_RETURN = 0x8;
    public const uint CP_UTF8 = 65001;

    [StructLayout(LayoutKind.Sequential)]
    public struct Coord { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SmallRect { public short Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ConsoleScreenBufferInfo
    {
        public Coord Size, CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoExW
    {
        public StartupInfoW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport(K)] public static partial int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out nint hPC);
    [LibraryImport(K)] public static partial int ResizePseudoConsole(nint hPC, Coord size);
    [LibraryImport(K)] public static partial void ClosePseudoConsole(nint hPC);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, nint attributes, uint size);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekNamedPipe(SafeFileHandle pipe, nint buffer, uint bufferSize, nint bytesRead, out uint totalAvailable, nint bytesLeft);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(nint handle, byte* buffer, int count, out int read, nint overlapped);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(nint handle, byte* buffer, int count, out int written, nint overlapped);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [LibraryImport(K)] public static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport(K, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(string? applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, char* environment, string? currentDirectory,
        StartupInfoExW* startupInfo, ProcessInformation* processInformation);

    [LibraryImport(K, SetLastError = true)] public static partial uint ResumeThread(SafeFileHandle thread);
    [LibraryImport(K, SetLastError = true)] public static partial uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeFileHandle process, uint exitCode);

    [LibraryImport(K, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateJobObjectW(nint attributes, string? name);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, void* info, int size);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [LibraryImport(K, SetLastError = true)] public static partial nint GetStdHandle(int which);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleMode(nint handle, uint mode);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetConsoleScreenBufferInfo(nint handle, out ConsoleScreenBufferInfo info);

    [LibraryImport(K)] public static partial uint GetConsoleCP();
    [LibraryImport(K)] public static partial uint GetConsoleOutputCP();

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleCP(uint codePage);

    [LibraryImport(K, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleOutputCP(uint codePage);

    /// <summary>Writes the whole span to a file/pipe/console handle.</summary>
    public static bool WriteAll(nint handle, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            var off = 0;
            while (off < data.Length)
            {
                if (!WriteFile(handle, p + off, data.Length - off, out var n, 0) || n <= 0) return false;
                off += n;
            }
        }
        return true;
    }
}
