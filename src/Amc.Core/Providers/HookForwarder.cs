using System.Text.RegularExpressions;
using Amc.Core.Providers.Claude;

namespace Amc.Core.Providers;

/// <summary>
/// How hooks reach amcd on Windows, where neither curl's POSIX one-liner nor a POSIX shell can be assumed:
/// the tool runs <c>agentctl hook-forward</c>, which copies the hook's stdin to the daemon's private socket
/// and its reply to stdout (see Amc.Cli.Hooks.HookForward). This type only builds the invocations: an argument
/// vector for tools that spawn without a shell (Claude Code's exec form) and quoted command lines for tools
/// that run hooks through cmd.exe or PowerShell. Never carries the API token.
/// </summary>
/// <param name="Executable">Absolute path of agentctl.exe (or of the dotnet host when agentctl runs as a dll).</param>
/// <param name="LeadingArgs">Arguments before the subcommand: the agentctl.dll path under the dotnet host, else empty.</param>
public sealed record HookForwarder(string Executable, IReadOnlyList<string> LeadingArgs)
{
    /// <summary>The agentctl subcommand that forwards one hook.</summary>
    public const string Subcommand = "hook-forward";

    /// <summary>The forwarder for an agentctl apphost (agentctl.exe) at <paramref name="executable"/>.</summary>
    public HookForwarder(string executable) : this(executable, []) { }

    /// <summary>
    /// Argument vector after <see cref="Executable"/>. The trailing <c>--tag</c> carries
    /// <see cref="ClaudeHooksConfig.Marker"/> so install/uninstall recognise the entry; the forwarder ignores it.
    /// </summary>
    public IReadOnlyList<string> Args(string tool, string? evt, string socketPath, bool wait = false)
    {
        var args = new List<string>(LeadingArgs) { Subcommand, tool };
        if (evt is not null) args.AddRange(["--event", evt]);
        if (wait) args.Add("--wait");
        args.AddRange(["--socket", socketPath, "--tag", ClaudeHooksConfig.Marker]);
        return args;
    }

    /// <summary>A command line for cmd.exe (Codex runs <c>cmd /C</c>; Node's shell:true does too). Also valid in Git Bash.</summary>
    public string CmdLine(IReadOnlyList<string> args) =>
        string.Join(' ', new[] { CmdQuote(Executable, always: true) }.Concat(args.Select(a => CmdQuote(a))));

    /// <summary>A command line for PowerShell (Gemini CLI, Cursor, Copilot's "powershell" field): <c>&amp; 'exe' args</c>.</summary>
    public string PowerShellLine(IReadOnlyList<string> args) =>
        "& " + string.Join(' ', new[] { PowerShellQuote(Executable, always: true) }.Concat(args.Select(a => PowerShellQuote(a))));

    private static readonly Regex Plain = new(@"^[A-Za-z0-9_.:/\\-]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// cmd.exe quoting: plain tokens stay bare, others are wrapped in double quotes (inside which &amp; | &lt; &gt; ^ are literal).
    /// Windows paths cannot contain '"'; '%' would still be expanded by cmd and is rejected.
    /// </summary>
    public static string CmdQuote(string s, bool always = false)
    {
        if (s.Contains('"') || s.Contains('%')) throw new ArgumentException($"cannot quote for cmd.exe: {s}", nameof(s));
        // A backslash before the closing quote would escape it for the program's own argv parsing.
        if (s.EndsWith('\\')) throw new ArgumentException($"cannot end in a backslash: {s}", nameof(s));
        // Paths are always quoted, so the line also survives Git Bash (backslashes are kept inside double quotes there).
        return !always && Plain.IsMatch(s) && !s.Contains('\\') ? s : "\"" + s + "\"";
    }

    /// <summary>PowerShell single-quote literal (no expansion); every quote character PowerShell accepts, ‘ ’ ‚ ‛ included, is doubled.</summary>
    public static string PowerShellQuote(string s, bool always = false) =>
        !always && Plain.IsMatch(s) ? s : "'" + Regex.Replace(s, "['‘’‚‛]", m => m.Value + m.Value) + "'";
}
