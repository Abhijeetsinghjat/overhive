using System.Text.Json;
using Amc.Core.Attention;
using Amc.Core.Events;

namespace Amc.Daemon.Attention;

/// <summary>
/// The user's dismiss / snooze marks, kept in the daemon (not in the browser) so they apply everywhere at once:
/// the desktop dashboard, the phone view, the CLI and the daemon's own notifications. Marks are per item id (one
/// occurrence), are pruned once their item is gone, and survive restarts in a small 0600 JSON file in the data dir.
/// </summary>
public sealed class AttentionInbox
{
    public static readonly TimeSpan MaxSnooze = TimeSpan.FromDays(7);
    private readonly string? _path;
    private readonly ILogger? _log;
    private readonly object _gate = new();
    private Dictionary<string, Mark> _marks = [];

    public sealed record Mark(bool Dismissed, DateTimeOffset? Until, DateTimeOffset At);

    public AttentionInbox(string? path = null, ILogger? log = null)
    {
        _path = path;
        _log = log;
        if (path is null || !File.Exists(path)) return;
        try { _marks = JsonSerializer.Deserialize<Dictionary<string, Mark>>(File.ReadAllText(path), Json.Options) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.LogWarning("Ignoring unreadable {Path}: {Message}", path, ex.Message);
        }
    }

    public void Dismiss(string id, DateTimeOffset now) => Set(id, new Mark(true, null, now));

    public void Snooze(string id, TimeSpan forTime, DateTimeOffset now)
    {
        if (forTime <= TimeSpan.Zero) forTime = TimeSpan.FromMinutes(1);
        if (forTime > MaxSnooze) forTime = MaxSnooze;
        Set(id, new Mark(false, now + forTime, now));
    }

    /// <summary>Removes the user's mark (undo).</summary>
    public void Restore(string id)
    {
        lock (_gate) { if (_marks.Remove(id)) Save(); }
    }

    /// <summary>
    /// Splits derived items into visible and hidden (annotated with the mark). Prunes marks of items that are
    /// gone and snoozes that ended.
    /// </summary>
    public (List<AttentionItem> Visible, List<AttentionItem> Hidden) Apply(IReadOnlyList<AttentionItem> items, DateTimeOffset now)
    {
        var visible = new List<AttentionItem>(items.Count);
        var hidden = new List<AttentionItem>();
        lock (_gate)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in items)
            {
                present.Add(i.Id);
                if (_marks.TryGetValue(i.Id, out var m) && (m.Dismissed || m.Until > now))
                    hidden.Add(i with { Dismissed = m.Dismissed ? true : null, SnoozedUntil = m.Until });
                else visible.Add(i);
            }
            var stale = _marks.Where(kv => !present.Contains(kv.Key) || (!kv.Value.Dismissed && kv.Value.Until <= now)).Select(kv => kv.Key).ToList();
            foreach (var k in stale) _marks.Remove(k);
            if (stale.Count > 0) Save();
        }
        return (visible, hidden);
    }

    private void Set(string id, Mark m)
    {
        lock (_gate) { _marks[id] = m; Save(); }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_marks, Json.Options)); // daemon umask 077 → 0600
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning("Could not save attention marks: {Message}", ex.Message);
        }
    }
}
