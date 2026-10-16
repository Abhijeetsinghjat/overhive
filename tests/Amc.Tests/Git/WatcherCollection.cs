namespace Amc.Tests.Git;

/// <summary>
/// Tests that need real file watchers. On Linux each watcher costs one of the user's inotify instances
/// (fs.inotify.max_user_instances, often 128 and shared with editors and other apps); run in parallel with the
/// daemon-host tests they can find none left. xUnit runs this collection alone, after the parallel ones.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WatcherCollection
{
    public const string Name = "watchers";
}
