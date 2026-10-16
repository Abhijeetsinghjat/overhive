using Amc.Core.Security;
using Amc.Core.State;

namespace Amc.Core.Attention;

/// <summary>Time windows that keep the queue about the present, not about last week.</summary>
public sealed record AttentionSettings
{
    /// <summary>Failed / disconnected agents are listed for this long after they ended.</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Finished sessions are listed (low priority) for this long; zero turns them off.</summary>
    public TimeSpan FinishedWindow { get; init; } = TimeSpan.FromHours(1);

    public static readonly AttentionSettings Default = new();
}

public sealed record AttentionContext(DateTimeOffset Now, AttentionSettings Settings);

/// <summary>
/// Extension point: anything that can tell the user "look at this". A provider reads the projected state
/// (inside the state read lock: copy, never keep references) or its own data and yields items. Providers must
/// be cheap and side-effect free; they run after every ingest batch.
/// <para>To add one: implement this interface and register it in the daemon with
/// <c>services.AddSingleton&lt;IAttentionProvider, MyProvider&gt;()</c>. Ordering, de-duplication by id,
/// redaction, capping, dismiss/snooze, the UI and notifications are handled for every provider.</para>
/// </summary>
public interface IAttentionProvider
{
    string Name { get; }
    IEnumerable<AttentionItem> Collect(WorldState state, AttentionContext ctx);
}

/// <summary>Pure derivation of the attention queue: collect from providers, sanitize, de-duplicate, order.</summary>
public static class AttentionDeriver
{
    public const int MaxTitle = 140;
    public const int MaxDetail = 400;

    public static IReadOnlyList<IAttentionProvider> BuiltIn { get; } = [
        new AgentAttentionProvider(), new ConflictAttentionProvider(),
        new GuardAttentionProvider(), new RiskAttentionProvider(), new VerificationAttentionProvider(),
    ];

    public static List<AttentionItem> Derive(WorldState state, DateTimeOffset now, IEnumerable<IAttentionProvider>? providers = null,
        AttentionSettings? settings = null)
    {
        var ctx = new AttentionContext(now, settings ?? AttentionSettings.Default);
        var byId = new Dictionary<string, AttentionItem>(StringComparer.Ordinal);
        foreach (var p in providers ?? BuiltIn)
            foreach (var item in p.Collect(state, ctx))
                byId.TryAdd(item.Id, Sanitize(item, p.Name)); // first provider wins on an id clash
        var list = byId.Values.ToList();
        list.Sort(Compare);
        return list;
    }

    /// <summary>Priority first (urgent → low), then the oldest first (it has waited longest), then id for determinism.</summary>
    public static int Compare(AttentionItem a, AttentionItem b)
    {
        var c = a.Priority.CompareTo(b.Priority);
        if (c != 0) return c;
        c = a.Since.CompareTo(b.Since);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    }

    /// <summary>Agent text reaches titles and details, and from there notifications: redact and cap once, centrally.</summary>
    public static AttentionItem Sanitize(AttentionItem i, string source) => i with
    {
        Title = Cap(OneLine(Redactor.Redact(i.Title)), MaxTitle),
        Detail = string.IsNullOrWhiteSpace(i.Detail) ? null : Cap(Redactor.Redact(i.Detail).Trim(), MaxDetail),
        Source = string.IsNullOrEmpty(i.Source) ? source : i.Source,
    };

    private static readonly char[] LineBreaks = ['\r', '\n', '\t'];

    public static string OneLine(string s) => string.Join(' ', s.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries)).Trim();

    public static string Cap(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
