namespace Amc.Cli.Infrastructure;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int DaemonUnreachable = 3;
    public const int Interrupted = 130;
}

/// <summary>A failure with a message meant for the user. <see cref="ExitCode"/> decides the process exit code.</summary>
public class CliException(string message, int exitCode = ExitCodes.Error) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public sealed class UsageException(string message) : CliException(message, ExitCodes.Usage);

public sealed class DaemonUnreachableException(string message) : CliException(message, ExitCodes.DaemonUnreachable);

/// <summary>The daemon answered with a non-success status.</summary>
public sealed class ApiException(int status, string message) : CliException(message)
{
    public int Status { get; } = status;
}
