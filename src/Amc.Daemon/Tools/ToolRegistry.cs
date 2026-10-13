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
        if (command.StartsWith('/')) return File.Exists(command) ? command : null;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(AmcOptions.Home, ".local", "bin"))
            .Append(Path.Combine(AmcOptions.Home, ".npm-global", "bin"))
            .Select(d => Path.Combine(d, command)).FirstOrDefault(File.Exists);
    }
}
