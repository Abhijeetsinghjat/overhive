namespace Amc.Daemon.Collectors.Git;

/// <summary>
/// Non-recursive directory watches for Git metadata. Events use inotify's mask bits on every OS:
/// (directory, entry name or null for the directory itself, mask); overflow is reported with directory "".
/// </summary>
public interface IDirWatcher : IDisposable
{
    event Action<string, string?, uint>? Changed;
    int Count { get; }
    bool Add(string dir);
    void Remove(string dir);
    bool IsWatched(string dir);
}

public static class DirWatchers
{
    /// <summary>inotify on Linux; the runtime's watcher (FSEvents on macOS, ReadDirectoryChangesW on Windows) elsewhere.</summary>
    public static IDirWatcher? TryCreate(out string? error)
    {
        if (OperatingSystem.IsLinux()) return Inotify.TryCreate(out error);
        error = null;
        return new FsDirWatcher();
    }
}

/// <summary>
/// One <see cref="FileSystemWatcher"/> per directory, translating its events to inotify mask bits.
/// Not used on Linux, where every FileSystemWatcher costs one of the user's limited inotify instances.
/// </summary>
public sealed class FsDirWatcher : IDirWatcher
{
    public event Action<string, string?, uint>? Changed;

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private bool _disposed;

    public int Count { get { lock (_watchers) return _watchers.Count; } }

    public bool IsWatched(string dir) { lock (_watchers) return _watchers.ContainsKey(dir); }

    public bool Add(string dir)
    {
        lock (_watchers)
        {
            if (_disposed) return false;
            if (_watchers.ContainsKey(dir)) return true;
            if (!Directory.Exists(dir)) return false;
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                w.Created += (_, e) => Raise(dir, e.Name, Inotify.Create | DirBit(e.FullPath));
                w.Deleted += (_, e) => Raise(dir, e.Name, Inotify.Delete);
                w.Changed += (_, e) => Raise(dir, e.Name, Inotify.CloseWrite);
                w.Renamed += (_, e) =>
                {
                    Raise(dir, e.OldName, Inotify.MovedFrom);
                    Raise(dir, e.Name, Inotify.MovedTo | DirBit(e.FullPath));
                };
                w.Error += (_, _) => Changed?.Invoke("", null, Inotify.Overflow);
                w.EnableRaisingEvents = true;
                _watchers[dir] = w;
                return true;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    public void Remove(string dir)
    {
        lock (_watchers)
            if (_watchers.Remove(dir, out var w)) w.Dispose();
    }

    public void Dispose()
    {
        lock (_watchers)
        {
            _disposed = true;
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
    }

    private static uint DirBit(string path) => Directory.Exists(path) ? Inotify.IsDir : 0;

    private void Raise(string dir, string? name, uint mask)
    {
        try { Changed?.Invoke(dir, name, mask); }
        catch (Exception) { /* a handler failure must not kill the watcher; periodic rescans still run */ }
    }
}
