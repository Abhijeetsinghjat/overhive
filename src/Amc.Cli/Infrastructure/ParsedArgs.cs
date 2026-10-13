namespace Amc.Cli.Infrastructure;

/// <summary>Result of parsing argv against a <see cref="CommandSpec"/>.</summary>
public sealed class ParsedArgs
{
    public string Command { get; set; } = "";
    public List<string> Positionals { get; } = [];
    public Dictionary<string, List<string>> Options { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
    /// <summary>Everything after a literal <c>--</c>.</summary>
    public List<string> Rest { get; } = [];
    public bool HasRest { get; set; }

    public bool Flag(string name) => Flags.Contains(name);
    public string? Opt(string name) => Options.TryGetValue(name, out var v) ? v[^1] : null;
    public bool Json => Flag("json");

    public int? IntOpt(string name)
    {
        var v = Opt(name);
        if (v is null) return null;
        return int.TryParse(v, out var i) && i > 0 ? i : throw new UsageException($"--{name} expects a positive integer, got '{v}'");
    }
}

/// <summary>
/// Minimal, dependency-free argv parser. Long options (<c>--name value</c>, <c>--name=value</c>),
/// a few short aliases, flags, positionals and a <c>--</c> terminator. Unknown options are usage errors.
/// </summary>
public static class ArgParser
{
    public static readonly IReadOnlySet<string> GlobalFlags = new HashSet<string> { "json", "no-color", "help" };
    public static readonly IReadOnlySet<string> GlobalValues = new HashSet<string> { "url" };

    private static readonly Dictionary<string, string> ShortAliases = new()
    {
        ["-h"] = "help", ["-f"] = "follow", ["-y"] = "yes", ["-n"] = "limit", ["-a"] = "all", ["-t"] = "type",
    };

    public static ParsedArgs Parse(IReadOnlyList<string> argv, Func<string, CommandSpec?> lookup)
    {
        var result = new ParsedArgs();
        CommandSpec? spec = null;
        for (var i = 0; i < argv.Count; i++)
        {
            var tok = argv[i];
            if (tok == "--")
            {
                result.HasRest = true;
                result.Rest.AddRange(argv.Skip(i + 1));
                break;
            }
            if (tok.StartsWith('-') && tok.Length > 1 && !IsNumber(tok))
            {
                string name;
                string? inlineValue = null;
                if (tok.StartsWith("--"))
                {
                    name = tok[2..];
                    var eq = name.IndexOf('=');
                    if (eq >= 0) { inlineValue = name[(eq + 1)..]; name = name[..eq]; }
                }
                else if (ShortAliases.TryGetValue(tok, out var alias)) name = alias;
                else throw new UsageException($"unknown option '{tok}'");

                var takesValue = GlobalValues.Contains(name) || spec?.ValueOptions.Contains(name) == true;
                var isFlag = GlobalFlags.Contains(name) || spec?.FlagOptions.Contains(name) == true;
                if (takesValue)
                {
                    var value = inlineValue ?? (i + 1 < argv.Count ? argv[++i] : throw new UsageException($"option --{name} requires a value"));
                    if (!result.Options.TryGetValue(name, out var list)) result.Options[name] = list = [];
                    list.Add(value);
                }
                else if (isFlag)
                {
                    if (inlineValue is not null) throw new UsageException($"option --{name} does not take a value");
                    result.Flags.Add(name);
                }
                else throw new UsageException(spec is null ? $"unknown option '{tok}'" : $"unknown option '{tok}' for '{spec.Name}'");
                continue;
            }

            if (spec is null)
            {
                spec = lookup(tok) ?? throw new UsageException($"unknown command '{tok}'");
                result.Command = spec.Name;
            }
            else result.Positionals.Add(tok);
        }

        if (spec is not null && !result.Flag("help"))
        {
            if (result.HasRest && !spec.AcceptsRest) throw new UsageException($"'{spec.Name}' does not take arguments after --");
            if (result.Positionals.Count < spec.MinArgs) throw new UsageException($"missing argument for '{spec.Name}'. Usage: agentctl {spec.Usage}");
            if (result.Positionals.Count > spec.MaxArgs) throw new UsageException($"too many arguments for '{spec.Name}'. Usage: agentctl {spec.Usage}");
        }
        return result;
    }

    private static bool IsNumber(string s) => double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out _);
}

public delegate Task<int> CommandHandler(CliContext ctx, ParsedArgs args, CancellationToken ct);

public sealed record CommandSpec(
    string Name,
    string Usage,
    string Summary,
    CommandHandler Handler,
    int MinArgs = 0,
    int MaxArgs = 0,
    string[]? Flags = null,
    string[]? Values = null,
    bool AcceptsRest = false)
{
    public IReadOnlySet<string> FlagOptions { get; } = new HashSet<string>(Flags ?? []);
    public IReadOnlySet<string> ValueOptions { get; } = new HashSet<string>(Values ?? []);
}
