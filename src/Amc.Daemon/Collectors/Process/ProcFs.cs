using System.Globalization;
using System.Text;
using Amc.Core.Security;

namespace Amc.Daemon.Collectors.Processes;

/// <summary>Parsed subset of /proc/&lt;pid&gt;/stat (field numbers per proc(5)).</summary>
public sealed record ProcStat(
    int Pid, string Comm, char State, int Ppid, int Pgrp, int Session, int TtyNr,
    long UTime, long STime, long StartTime)
{
    /// <summary>Field 22 (starttime, clock ticks since boot) as the registry's <c>procStart</c> string.</summary>
    public string ProcStart => StartTime.ToString(CultureInfo.InvariantCulture);
    public long CpuTicks => UTime + STime;
}

/// <summary>Raw /proc access, abstracted so the tracker can be tested without a live system.</summary>
public interface IProcSource
{
    IEnumerable<int> ListPids();
    string? ReadStat(int pid);
    byte[]? ReadCmdline(int pid);
    string? ReadStatus(int pid);
    /// <summary>readlink of /proc/&lt;pid&gt;/&lt;name&gt; ("cwd", "fd/0"). Null when not permitted or gone.</summary>
    string? ReadLink(int pid, string name);
    /// <summary>Only ever called for agent processes (see <see cref="ProcParsers.EnvSummary"/>).</summary>
    byte[]? ReadEnviron(int pid);
    /// <summary>
    /// Whether the process runs as the daemon's user, when the source can tell directly (Windows: token SID).
    /// Null means unknown: callers fall back to the Uid line of <see cref="ReadStatus"/>.
    /// </summary>
    bool? OwnedByCurrentUser(int pid) => null;
    long ClockTicksPerSecond { get; }
    DateTimeOffset BootTime { get; }
}

/// <summary>Reads the real procfs. Every read tolerates the process vanishing mid-scan.</summary>
public sealed class ProcFs(string root = "/proc") : IProcSource
{
    private DateTimeOffset? _boot;

    public IEnumerable<int> ListPids()
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root); }
        catch (IOException) { yield break; }
        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d.AsSpan());
            if (name.Length > 0 && char.IsAsciiDigit(name[0]) && int.TryParse(name, out var pid)) yield return pid;
        }
    }

    public string? ReadStat(int pid) => Try(() => File.ReadAllText(P(pid, "stat")));
    public byte[]? ReadCmdline(int pid) => Try(() => File.ReadAllBytes(P(pid, "cmdline")));
    public string? ReadStatus(int pid) => Try(() => File.ReadAllText(P(pid, "status")));
    public byte[]? ReadEnviron(int pid) => Try(() => File.ReadAllBytes(P(pid, "environ")));
    public string? ReadLink(int pid, string name) => Try(() => new FileInfo(P(pid, name)).LinkTarget);

    public long ClockTicksPerSecond { get; } = ProcNative.ClockTicks();

    public DateTimeOffset BootTime => _boot ??= ReadBootTime();

    private DateTimeOffset ReadBootTime()
    {
        var text = Try(() => File.ReadAllText(Path.Combine(root, "stat")));
        var btime = text?.Split('\n').FirstOrDefault(l => l.StartsWith("btime ", StringComparison.Ordinal));
        return btime is not null && long.TryParse(btime.AsSpan(6).Trim(), out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    private string P(int pid, string name) => Path.Combine(root, pid.ToString(CultureInfo.InvariantCulture), name);

    private static T? Try<T>(Func<T?> f) where T : class
    {
        try { return f(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

public static class ProcParsers
{
    public const int MaxCommandChars = 4096;
    public const int MaxEnvEntries = 200;

    /// <summary>
    /// Parses /proc/&lt;pid&gt;/stat. comm (field 2) may contain spaces and parentheses, so
    /// the remaining fields are split from the LAST ')'.
    /// </summary>
    public static ProcStat? ParseStat(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var open = content.IndexOf('(');
        var close = content.LastIndexOf(')');
        if (open <= 0 || close < open) return null;
        if (!int.TryParse(content.AsSpan(0, open).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
        var comm = content[(open + 1)..close];
        var f = content[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // f[i] is field (i + 3): state=3, ppid=4, pgrp=5, session=6, tty_nr=7, utime=14, stime=15, starttime=22
        if (f.Length < 20 || f[0].Length != 1) return null;
        try
        {
            return new ProcStat(pid, comm, f[0][0],
                I(f[1]), I(f[2]), I(f[3]), I(f[4]), L(f[11]), L(f[12]), L(f[19]));
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }

        static int I(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        static long L(string s) => long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>NUL-separated argv → single redacted, capped display string. Null for kernel threads/zombies.</summary>
    public static string? ParseCmdline(byte[]? raw)
    {
        if (raw is null || raw.Length == 0) return null;
        var parts = Encoding.UTF8.GetString(raw).Split('\0');
        var n = parts.Length;
        while (n > 0 && parts[n - 1].Length == 0) n--;
        if (n == 0) return null;
        var joined = string.Join(' ', parts.AsSpan(0, n).ToArray().Select(QuoteIfNeeded));
        joined = Redactor.Redact(joined); // before truncation, so a secret cut at the boundary can't leak its prefix
        return joined.Length > MaxCommandChars ? joined[..MaxCommandChars] + "…" : joined;
    }

    private static string QuoteIfNeeded(string a) =>
        a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? a : "'" + a.Replace("'", "'\\''") + "'";

    /// <summary>VmRSS from /proc/&lt;pid&gt;/status, in bytes.</summary>
    public static long ParseVmRssBytes(string? status)
    {
        if (status is null) return 0;
        foreach (var line in status.Split('\n'))
        {
            if (!line.StartsWith("VmRSS:", StringComparison.Ordinal)) continue;
            var parts = line[6..].Split(' ', '\t').Where(p => p.Length > 0).ToArray();
            if (parts.Length >= 1 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                return kb * 1024;
        }
        return 0;
    }

    /// <summary>
    /// Only "/dev/pts/N" and "/dev/ttyX" targets of fd/0 count as a terminal.
    /// </summary>
    public static string? ParseTty(string? fd0Target) =>
        fd0Target is not null && (fd0Target.StartsWith("/dev/pts/", StringComparison.Ordinal) || fd0Target.StartsWith("/dev/tty", StringComparison.Ordinal))
            ? fd0Target : null;

    /// <summary>
    /// Environment summary for agent processes. Values only for <see cref="Redactor.SafeEnvNames"/>
    /// (still passed through the redactor) and our own AMC_TERMINAL_ID; every other variable is
    /// listed by name only — values are never kept.
    /// </summary>
    public static IReadOnlyList<string> EnvSummary(byte[]? environ)
    {
        if (environ is null || environ.Length == 0) return [];
        var safe = new List<string>();
        var names = new List<string>();
        foreach (var entry in Encoding.UTF8.GetString(environ).Split('\0'))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var name = entry[..eq];
            if (Redactor.SafeEnvNames.Contains(name) || name == TerminalEnv.Variable)
                safe.Add(name + "=" + Redactor.RedactAndCap(entry[(eq + 1)..], 512));
            else names.Add(name);
        }
        safe.Sort(StringComparer.Ordinal);
        names.Sort(StringComparer.Ordinal);
        return [.. safe.Concat(names).Take(MaxEnvEntries)];
    }

    /// <summary>Value of AMC_TERMINAL_ID in an environ block, if present.</summary>
    public static string? TerminalIdFromEnv(IReadOnlyList<string> summary)
    {
        var prefix = TerminalEnv.Variable + "=";
        return summary.FirstOrDefault(s => s.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }
}

/// <summary>Environment variable `agentctl run` sets for its child.</summary>
public static class TerminalEnv
{
    public const string Variable = "AMC_TERMINAL_ID";
}
