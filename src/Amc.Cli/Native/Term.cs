namespace Amc.Cli.Native;

/// <summary>The local terminal (fd 0/1/2) on every OS: libc on Unix, the console API on Windows.</summary>
public static class Term
{
    public static bool IsTerminal(int fd)
    {
        if (!OperatingSystem.IsWindows()) return Libc.IsTty(fd);
        return Kernel32.GetConsoleMode(StdHandle(fd), out _);
    }

    /// <summary>Visible size of the terminal behind <paramref name="fd"/>, or null when it is not one.</summary>
    public static WinSize? GetSize(int fd)
    {
        if (!OperatingSystem.IsWindows()) return Libc.GetWinSize(fd);
        if (!Kernel32.GetConsoleScreenBufferInfo(StdHandle(fd), out var info)) return null;
        var ws = new WinSize(info.Window.Right - info.Window.Left + 1, info.Window.Bottom - info.Window.Top + 1);
        return ws.IsValid ? ws : null;
    }

    public static bool WriteAll(int fd, ReadOnlySpan<byte> data) =>
        OperatingSystem.IsWindows() ? Kernel32.WriteAll(StdHandle(fd), data) : Libc.WriteAll(fd, data);

    /// <summary>Blocking read from stdin on Windows. Returns 0 at end of input or on error.</summary>
    public static unsafe int ReadStdIn(Span<byte> buffer)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        fixed (byte* p = buffer)
            return Kernel32.ReadFile(StdHandle(0), p, buffer.Length, out var n, 0) ? n : 0;
    }

    private static nint StdHandle(int fd)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return Kernel32.GetStdHandle(fd switch { 0 => Kernel32.STD_INPUT_HANDLE, 1 => Kernel32.STD_OUTPUT_HANDLE, _ => Kernel32.STD_ERROR_HANDLE });
    }

    /// <summary>
    /// Prepares the Windows console: UTF-8 code pages and VT processing on output (ConPTY emits VT sequences), and with
    /// <paramref name="rawInput"/> no line editing, echo or ^C processing on input, keys arriving as VT sequences.
    /// Returns null when stdout is not a console.
    /// </summary>
    public static RawModeGuard? EnterWindowsConsole(bool rawInput)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var hOut = StdHandle(1);
        if (!Kernel32.GetConsoleMode(hOut, out var outMode)) return null;
        var hIn = StdHandle(0);
        var inMode = 0u;
        var haveIn = rawInput && Kernel32.GetConsoleMode(hIn, out inMode);
        uint inCp = Kernel32.GetConsoleCP(), outCp = Kernel32.GetConsoleOutputCP();

        Kernel32.SetConsoleOutputCP(Kernel32.CP_UTF8);
        Kernel32.SetConsoleMode(hOut, outMode | Kernel32.ENABLE_PROCESSED_OUTPUT | Kernel32.ENABLE_VIRTUAL_TERMINAL_PROCESSING | Kernel32.DISABLE_NEWLINE_AUTO_RETURN);
        if (haveIn)
        {
            Kernel32.SetConsoleCP(Kernel32.CP_UTF8);
            Kernel32.SetConsoleMode(hIn, (inMode & ~(Kernel32.ENABLE_PROCESSED_INPUT | Kernel32.ENABLE_LINE_INPUT | Kernel32.ENABLE_ECHO_INPUT))
                                         | Kernel32.ENABLE_VIRTUAL_TERMINAL_INPUT);
        }
        return new RawModeGuard(() =>
        {
            if (!OperatingSystem.IsWindows()) return;
            if (haveIn) { Kernel32.SetConsoleMode(hIn, inMode); Kernel32.SetConsoleCP(inCp); }
            Kernel32.SetConsoleMode(hOut, outMode);
            Kernel32.SetConsoleOutputCP(outCp);
        });
    }
}
