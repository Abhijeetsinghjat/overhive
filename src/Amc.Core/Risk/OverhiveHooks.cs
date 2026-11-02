using System.Text.RegularExpressions;
using Amc.Core.Providers;
using Amc.Core.Providers.Claude;

namespace Amc.Core.Risk;

/// <summary>
/// Recognises Overhive's own hook commands so the risk recorder never reports them as the agent's actions.
/// AI tools run our hooks as child processes of the agent (the process collector sees them as
/// <c>/bin/sh -c '… # agent-mission-control'</c>), and the rewake waiter appends to Overhive's own rewake.log.
/// The match is exact: the command must be one of the templates <see cref="ClaudeHooksConfig"/>,
/// <see cref="ToolHooksConfig"/> and <see cref="HookForwarder"/> generate, with only the socket path, tool and
/// event free. A command that merely mentions the marker or the state directory (<c>cat …/token</c>,
/// <c>rm -rf ~ # agent-mission-control</c>) is classified as usual.
/// </summary>
public static class OverhiveHooks
{
    /// <summary>A word quoted by <see cref="ClaudeHooksConfig.ShellQuote"/>: <c>'…'</c> with <c>'\''</c> for each quote.</summary>
    private const string Quoted = @"'[^']*'(?:\\''[^']*')*";

    private static readonly Regex[] Templates = BuildTemplates();

    private static Regex[] BuildTemplates()
    {
        const string socket = "/OVERHIVE-SOCKET-DIR/amcd.sock";
        var quotedSocket = Regex.Escape(ClaudeHooksConfig.ShellQuote(socket));
        // Derived exactly like BuildRewakeCommand derives it, so the template matches on every OS.
        var quotedLog = Regex.Escape(ClaudeHooksConfig.ShellQuote(
            Path.Combine(Path.GetDirectoryName(socket) ?? ".", ClaudeHooksConfig.RewakeLogFileName)));

        Regex Template(string command) => new(
            "^" + Regex.Escape(command).Replace(quotedLog, Quoted).Replace(quotedSocket, Quoted)
                .Replace("TOOLX", "[a-z][a-z0-9-]*").Replace("EVENTX", "[A-Za-z]+") + "$",
            RegexOptions.CultureInvariant);

        return
        [
            Template(ClaudeHooksConfig.BuildCommand(socket)),
            Template(ClaudeHooksConfig.BuildRewakeCommand(socket)),
            Template(ToolHooksConfig.BuildCommand(socket, "TOOLX", "EVENTX")),
        ];
    }

    private static readonly HashSet<string> Shells = ["sh", "bash", "dash", "zsh"];

    /// <summary>True when <paramref name="command"/> is exactly one of Overhive's hook commands (bare or as <c>sh -c '…'</c>).</summary>
    public static bool IsHookCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || !command.Contains(ClaudeHooksConfig.Marker, StringComparison.Ordinal)) return false;
        var text = command.Trim();
        if (MatchesTemplate(text) || IsForwarder(text)) return true;

        // As the process collector sees it: /bin/sh -c '<hook command>'.
        var cmds = ShellLexer.Parse(text, ShellDialect.Posix);
        if (cmds.Count != 1 || cmds[0].Redirects.Count > 0) return false;
        var w = cmds[0].Words;
        return w.Count == 3 && Shells.Contains(ExeName(w[0])) && w[1] == "-c" && MatchesTemplate(w[2].Trim());
    }

    private static bool MatchesTemplate(string script) => Templates.Any(t => t.IsMatch(script));

    /// <summary>
    /// The Windows hook: <c>agentctl hook-forward &lt;tool&gt; [--event E] [--wait] --socket S --tag agent-mission-control</c>
    /// (exec form, cmd.exe line, or PowerShell <c>&amp; 'agentctl.exe' …</c>; also under the dotnet host with agentctl.dll).
    /// </summary>
    private static bool IsForwarder(string text)
    {
        foreach (var dialect in (ShellDialect[])[ShellDialect.Cmd, ShellDialect.PowerShell])
        {
            var cmds = ShellLexer.Parse(text, dialect);
            if (cmds.Count != 1 || cmds[0].Redirects.Count > 0) continue;
            var w = cmds[0].Words.SkipWhile(x => x == "&").ToList();
            var i = 0;
            if (i < w.Count && ExeName(w[i]) == "dotnet") i++;
            if (i >= w.Count || ExeName(w[i]) != "agentctl") continue;
            i++;
            if (i + 1 >= w.Count || w[i] != HookForwarder.Subcommand) continue;
            i += 2; // subcommand, tool
            var ok = w.Count >= i + 4 && w[^2] == "--tag" && w[^1] == ClaudeHooksConfig.Marker;
            for (var j = i; ok && j < w.Count - 2; j++)
            {
                if (w[j] is "--event" or "--socket" && j + 1 < w.Count - 2) j++;
                else if (w[j] != "--wait") ok = false;
            }
            if (ok) return true;
        }
        return false;
    }

    /// <summary>File name without directory and .exe/.dll, lower case.</summary>
    private static string ExeName(string word)
    {
        var n = word[(Math.Max(word.LastIndexOf('/'), word.LastIndexOf('\\')) + 1)..].ToLowerInvariant();
        foreach (var ext in (string[])[".exe", ".dll"])
            if (n.EndsWith(ext)) return n[..^ext.Length];
        return n;
    }
}
