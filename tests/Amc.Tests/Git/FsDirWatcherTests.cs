using System.Collections.Concurrent;
using Amc.Daemon.Collectors.Git;

namespace Amc.Tests.Git;

public class FsDirWatcherTests
{
    [Fact]
    public async Task Reports_create_change_rename_and_delete_with_inotify_bits()
    {
        var dir = Directory.CreateTempSubdirectory("amc-fsw-").FullName;
        try
        {
            using var w = new FsDirWatcher();
            var events = new ConcurrentQueue<(string Dir, string? Name, uint Mask)>();
            w.Changed += (d, n, m) => events.Enqueue((d, n, m));
            Assert.True(w.Add(dir));
            Assert.True(w.IsWatched(dir));
            Assert.Equal(1, w.Count);
            await Task.Delay(300); // FSEvents on macOS starts asynchronously

            await File.WriteAllTextAsync(Path.Combine(dir, "HEAD"), "ref: refs/heads/main\n");
            File.Move(Path.Combine(dir, "HEAD"), Path.Combine(dir, "ORIG_HEAD"));
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.Delete(Path.Combine(dir, "ORIG_HEAD"));

            bool Has(string name, uint bit) => events.Any(e => e.Dir == dir && e.Name == name && (e.Mask & bit) != 0);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until && !(Has("ORIG_HEAD", Inotify.MovedTo) && Has("sub", Inotify.IsDir) && Has("ORIG_HEAD", Inotify.Delete)))
                await Task.Delay(50);

            Assert.True(Has("HEAD", Inotify.Create | Inotify.CloseWrite | Inotify.MovedFrom), string.Join(", ", events));
            Assert.True(Has("ORIG_HEAD", Inotify.MovedTo), string.Join(", ", events));
            Assert.True(Has("sub", Inotify.IsDir), string.Join(", ", events));
            Assert.True(Has("ORIG_HEAD", Inotify.Delete), string.Join(", ", events));

            w.Remove(dir);
            Assert.False(w.IsWatched(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
