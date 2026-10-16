using System.Diagnostics;
using System.Text;
using Amc.Core.Attention;

namespace Amc.Daemon.Attention;

/// <summary>
/// Local desktop notifications: notify-send (Linux, libnotify) or osascript "display notification" (macOS).
/// No shell: the program and its arguments go through ArgumentList; the AppleScript literal is escaped.
/// Sent only while no dashboard is connected (the dashboard has its own browser notifications).
/// </summary>
public sealed class DesktopNotifier(NotificationOptions options, ILogger<DesktopNotifier> log) : INotifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private bool _missingLogged;

    public string Name => "desktop";
    public bool Enabled => options.Desktop && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
    public bool OnlyWithoutDashboard => true;

    public async Task<bool> SendAsync(NotificationMessage m, CancellationToken ct)
    {
        var (file, args) = Command(m, OperatingSystem.IsMacOS());
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(); } catch (InvalidOperationException) { /* already exited */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (!_missingLogged) log.LogWarning("Desktop notifications are on but {File} was not found (install libnotify-bin / notify-send)", file);
            _missingLogged = true;
            return false;
        }
    }

    /// <summary>The exact command line (pure, unit-tested). Text is never interpreted by a shell.</summary>
    public static (string File, IReadOnlyList<string> Args) Command(NotificationMessage m, bool macOS)
    {
        if (macOS)
            return ("osascript", ["-e", $"display notification \"{AppleScriptEscape(m.Body)}\" with title \"{AppleScriptEscape(m.Title)}\""]);
        return ("notify-send",
        [
            "--app-name=Overhive",
            "--urgency=" + (m.Priority == AttentionPriority.Urgent ? "critical" : "normal"),
            "--", m.Title, MarkupEscape(m.Body),
        ]);
    }

    /// <summary>Notification servers may render the body as simple markup; agent text must show literally.</summary>
    public static string MarkupEscape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Escapes text for an AppleScript string literal: backslash and quote escaped, control characters become spaces.</summary>
    public static string AppleScriptEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            if (ch is '\\' or '"') sb.Append('\\').Append(ch);
            else if (char.IsControl(ch)) sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
