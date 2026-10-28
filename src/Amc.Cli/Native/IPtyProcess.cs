namespace Amc.Cli.Native;

/// <summary>A child process running on a pseudo-terminal we own: a Unix PTY or a Windows ConPTY.</summary>
public interface IPtyProcess : IDisposable
{
    int Pid { get; }

    /// <summary>How long output must stay quiet after the child exited before the reader gives up.</summary>
    int IdleAfterExitMs { get; }

    /// <summary>Reads terminal output: &gt;0 bytes read, 0 nothing within <paramref name="timeoutMs"/>, -1 closed for good.</summary>
    int Read(Span<byte> buffer, int timeoutMs);

    /// <summary>Thread-safe write of input bytes into the terminal (user keystrokes and injected input).</summary>
    bool Write(ReadOnlySpan<byte> data);

    bool Resize(WinSize size);

    /// <summary>Blocks until the child exits and returns its exit code.</summary>
    int WaitForExit();

    /// <summary>Asks the child to end as if its terminal went away (SIGHUP; on Windows the console is closed).</summary>
    void Hangup();
}

/// <summary>Picks the pseudo-terminal backend for this OS.</summary>
public static class Pty
{
    /// <summary>Starts <paramref name="argv"/> on a new pseudo-terminal. Throws <see cref="PtySpawnException"/>.</summary>
    public static IPtyProcess Spawn(IReadOnlyList<string> argv, string? cwd, IDictionary<string, string?> envOverrides, WinSize size) =>
        OperatingSystem.IsWindows()
            ? ConPtyProcess.Spawn(argv, cwd, envOverrides, size)
            : PtyProcess.Spawn(argv, cwd, envOverrides, size);
}
