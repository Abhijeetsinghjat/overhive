using System.Text.RegularExpressions;

namespace Amc.Core.Domain;

/// <summary>
/// One AI coding tool Overhive can launch and recognise. Tools authenticate themselves (the user's own
/// subscription or key); Overhive never sees or stores those credentials. Users add or override tools in
/// config.json under "Tools".
/// </summary>
public sealed partial class AgentTool
{
    /// <summary>Stable id, also the provider prefix of agent ids (e.g. "codex" → "codex:…").</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Executable to launch (looked up on PATH and in ~/.local/bin).</summary>
    public string Command { get; set; } = "";
    /// <summary>
    /// Names that identify a running instance: matched against the process name, the executable and, for
    /// script-based tools (node/python), the script argument's file name.
    /// </summary>
    public List<string> ProcessNames { get; set; } = [];
    /// <summary>Flag that selects a model when launching, e.g. "--model". Null: the tool's default model only.</summary>
    public string? ModelFlag { get; set; }
    /// <summary>Suggested model names for the launch dialog (free text is also accepted).</summary>
    public List<string> Models { get; set; } = [];
    /// <summary>"positional" (first message as an argument), a flag such as "-p", or null (type it in the terminal).</summary>
    public string? PromptArg { get; set; }
    /// <summary>Id of a native adapter that integrates more deeply (e.g. "claude"); null = generic integration.</summary>
    public string? Adapter { get; set; }
    /// <summary>Hidden from launch lists and process discovery when false.</summary>
    public bool Enabled { get; set; } = true;

    public static bool ValidId(string id) => IdPattern().IsMatch(id);
    public static bool ValidModel(string m) => ModelPattern().IsMatch(m);

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex IdPattern();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@-]{0,79}$")]
    private static partial Regex ModelPattern();
}

public static class AgentToolCatalog
{
    /// <summary>
    /// Built-in definitions. Only the Claude Code entry has a native adapter today; the others get the
    /// generic integration (process, terminal, git, files) until an adapter or the open protocol adds more.
    /// Launch flags for tools other than Claude Code are left unset until verified against their docs.
    /// </summary>
    public static List<AgentTool> Defaults() =>
    [
        new() { Id = "claude", Name = "Claude Code", Command = "claude", ProcessNames = ["claude"], ModelFlag = "--model",
                Models = ["opus", "sonnet", "haiku"], PromptArg = "positional", Adapter = "claude" },
        new() { Id = "codex", Name = "Codex CLI", Command = "codex", ProcessNames = ["codex"] },
        new() { Id = "gemini", Name = "Gemini CLI", Command = "gemini", ProcessNames = ["gemini"] },
        new() { Id = "grok", Name = "Grok CLI", Command = "grok", ProcessNames = ["grok"] },
        new() { Id = "aider", Name = "Aider", Command = "aider", ProcessNames = ["aider"] },
        new() { Id = "opencode", Name = "OpenCode", Command = "opencode", ProcessNames = ["opencode"] },
        new() { Id = "cursor", Name = "Cursor CLI", Command = "cursor-agent", ProcessNames = ["cursor-agent"] },
        new() { Id = "copilot", Name = "GitHub Copilot CLI", Command = "copilot", ProcessNames = ["copilot"] },
    ];

    /// <summary>Defaults merged with user entries (same id overrides, new ids are added); invalid entries are dropped.</summary>
    public static List<AgentTool> Merge(IEnumerable<AgentTool>? user)
    {
        var byId = Defaults().ToDictionary(t => t.Id);
        foreach (var t in user ?? [])
        {
            if (!AgentTool.ValidId(t.Id) || string.IsNullOrWhiteSpace(t.Command) || IsRelativePath(t.Command)) continue;
            if (t.ProcessNames.Count == 0) t.ProcessNames = [Path.GetFileName(t.Command)];
            if (string.IsNullOrEmpty(t.Name)) t.Name = t.Id;
            byId[t.Id] = t;
        }
        return [.. byId.Values];
    }

    /// <summary>A command with a directory part that is not absolute ("bin/x", and on Windows "bin\x"): ambiguous, so refused.</summary>
    private static bool IsRelativePath(string command) =>
        (command.Contains('/') || PathText.Windows && command.Contains('\\')) && !PathText.IsAbsolute(command);
}
