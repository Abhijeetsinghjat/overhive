using System.Text.RegularExpressions;

namespace Amc.Daemon.Api;

/// <summary>
/// The optional "AllowedHosts" setting: exact extra Host names (e.g. <c>my-box.tailnet-1234.ts.net</c> for
/// <c>tailscale serve</c>) that the Host/Origin checks accept besides loopback. Wildcards are refused, because a
/// wildcard would reopen the DNS-rebinding hole the Host check closes. Accepts a JSON array or a ';'-separated
/// string (the shape ASP.NET's own setting of the same name uses, which Overhive replaces).
/// </summary>
public static partial class HostAllowList
{
    public const string Key = "AllowedHosts";

    /// <summary>Reads and validates the setting; throws with a clear message on an invalid entry.</summary>
    public static List<string> Resolve(IConfiguration config)
    {
        var section = config.GetSection(Key);
        var raw = section.GetChildren().Select(c => c.Value).ToList();
        if (raw.Count == 0 && section.Value is { } scalar) raw.AddRange(scalar.Split(';'));
        var result = new List<string>();
        foreach (var entry in raw.Select(e => e?.Trim()).Where(e => !string.IsNullOrEmpty(e)))
        {
            var error = Validate(entry!);
            if (error is not null) throw new InvalidOperationException($"Invalid {Key} entry '{entry}': {error}. See docs/REMOTE_ACCESS.md.");
            var host = entry!.ToLowerInvariant();
            if (!result.Contains(host)) result.Add(host);
        }
        return result;
    }

    /// <summary>Null when <paramref name="host"/> is an exact host name or IP address; otherwise why not.</summary>
    public static string? Validate(string host)
    {
        if (host.Contains('*')) return "wildcards are not allowed; list each exact host name";
        if (host.Contains(':') || host.Contains('/')) return "give the bare host name, without scheme, port or path";
        return HostName().IsMatch(host) ? null : "not a valid host name";
    }

    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();
}
