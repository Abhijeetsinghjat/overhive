namespace Amc.Tests.Fixtures;

/// <summary>Best-effort removal of test temp trees, including ones holding Git repositories.</summary>
public static class TempDirs
{
    /// <summary>
    /// Deletes <paramref name="path"/> recursively, never throwing. On Windows Git's object files are read-only and
    /// a just-exited process (git, the daemon) may still hold a handle, so read-only attributes are cleared and the
    /// delete is retried briefly.
    /// </summary>
    public static void Delete(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                if (OperatingSystem.IsWindows())
                    foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                        File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 10 || !OperatingSystem.IsWindows()) return;
                Thread.Sleep(100);
            }
        }
    }
}
