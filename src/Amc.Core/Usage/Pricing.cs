using System.Text.RegularExpressions;

namespace Amc.Core.Usage;

/// <summary>
/// Prices of one model in currency units per million tokens. A missing cache price falls back to the input price
/// (a provider that does not bill cache writes or reads separately), a missing 1-hour write price to the 5-minute one.
/// </summary>
public sealed class ModelPrice
{
    public double Input { get; set; }
    public double Output { get; set; }
    /// <summary>Cache write with the default (5-minute) lifetime.</summary>
    public double? CacheWrite { get; set; }
    /// <summary>Cache write with the 1-hour lifetime.</summary>
    public double? CacheWrite1h { get; set; }
    public double? CacheRead { get; set; }
    /// <summary>Prompts (input + cache write + cache read) above this many tokens pay <see cref="LongContext"/> prices.</summary>
    public long? LongContextAbove { get; set; }
    public ModelPrice? LongContext { get; set; }
    /// <summary>Multiplier for responses served in fast mode (usage "speed": "fast"); null: no fast price, standard applies.</summary>
    public double? FastMultiplier { get; set; }
    /// <summary>Multiplier for US-only inference (usage "inference_geo": "us"); null: none.</summary>
    public double? UsOnlyMultiplier { get; set; }

    public double CacheWriteOr => CacheWrite ?? Input;
    public double CacheWrite1hOr => CacheWrite1h ?? CacheWriteOr;
    public double CacheReadOr => CacheRead ?? Input;
}

/// <summary>Config section "Pricing" (docs/USAGE.md). On by default; the built-in table covers Anthropic's models.</summary>
public sealed class PricingSettings
{
    /// <summary>Show estimated API cost in the dashboard and CLI.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Label only; the built-in prices are USD, so change it only together with your own prices.</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>
    /// Added to or replacing built-in entries (whole entry per key). A key is a model id ("gpt-5-codex"), optionally
    /// ending in '*' to match every id with that prefix ("gemini-2.5-pro*").
    /// </summary>
    public Dictionary<string, ModelPrice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One model response, as the usage ledger reads it from the event log (values are the response's maximum).</summary>
public sealed record UsageResponse(
    string? AgentId, string? Model, DateTimeOffset At, long Input, long Output, long CacheWrite, long CacheRead,
    long? CacheWrite1h = null, string? Speed = null, string? Geo = null)
{
    public long Total => Input + Output + CacheWrite + CacheRead;
}

/// <summary>Model id → price, with id normalization and longest-prefix wildcards. Immutable after construction.</summary>
public sealed partial class PriceTable
{
    /// <summary>When the built-in prices were taken from <see cref="Source"/>.</summary>
    public const string BuiltInAsOf = "2026-11-11";
    public const string Source = "https://platform.claude.com/docs/en/about-claude/pricing";

    private readonly Dictionary<string, ModelPrice> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefix, ModelPrice Price)> _prefixes = [];
    private readonly Dictionary<string, ModelPrice?> _cache = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public string Currency { get; }
    public IReadOnlyDictionary<string, ModelPrice> Entries { get; }
    public IReadOnlySet<string> Overridden { get; }

    public PriceTable(PricingSettings? settings = null)
    {
        settings ??= new PricingSettings();
        Currency = string.IsNullOrWhiteSpace(settings.Currency) ? "USD" : settings.Currency.Trim();
        var all = new Dictionary<string, ModelPrice>(BuiltIn(), StringComparer.OrdinalIgnoreCase);
        var overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in settings.Models)
        {
            if (string.IsNullOrWhiteSpace(k) || v is null) continue;
            var key = k.Trim();
            all[key] = v;
            overridden.Add(key);
        }
        foreach (var (k, v) in all)
        {
            if (k.EndsWith('*')) _prefixes.Add((k[..^1], v));
            else _exact[k] = v;
        }
        _prefixes.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
        Entries = all;
        Overridden = overridden;
    }

    /// <summary>The price for a model id as reported (dated, Bedrock or Vertex spellings included), or null when unknown.</summary>
    public ModelPrice? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        lock (_lock)
        {
            if (_cache.TryGetValue(model, out var hit)) return hit;
            var found = Lookup(model.Trim()) ?? Lookup(Normalize(model));
            if (_cache.Count < 4096) _cache[model] = found;
            return found;
        }
    }

    private ModelPrice? Lookup(string id)
    {
        if (_exact.TryGetValue(id, out var p)) return p;
        foreach (var (prefix, price) in _prefixes)
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return price;
        return null;
    }

    [GeneratedRegex(@"-(\d{8})$")]
    private static partial Regex DateSuffix();

    [GeneratedRegex(@"-v\d+(:\d+)?$")]
    private static partial Regex BedrockVersion();

    /// <summary>
    /// "claude-haiku-4-5-20251001" → "claude-haiku-4-5"; "us.anthropic.claude-opus-4-8-v1:0" → "claude-opus-4-8";
    /// "claude-opus-4-5@20251101" → "claude-opus-4-5"; "claude-opus-5-5[1m]" → "claude-opus-5-5".
    /// </summary>
    public static string Normalize(string model)
    {
        var m = model.Trim();
        var bracket = m.IndexOf('[');
        if (bracket > 0) m = m[..bracket];
        var at = m.IndexOf('@');
        if (at > 0) m = m[..at];
        var anthropic = m.IndexOf("anthropic.", StringComparison.OrdinalIgnoreCase);
        if (anthropic >= 0) m = m[(anthropic + "anthropic.".Length)..];
        m = BedrockVersion().Replace(m, "");
        m = DateSuffix().Replace(m, "");
        return m;
    }

    /// <summary>Estimated cost of one response at list prices, or null when the model has no price.</summary>
    public double? Cost(UsageResponse r)
    {
        if (Find(r.Model) is not { } p) return null;
        var prompt = r.Input + r.CacheWrite + r.CacheRead;
        if (p.LongContextAbove is { } above && p.LongContext is { } lc && prompt > above) p = lc.WithModifiersOf(p);
        var cw1h = Math.Clamp(r.CacheWrite1h ?? 0, 0, r.CacheWrite);
        var cw5m = r.CacheWrite - cw1h;
        var cost = (r.Input * p.Input + r.Output * p.Output + cw5m * p.CacheWriteOr + cw1h * p.CacheWrite1hOr + r.CacheRead * p.CacheReadOr) / 1_000_000d;
        if (string.Equals(r.Speed, "fast", StringComparison.OrdinalIgnoreCase) && p.FastMultiplier is { } fast) cost *= fast;
        if (string.Equals(r.Geo, "us", StringComparison.OrdinalIgnoreCase) && p.UsOnlyMultiplier is { } us) cost *= us;
        return cost;
    }

    /// <summary>
    /// Anthropic first-party API list prices (USD per million tokens), taken from <see cref="Source"/> on
    /// <see cref="BuiltInAsOf"/>. Other vendors' prices change often and are not shipped: add them in config.
    /// </summary>
    public static IReadOnlyDictionary<string, ModelPrice> BuiltIn()
    {
        // input, output, 5m write, 1h write, cache read; fast/US-only multipliers where the page lists them.
        static ModelPrice P(double i, double o, double w5, double w1, double r, double? fast = null, double? us = null) =>
            new() { Input = i, Output = o, CacheWrite = w5, CacheWrite1h = w1, CacheRead = r, FastMultiplier = fast, UsOnlyMultiplier = us };
        const double Us = 1.1; // US-only inference: Claude 4.6 and later
        var haiku55 = P(0.10, 0.50, 0.125, 0.20, 0.01, us: Us);
        haiku55.LongContextAbove = 100_000;
        haiku55.LongContext = P(0.50, 2.50, 0.625, 1.00, 0.05);
        return new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-fable-5-2"] = P(8, 40, 10, 16, 0.20, fast: 2, us: Us),
            ["claude-mythos-5-2"] = P(8, 40, 10, 16, 0.20, fast: 2, us: Us),
            ["claude-fable-5-1"] = P(10, 50, 12.50, 20, 0.25, us: Us),
            ["claude-mythos-5-1"] = P(10, 50, 12.50, 20, 0.25, us: Us),
            ["claude-fable-5"] = P(10, 50, 12.50, 20, 1, us: Us),
            ["claude-mythos-5"] = P(10, 50, 12.50, 20, 1, us: Us),
            ["claude-opus-5-5"] = P(4, 20, 5, 8, 0.20, fast: 2, us: Us),
            ["claude-opus-5"] = P(5, 25, 6.25, 10, 0.50, fast: 2, us: Us),
            ["claude-opus-4-8"] = P(5, 25, 6.25, 10, 0.50, fast: 2, us: Us),
            ["claude-opus-4-7"] = P(5, 25, 6.25, 10, 0.50, us: Us),
            ["claude-opus-4-6"] = P(5, 25, 6.25, 10, 0.50, us: Us),
            ["claude-opus-4-5"] = P(5, 25, 6.25, 10, 0.50),
            ["claude-opus-4-1"] = P(15, 75, 18.75, 30, 1.50),
            ["claude-opus-4"] = P(15, 75, 18.75, 30, 1.50),
            ["claude-sonnet-5-5"] = P(2, 10, 2.50, 4, 0.10, us: Us),
            ["claude-sonnet-5"] = P(2, 10, 2.50, 4, 0.20, us: Us),
            ["claude-sonnet-4-6"] = P(3, 15, 3.75, 6, 0.30, us: Us),
            ["claude-sonnet-4-5"] = P(3, 15, 3.75, 6, 0.30),
            ["claude-sonnet-4"] = P(3, 15, 3.75, 6, 0.30),
            ["claude-haiku-5-5"] = haiku55,
            ["claude-haiku-4-5"] = P(1, 5, 1.25, 2, 0.10),
        };
    }
}

internal static class ModelPriceExtensions
{
    /// <summary>A long-context tier inherits the fast and US-only multipliers of its base entry unless it sets its own.</summary>
    public static ModelPrice WithModifiersOf(this ModelPrice tier, ModelPrice baseEntry) => new()
    {
        Input = tier.Input, Output = tier.Output, CacheWrite = tier.CacheWrite, CacheWrite1h = tier.CacheWrite1h, CacheRead = tier.CacheRead,
        FastMultiplier = tier.FastMultiplier ?? baseEntry.FastMultiplier,
        UsOnlyMultiplier = tier.UsOnlyMultiplier ?? baseEntry.UsOnlyMultiplier,
    };
}
