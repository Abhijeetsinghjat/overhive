using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Amc.Core.Events;

/// <summary>How the system knows a fact. Displayed next to every claim in the UI.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Evidence>))]
public enum Evidence
{
    /// <summary>Seen directly by the OS, Git or the filesystem.</summary>
    Observed,
    /// <summary>Stated by the agent (hook payload, transcript, tool input).</summary>
    Reported,
    /// <summary>Derived by correlation; may be wrong.</summary>
    Inferred,
}

/// <summary>Which collector produced an event.</summary>
public static class EventSources
{
    public const string Hook = "hook";
    public const string Transcript = "transcript";
    public const string Registry = "registry";
    public const string Process = "process";
    public const string Git = "git";
    public const string Pty = "pty";
    public const string User = "user";
    public const string System = "system";
    public const string Simulator = "simulator";
}

/// <summary>
/// The normalized, immutable event envelope. The append-only log of these is the
/// single source of truth; all state is a fold over it (see <see cref="State.Reducer"/>).
/// </summary>
public sealed record AgentEvent
{
    /// <summary>Global monotonic sequence assigned on persistence (0 before).</summary>
    public long Seq { get; init; }
    public string Id { get; init; } = Ulid.NewId();
    public DateTimeOffset Ts { get; init; } = DateTimeOffset.UtcNow;
    public required string Type { get; init; }
    public required string Source { get; init; }
    public Evidence Evidence { get; init; } = Evidence.Observed;
    public string? AgentId { get; init; }
    public string? SessionId { get; init; }
    public string? RepositoryId { get; init; }
    public string? WorktreeId { get; init; }
    /// <summary>Path relative to the worktree root when <see cref="WorktreeId"/> is set, otherwise absolute.</summary>
    public string? FilePath { get; init; }
    /// <summary>Unique key so the same fact reported by two collectors is stored once.</summary>
    public string? DedupeKey { get; init; }
    public JsonObject Data { get; init; } = new();

    public T? Payload<T>() where T : class => Data.Deserialize<T>(Json.Options);

    public static AgentEvent Create<T>(string type, string source, T payload, Evidence evidence = Evidence.Observed) where T : class
        => new() { Type = type, Source = source, Evidence = evidence, Data = Json.ToObject(payload) };

    public AgentEvent WithPayload<T>(T payload) where T : class => this with { Data = Json.ToObject(payload) };
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static JsonObject ToObject<T>(T value) =>
        JsonSerializer.SerializeToNode(value, Options) as JsonObject ?? new JsonObject();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>Lexicographically sortable unique id (time-ordered), no external dependency.</summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewId()
    {
        Span<char> chars = stackalloc char[26];
        var ms = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 9; i >= 0; i--) { chars[i] = Alphabet[(int)(ms & 31)]; ms >>= 5; }
        Span<byte> rnd = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(rnd);
        for (var i = 10; i < 26; i++) chars[i] = Alphabet[rnd[i - 10] & 31];
        return new string(chars);
    }
}
