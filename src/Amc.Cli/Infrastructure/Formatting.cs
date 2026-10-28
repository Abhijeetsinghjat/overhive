using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Amc.Core.Domain;
using Amc.Core.Events;

namespace Amc.Cli.Infrastructure;

/// <summary>ANSI styling that collapses to plain text when colour is off.</summary>
public readonly partial struct Style(bool enabled)
{
    public bool Enabled => enabled;
    public string Red(string s) => Wrap("31", s);
    public string Green(string s) => Wrap("32", s);
    public string Yellow(string s) => Wrap("33", s);
    public string Blue(string s) => Wrap("34", s);
    public string Magenta(string s) => Wrap("35", s);
    public string Cyan(string s) => Wrap("36", s);
    public string Dim(string s) => Wrap("2", s);
    public string Bold(string s) => Wrap("1", s);

    public string Wrap(string code, string s) => enabled && s.Length > 0 ? $"\u001b[{code}m{s}\u001b[0m" : s;

    public string Ok(bool ok) => ok ? Green("✓") : Red("✗");

    public string Status(AgentStatus st, string text) => st switch
    {
        AgentStatus.Running or AgentStatus.Starting => Green(text),
        AgentStatus.Waiting => Cyan(text),
        AgentStatus.Blocked => Yellow(text),
        AgentStatus.Paused => Magenta(text),
        AgentStatus.Failed or AgentStatus.Disconnected => Red(text),
        AgentStatus.Completed or AgentStatus.Cancelled or AgentStatus.Unknown => Dim(text),
        _ => text,
    };

    public string Health(string? state, string text) => state switch
    {
        "ok" => Green(text),
        "degraded" or "starting" => Yellow(text),
        "failed" => Red(text),
        "disabled" => Dim(text),
        _ => text,
    };

    public static int VisibleLength(string s) => Ansi().Replace(s, "").Length;

    [GeneratedRegex("\u001b\\[[0-9;]*m")]
    private static partial Regex Ansi();
}

public static class Fmt
{
    public static string Age(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalSeconds < 60 ? $"{(int)t.TotalSeconds}s"
            : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m"
            : t.TotalHours < 48 ? $"{(int)t.TotalHours}h"
            : $"{(int)t.TotalDays}d";
    }

    public static string Age(DateTimeOffset now, DateTimeOffset? at) => at is { } a && a > DateTimeOffset.MinValue.AddDays(1) ? Age(now - a) : "-";

    public static string Trunc(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = OneLine(s);
        return s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";
    }

    public static string OneLine(string s) => s.ReplaceLineEndings(" ⏎ ").Replace('\t', ' ');

    public static string Status(AgentStatus s) => JsonName(s);
    public static string Evidence(Evidence e) => e switch
    {
        Core.Events.Evidence.Observed => "obs",
        Core.Events.Evidence.Reported => "rep",
        Core.Events.Evidence.Inferred => "inf",
        _ => e.ToString().ToLowerInvariant(),
    };

    /// <summary>The wire name of an enum value (snake_case), as the API and UI show it.</summary>
    public static string JsonName<T>(T value) where T : struct, Enum => Json.Serialize(value).Trim('"');

    public static string HomeRelative(string? path, string home)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (home.Length <= 1 || !PathText.IsAbsolute(path) || PathText.Relative(home, path) is not { } rel) return path;
        return rel.Length == 0 ? "~" : "~/" + rel;
    }

    /// <summary>Parses "10m", "2h", "30s", "1d", "1w" or an absolute timestamp into an instant.</summary>
    public static DateTimeOffset ParseSince(string text, DateTimeOffset now)
    {
        var m = Regex.Match(text.Trim(), @"^(\d+(?:\.\d+)?)\s*(s|m|h|d|w)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var span = char.ToLowerInvariant(m.Groups[2].Value[0]) switch
            {
                's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n), _ => TimeSpan.FromDays(7 * n),
            };
            return now - span;
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)) return at;
        throw new UsageException($"cannot parse time '{text}' (use e.g. 30s, 10m, 2h, 1d or an ISO timestamp)");
    }
}

/// <summary>Column-aligned text table. Colour codes are ignored when measuring widths.</summary>
public sealed class TextTable(params string[] headers)
{
    private readonly List<string[]> _rows = [];
    public int Count => _rows.Count;

    public void Add(params string[] cells) => _rows.Add(cells);

    public void Write(TextWriter w, Style style, bool header = true)
    {
        var cols = headers.Length;
        var widths = new int[cols];
        for (var c = 0; c < cols; c++)
            widths[c] = Math.Max(header ? headers[c].Length : 0, _rows.Count == 0 ? 0 : _rows.Max(r => c < r.Length ? Style.VisibleLength(r[c]) : 0));

        if (header) w.WriteLine(Line(headers.Select(h => style.Dim(h)).ToArray(), widths));
        foreach (var r in _rows) w.WriteLine(Line(r, widths));
    }

    private static string Line(string[] cells, int[] widths)
    {
        var sb = new StringBuilder();
        for (var c = 0; c < widths.Length; c++)
        {
            var cell = c < cells.Length ? cells[c] : "";
            sb.Append(cell);
            if (c < widths.Length - 1) sb.Append(' ', widths[c] - Style.VisibleLength(cell) + 2);
        }
        return sb.ToString().TrimEnd();
    }
}
