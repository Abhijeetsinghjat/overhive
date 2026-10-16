using System.Net.Http.Headers;
using System.Text;
using Amc.Core.Attention;
using Amc.Core.Security;

namespace Amc.Daemon.Attention;

/// <summary>
/// Optional push to the user's OWN ntfy server/topic (Notifications.NtfyUrl). This is the only outbound
/// network call in Overhive and happens only when configured. One short plain-text POST per (coalesced)
/// notification, 5 s timeout, no retries, no redirects; after repeated failures the channel pauses.
/// </summary>
public sealed class NtfyNotifier : INotifier
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PauseAfterFailures = TimeSpan.FromMinutes(10);
    public const int FailuresBeforePause = 3;

    private readonly NotificationOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly Uri? _url;
    private int _failures;
    private DateTimeOffset _pausedUntil;

    public NtfyNotifier(NotificationOptions options, ILogger<NtfyNotifier> log, TimeProvider clock, HttpMessageHandler? handler = null)
    {
        _options = options;
        _log = log;
        _clock = clock;
        // No redirects: a misconfigured or hostile server must not bounce the message somewhere else.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout };
        if (string.IsNullOrWhiteSpace(options.NtfyUrl)) return;
        if (TryParseUrl(options.NtfyUrl, out var url, out var error)) _url = url;
        else log.LogWarning("Notifications.NtfyUrl ignored: {Error}", error);
    }

    public string Name => "ntfy";
    public bool Enabled => _url is not null;
    public bool OnlyWithoutDashboard => false; // for the phone: useful exactly while the desktop dashboard is open but you are away

    /// <summary>http(s) only, a topic path, and no credentials in the URL (use NtfyTokenFile).</summary>
    public static bool TryParseUrl(string raw, out Uri? url, out string? error)
    {
        url = null;
        error = null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http"))
            error = "must be an absolute http(s) URL of a topic, e.g. https://ntfy.example.org/my-topic";
        else if (!string.IsNullOrEmpty(u.UserInfo)) error = "must not contain credentials; put an access token in NtfyTokenFile";
        else if (u.AbsolutePath.Trim('/').Length == 0) error = "must include the topic path, e.g. https://ntfy.example.org/my-topic";
        else url = u;
        return url is not null;
    }

    /// <summary>The request exactly as sent (pure apart from reading the token file; unit-tested).</summary>
    public HttpRequestMessage BuildRequest(NotificationMessage m)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            // Body: titles only, redacted (again) right before leaving the machine. Header values stay ASCII.
            Content = new StringContent(Redactor.Redact(m.Title + "\n" + m.Body), Encoding.UTF8, "text/plain"),
        };
        req.Headers.Add("Title", "Overhive");
        req.Headers.Add("Priority", m.Priority == AttentionPriority.Urgent ? "high" : "default");
        req.Headers.Add("Tags", "bell");
        if (ReadToken() is { } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    public async Task<bool> SendAsync(NotificationMessage m, CancellationToken ct)
    {
        if (_url is null) return false;
        var now = _clock.GetUtcNow();
        if (now < _pausedUntil) return false;
        try
        {
            using var req = BuildRequest(m);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.IsSuccessStatusCode) { _failures = 0; return true; }
            _log.LogWarning("ntfy returned {Status} for {Host}", (int)res.StatusCode, _url.Host);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("ntfy request to {Host} failed: {Message}", _url.Host, ex.Message);
        }
        if (++_failures >= FailuresBeforePause)
        {
            _pausedUntil = now + PauseAfterFailures;
            _failures = 0;
            _log.LogWarning("ntfy paused for {Minutes} min after {N} failures", PauseAfterFailures.TotalMinutes, FailuresBeforePause);
        }
        return false;
    }

    private string? ReadToken()
    {
        if (string.IsNullOrWhiteSpace(_options.NtfyTokenFile)) return null;
        try
        {
            var t = File.ReadAllText(_options.NtfyTokenFile).Trim();
            // Only token characters: a stray newline or space must not break (or inject into) the header.
            return t.Length > 0 && t.All(c => c is > ' ' and < (char)127) ? t : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Cannot read Notifications.NtfyTokenFile: {Message}", ex.Message);
            return null;
        }
    }
}
