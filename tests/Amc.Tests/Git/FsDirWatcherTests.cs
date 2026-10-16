using System.Collections.Concurrent;
using Amc.Daemon.Collectors.Git;

namespace Amc.Tests.Git;

[Collection(WatcherCollection.Name)]
public class FsDirWatcherTests
{
    [Fact]
    public async Task Reports_changed_entries_with_inotify_bits()
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

            // The collector only needs to learn that an entry changed (it then refreshes); the exact bits vary:
            // FSEvents on macOS may report a rename as a delete of the old name and nothing for the new one.
            bool Has(string name, uint bits = uint.MaxValue) => events.Any(e => e.Dir == dir && e.Name == name && (e.Mask & bits) != 0);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until && !(Has("HEAD") && Has("ORIG_HEAD") && Has("sub", Inotify.IsDir)))
                await Task.Delay(50);

            var seen = string.Join(", ", events);
            Assert.True(Has("HEAD", Inotify.Create | Inotify.CloseWrite), seen);
            Assert.True(Has("ORIG_HEAD"), seen);
            Assert.True(Has("sub", Inotify.IsDir), seen);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            {
                // These report renames and deletes precisely.
                Assert.True(Has("ORIG_HEAD", Inotify.MovedTo), seen);
                Assert.True(Has("ORIG_HEAD", Inotify.Delete), seen);
            }

            w.Remove(dir);
            Assert.False(w.IsWatched(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
