using System.Text.RegularExpressions;

namespace Amc.Core.Security;

/// <summary>
/// Removes credentials from any text before it is persisted or displayed.
/// Applied at ingest, so secrets never reach SQLite, the WebSocket or the UI.
/// </summary>
public static partial class Redactor
{
    public const string Mask = "«redacted»";

    /// <summary>Environment variable names that are safe to display (values are still redacted if they look secret).</summary>
    public static readonly IReadOnlySet<string> SafeEnvNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "PATH", "HOME", "USER", "SHELL", "TERM", "LANG", "PWD", "EDITOR", "TZ", "COLORTERM", "LOGNAME",
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "DOTNET_ROOT", "NODE_ENV", "VIRTUAL_ENV",
    };

    private static readonly Regex[] Patterns =
    [
        PrivateKey(), Bearer(), BasicAuth(), AuthHeader(), CliSecretFlag(), CurlUser(), DbClientPassword(), KeyValueSecret(), UrlCredentials(),
        Anthropic(), OpenAi(), GitHub(), Slack(), Aws(), Google(), Jwt(), Npm(),
    ];

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var p in Patterns)
            text = p.Replace(text, m => m.Groups["keep"].Success ? m.Groups["keep"].Value + Mask : Mask);
        return text;
    }

    public static bool IsSensitiveName(string name) => SensitiveName().IsMatch(name);

    /// <summary>Redacts and caps a text field (keeps the tail, which is usually where errors and summaries are).</summary>
    public static string? RedactAndCap(string? text, int maxChars = 16 * 1024)
    {
        if (text is null) return null;
        var r = Redact(text);
        return r.Length <= maxChars ? r : "…[truncated " + (r.Length - maxChars) + " chars]…\n" + r[^maxChars..];
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKey();
    [GeneratedRegex(@"(?<keep>\b[Bb]earer\s+)[A-Za-z0-9\-._~+/]{12,}=*")]
    private static partial Regex Bearer();
    [GeneratedRegex(@"(?<keep>\b(?:Basic|Digest)\s+)[A-Za-z0-9+/=._~-]{8,}")]
    private static partial Regex BasicAuth();
    [GeneratedRegex(@"(?<keep>--(?i:password|passwd|pass|pwd|token|secret|api-key|apikey|access-token|client-secret)(?:=|\s+))[^\s""']{3,}")]
    private static partial Regex CliSecretFlag();
    [GeneratedRegex(@"(?<keep>\s(?:-u|--user)\s+[^\s:]+:)[^\s""']{3,}")]
    private static partial Regex CurlUser();
    [GeneratedRegex(@"(?<keep>\b(?:mysql|mysqldump|mariadb|mysqladmin)\b[^\n]*?\s-p)[^\s-][^\s]{2,}")]
    private static partial Regex DbClientPassword();
    [GeneratedRegex(@"(?<keep>(?i:authorization|x-api-key|api-key|x-amc-token|cookie)\s*[:=]\s*[""']?)[^\s""']{6,}")]
    private static partial Regex AuthHeader();
    [GeneratedRegex(@"(?<keep>\b[A-Za-z0-9_.-]*(?i:password|passwd|pass|pwd|secret|token|api_?key|access_?key|private_?key|client_?secret|connectionstring)[A-Za-z0-9_.-]*[""']?\s*[:=]\s*[""']?)[^\s""',;]{4,}")]
    private static partial Regex KeyValueSecret();
    [GeneratedRegex(@"(?<keep>\b[a-z][a-z0-9+.-]*://[^/\s:@]+:)[^@\s/]+(?=@)")]
    private static partial Regex UrlCredentials();
    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}")]
    private static partial Regex Anthropic();
    [GeneratedRegex(@"\bsk-(proj-)?[A-Za-z0-9_\-]{20,}")]
    private static partial Regex OpenAi();
    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})")]
    private static partial Regex GitHub();
    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}")]
    private static partial Regex Slack();
    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex Aws();
    [GeneratedRegex(@"\b(AIza[0-9A-Za-z_\-]{35}|ya29\.[0-9A-Za-z_\-]{20,})")]
    private static partial Regex Google();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")]
    private static partial Regex Jwt();
    [GeneratedRegex(@"\bnpm_[A-Za-z0-9]{36}\b")]
    private static partial Regex Npm();
    [GeneratedRegex(@"(?i)(pass|secret|token|key|auth|cred|cookie|session|private|signature)")]
    private static partial Regex SensitiveName();
}
