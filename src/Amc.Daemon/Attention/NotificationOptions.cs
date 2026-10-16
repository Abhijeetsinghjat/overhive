using Amc.Core.Attention;

namespace Amc.Daemon.Attention;

/// <summary>
/// Notifications for new attention items (config section "Notifications"). Everything is off by default.
/// Browser notifications are opt-in in the dashboard itself and need no configuration here.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>Desktop notifications from the daemon (notify-send on Linux, osascript on macOS) while no dashboard is open.</summary>
    public bool Desktop { get; set; }
    /// <summary>
    /// Full topic URL on the user's OWN ntfy server, e.g. https://ntfy.example.org/overhive-7f3k. Empty = off.
    /// This is the only outbound network call Overhive makes, and only when set.
    /// </summary>
    public string NtfyUrl { get; set; } = "";
    /// <summary>Optional file holding an ntfy access token (sent as a Bearer token). Keep it 0600.</summary>
    public string NtfyTokenFile { get; set; } = "";
    /// <summary>Lowest priority that notifies: "urgent" (default) or "normal" (also sessions waiting for their next prompt).</summary>
    public string MinPriority { get; set; } = "urgent";
    /// <summary>Items that appear within this many seconds are sent as one notification.</summary>
    public int CoalesceSeconds { get; set; } = 5;
    /// <summary>At most one notification per channel in this many seconds; later items wait and are coalesced.</summary>
    public int MinIntervalSeconds { get; set; } = 60;
    /// <summary>Hard cap per channel per hour.</summary>
    public int MaxPerHour { get; set; } = 20;

    public AttentionPriority EffectiveMinPriority =>
        Enum.TryParse<AttentionPriority>(MinPriority, ignoreCase: true, out var p) && p != AttentionPriority.Low ? p : AttentionPriority.Urgent;
}
