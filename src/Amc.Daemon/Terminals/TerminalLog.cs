namespace Amc.Daemon.Terminals;

/// <summary>
/// Append-only raw output log per terminal (&lt;TerminalDir&gt;/&lt;id&gt;.log, 0600), rotated to
/// &lt;id&gt;.log.1 when it exceeds <see cref="MaxBytes"/> (so at most 2× on disk per terminal).
/// </summary>
public sealed class TerminalLog : IDisposable
{
    public const long DefaultMaxBytes = 8L * 1024 * 1024;

    private readonly string _path;
    private readonly long _maxBytes;
    private FileStream? _fs;

    public TerminalLog(string dir, string terminalId, long maxBytes = DefaultMaxBytes)
    {
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = PathFor(dir, terminalId);
        _maxBytes = maxBytes;
    }

    public static string PathFor(string dir, string terminalId) => Path.Combine(dir, terminalId + ".log");

    public void Append(ReadOnlySpan<byte> data)
    {
        _fs ??= Open();
        if (_fs.Length + data.Length > _maxBytes && _fs.Length > 0)
        {
            _fs.Dispose();
            File.Move(_path, _path + ".1", overwrite: true);
            _fs = Open();
        }
        _fs.Write(data);
        _fs.Flush();
    }

    private FileStream Open()
    {
        var o = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(_path, o);
    }

    /// <summary>Last <paramref name="max"/> bytes of the log (current + rotated file). Used after a daemon restart.</summary>
    public static byte[] ReadTail(string dir, string terminalId, int max)
    {
        var path = PathFor(dir, terminalId);
        try
        {
            var current = File.Exists(path) ? Tail(path, max) : [];
            if (current.Length >= max || !File.Exists(path + ".1")) return current;
            var older = Tail(path + ".1", max - current.Length);
            return [.. older, .. current];
        }
        catch (IOException) { return []; }
    }

    private static byte[] Tail(string path, int max)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var n = (int)Math.Min(max, fs.Length);
        fs.Seek(-n, SeekOrigin.End);
        var buf = new byte[n];
        fs.ReadExactly(buf);
        return buf;
    }

    public void Dispose() => _fs?.Dispose();
}
