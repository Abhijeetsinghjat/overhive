using Amc.Core.Domain;
using Amc.Daemon.Infrastructure;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Tools;

/// <summary>The AI tools Overhive knows: built-in definitions merged with the user's config.json "Tools".</summary>
public sealed class ToolRegistry(IOptions<AmcOptions> options)
{
    private readonly List<AgentTool> _tools = AgentToolCatalog.Merge(options.Value.Tools).Where(t => t.Enabled).ToList();

    public IReadOnlyList<AgentTool> All => _tools;
    public AgentTool? Get(string id) => _tools.FirstOrDefault(t => t.Id == id);

    /// <summary>Absolute path of the tool's executable, or null when it is not installed.</summary>
    public static string? FindExecutable(string command)
    {
        if (PathText.IsAbsolute(command)) return Executables(command).FirstOrDefault(File.Exists);
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(AmcOptions.Home, ".local", "bin"))
            .Append(Path.Combine(AmcOptions.Home, ".npm-global", "bin"))
            .SelectMany(d => Executables(Path.Combine(d, command))).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The files that would run for <paramref name="path"/>: itself, or on Windows (when it has no extension) the
    /// PATHEXT variants "x.exe", "x.cmd" … — npm's extension-less shim there is a shell script Windows cannot start.
    /// </summary>
    public static IEnumerable<string> Executables(string path)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(path)) return [path];
        return (Environment.GetEnvironmentVariable("PATHEXT") is { Length: > 0 } e ? e : ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ext => path + ext.ToLowerInvariant());
    }
}
