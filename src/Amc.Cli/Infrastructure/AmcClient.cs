using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Core.Events;

namespace Amc.Cli.Infrastructure;

public sealed record ApiResponse(int Status, string Body, string? ContentType)
{
    public bool Success => Status is >= 200 and < 300;
    public bool IsJson => ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
    public JsonNode? Node => TryParse(Body);

    public static JsonNode? TryParse(string body)
    {
        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }

    /// <summary>The daemon's error text: {error} or {detail}, falling back to the raw body.</summary>
    public string ErrorText()
    {
        var n = Node as JsonObject;
        var text = n?["error"]?.ToString() ?? n?["detail"]?.ToString();
        if (text is { Length: > 0 }) return text;
        return Body.Length is > 0 and < 300 ? Body.Trim() : $"HTTP {Status}";
    }
}

/// <summary>
/// Thin HTTP client for the amcd REST API (the same API the UI uses). Authenticates with
/// the X-Amc-Token header; never sends an Origin header (the middleware rejects foreign origins).
/// </summary>
public sealed class AmcClient : IDisposable
{
    public const string TokenHeader = "X-Amc-Token";

    private readonly HttpClient _http;

    public Uri BaseUrl { get; }
    public string Token { get; }

    public AmcClient(Uri baseUrl, string token, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        BaseUrl = baseUrl;
        Token = token;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseUrl;
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Add(TokenHeader, token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("agentctl", "1.0"));
    }

    /// <summary>Sends a request. Never throws for HTTP status codes; throws <see cref="DaemonUnreachableException"/> when nobody answers.</summary>
    public async Task<ApiResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json.Options), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            return new ApiResponse((int)resp.StatusCode, text, resp.Content.Headers.ContentType?.MediaType);
        }
        catch (HttpRequestException ex)
        {
            var refused = ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused;
            throw new DaemonUnreachableException(refused
                ? $"daemon not reachable at {BaseUrl} (connection refused) — is amcd running?"
                : $"daemon not reachable at {BaseUrl}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DaemonUnreachableException($"daemon at {BaseUrl} did not respond within {_http.Timeout.TotalSeconds:0}s");
        }
    }

    /// <summary>GET returning the raw body; non-success statuses become <see cref="ApiException"/>.</summary>
    public async Task<ApiResponse> GetAsync(string path, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, path, null, ct);
        if (!r.Success) throw ToException(r, path);
        return r;
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        var r = await GetAsync(path, ct);
        try
        {
            return JsonSerializer.Deserialize<T>(r.Body, Json.Options) ?? throw new CliException($"empty response from {path}");
        }
        catch (JsonException ex)
        {
            throw new CliException($"unexpected response from {path}: {ex.Message}");
        }
    }

    public async Task<JsonNode> GetNodeAsync(string path, CancellationToken ct = default)
    {
        var r = await GetAsync(path, ct);
        return r.Node ?? throw new CliException($"unexpected non-JSON response from {path}");
    }

    public static ApiException ToException(ApiResponse r, string path) => r.Status switch
    {
        401 => new ApiException(401, "daemon rejected the token (401). The token file may be stale — was amcd started with a different data dir?"),
        404 => new ApiException(404, $"not found: {path.Split('?')[0]} ({r.ErrorText()})"),
        421 => new ApiException(421, "daemon rejected the Host header (421) — use http://127.0.0.1:<port>"),
        _ => new ApiException(r.Status, $"daemon returned {r.Status}: {r.ErrorText()}"),
    };

    /// <summary>Path segment escaping for ids such as <c>claude:abc/def</c>.</summary>
    public static string Seg(string id) => Uri.EscapeDataString(id);

    public static string Query(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => p.Value is { Length: > 0 }).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    public void Dispose() => _http.Dispose();
}
