using System.Net;
using Amc.Daemon.Infrastructure;

namespace Amc.Daemon.Api;

public sealed class ApiToken(string value)
{
    public string Value { get; } = value;
    public const string Header = "X-Amc-Token";
    public const string QueryParam = "token";
}

/// <summary>
/// 1. Host header must name a loopback host (defeats DNS rebinding).
/// 2. Cross-origin requests are rejected (Origin, when present, must be our own origin).
/// 3. Everything except the UI shell and static assets requires the API token.
/// </summary>
public sealed class SecurityMiddleware(RequestDelegate next, ApiToken token, ILogger<SecurityMiddleware> log)
{
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "[::1]", "::1" };

    public async Task InvokeAsync(HttpContext ctx)
    {
        var host = ctx.Request.Host.Host;
        if (!LoopbackHosts.Contains(host) && !IsLoopbackAddress(host))
        {
            log.LogWarning("Rejected request with non-loopback Host header {Host}", host);
            await Reject(ctx, 421, "Host not allowed");
            return;
        }

        if (ctx.Request.Headers.Origin is { Count: > 0 } origin && !SameOrigin(origin.ToString(), ctx))
        {
            log.LogWarning("Rejected cross-origin request from {Origin}", origin.ToString());
            await Reject(ctx, 403, "Cross-origin requests are not allowed");
            return;
        }

        // Requests over the Unix socket are authenticated by the filesystem: the socket lives in a
        // 0700 directory owned by this user, so only this user can connect to it.
        if (RequiresToken(ctx.Request.Path) && !IsUnixSocket(ctx))
        {
            var presented = ctx.Request.Headers[ApiToken.Header].FirstOrDefault()
                            ?? (AcceptsQueryToken(ctx.Request.Path) ? ctx.Request.Query[ApiToken.QueryParam].FirstOrDefault() : null);
            if (!TokenStore.FixedTimeEquals(presented, token.Value))
            {
                await Reject(ctx, 401, "Missing or invalid token");
                return;
            }
        }

        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        await next(ctx);
    }

    /// <summary>Browsers cannot set headers on WebSocket upgrades, so socket endpoints also accept ?token=.</summary>
    private static bool AcceptsQueryToken(PathString p) => p.StartsWithSegments("/ws") || p.StartsWithSegments("/pty");

    public static bool IsUnixSocket(HttpContext ctx) => UnixSocketConnections.Contains(ctx.Connection.Id);

    private static bool RequiresToken(PathString p) =>
        p.StartsWithSegments("/api") || p.StartsWithSegments("/ws") || p.StartsWithSegments("/hooks") || p.StartsWithSegments("/pty");

    private static bool IsLoopbackAddress(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);

    private static bool SameOrigin(string origin, HttpContext ctx) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u)
        && (LoopbackHosts.Contains(u.Host) || IsLoopbackAddress(u.Host))
        && u.Port == ctx.Connection.LocalPort;

    private static Task Reject(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = message });
    }
}

/// <summary>Kestrel connection ids accepted on the Unix socket listener (registered by connection middleware).</summary>
public static class UnixSocketConnections
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Ids = new();
    public static void Add(string id) => Ids[id] = 0;
    public static void Remove(string id) => Ids.TryRemove(id, out _);
    public static bool Contains(string? id) => id is not null && Ids.ContainsKey(id);
}
