using Amc.Daemon.Platform;

namespace Amc.Daemon.Collectors.Processes;

/// <summary>The process source for the running OS: /proc on Linux, libproc on macOS.</summary>
public static class ProcSources
{
    public static IProcSource Create() => OperatingSystem.IsMacOS() ? new MacProcSource() : new ProcFs();
    public static IProcSource Default { get; } = Create();
}
