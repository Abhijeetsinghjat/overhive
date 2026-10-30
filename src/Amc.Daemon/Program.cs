using Amc.Daemon;
using Amc.Daemon.Api;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Storage;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

builder.Configuration
    .AddJsonFile(AmcOptions.ConfigPath(), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("AMC_")
    .AddCommandLine(args);
builder.Services.Configure<AmcOptions>(builder.Configuration);
var options = builder.Configuration.Get<AmcOptions>() ?? new AmcOptions();

if (options.BindAddress is not ("127.0.0.1" or "localhost" or "::1"))
    throw new InvalidOperationException(
        $"Refusing to bind to {options.BindAddress}. Remote access is not supported in this version; see SECURITY.md.");
// Extra Host names (e.g. for `tailscale serve`) are checked by SecurityMiddleware; ASP.NET's own host filtering,
// which reads the same "AllowedHosts" key, is turned off so the two can never disagree.
options.AllowedHosts = HostAllowList.Resolve(builder.Configuration);
builder.Services.PostConfigure<AmcOptions>(o => o.AllowedHosts = options.AllowedHosts);
builder.Services.Configure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(o => o.AllowedHosts = ["*"]);
// Files we create (db, WAL, logs, socket) are private to this user: umask 077 on Unix; on Windows an owner-only,
// inheritable ACL on the data directory, which every file created in it inherits.
if (!OperatingSystem.IsWindows()) Amc.Daemon.Infrastructure.NativeUmask.Set(0x3F /* 077 */);
Directory.CreateDirectory(options.DataDir);
if (OperatingSystem.IsWindows()) Amc.Core.Security.OwnerOnly.Restrict(options.DataDir);
// Unix socket paths are limited (104 bytes on macOS, 108 on Linux, counting the terminating NUL).
var socketLimit = OperatingSystem.IsMacOS() ? 103 : 107;
if (System.Text.Encoding.UTF8.GetByteCount(options.SocketPath) > socketLimit)
{
    Console.Error.WriteLine($"amcd: the private socket path is too long for this OS ({options.SocketPath}, max {socketLimit} bytes). " +
                            "Set a shorter data directory, e.g. AMC_DataDir=~/.overhive, or shorten XDG_STATE_HOME.");
    return 2;
}
if (File.Exists(options.SocketPath))
{
    // Only remove the socket if it is stale: a second instance must never break a running one.
    using var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
    try
    {
        probe.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(options.SocketPath));
        Console.Error.WriteLine($"amcd is already running (socket {options.SocketPath} is live). " +
                                (OperatingSystem.IsWindows() ? "If it runs as the scheduled task: Get-ScheduledTask Overhive (stop it with Stop-ScheduledTask Overhive)"
                                 : OperatingSystem.IsMacOS() ? "If it runs as a service: launchctl print gui/$(id -u)/dev.overhive.amcd"
                                                             : "If it runs as a service: systemctl --user status amcd"));
        return 1;
    }
    catch (System.Net.Sockets.SocketException) { File.Delete(options.SocketPath); } // stale socket from a crashed run
}
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(System.Net.IPAddress.Parse(options.BindAddress == "localhost" ? "127.0.0.1" : options.BindAddress), options.Port);
    k.ListenUnixSocket(options.SocketPath, lo => lo.Use(next => async conn =>
    {
        UnixSocketConnections.Add(conn.ConnectionId);
        try { await next(conn); }
        finally { UnixSocketConnections.Remove(conn.ConnectionId); }
    }));
});
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Amc.Core.Events.Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = Amc.Core.Events.Json.Options.DefaultIgnoreCondition;
    foreach (var c in Amc.Core.Events.Json.Options.Converters) o.SerializerOptions.Converters.Add(c);
});
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

// Core infrastructure
builder.Services.AddSingleton(new ApiToken(TokenStore.LoadOrCreate(options.TokenPath)));
builder.Services.AddSingleton<HealthRegistry>();
builder.Services.AddSingleton(sp => new EventStore(sp.GetRequiredService<IOptions<AmcOptions>>().Value.DbPath));
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<Broadcaster>();
builder.Services.AddSingleton<EventPipeline>();
builder.Services.AddSingleton<IEventSink>(sp => sp.GetRequiredService<EventPipeline>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventPipeline>());
builder.Services.AddSingleton<Mailbox>();
builder.Services.AddSingleton<AgentOperations>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<StalenessMonitor>();
builder.Services.AddHostedService<Amc.Daemon.Verification.VerificationMonitor>();
builder.Services.AddHostedService<Amc.Daemon.Storage.RetentionService>();

// Collectors & providers register themselves here (each module exposes one Add* extension).
builder.Services.AddCollectors(options);

var app = builder.Build();

app.UseMiddleware<SecurityMiddleware>();
// KeepAliveTimeout: a peer that stops answering pings (sleeping phone behind `tailscale serve`, dropped network)
// is aborted instead of holding its socket, send loop and queue until TCP gives up.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles(new DefaultFilesOptions { DefaultFileNames = [] });
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

// The UI shell: the token is embedded so the browser can call the API. Served only to loopback Hosts (middleware).
app.MapGet("/", (IWebHostEnvironment env, ApiToken token) =>
{
    var path = Path.Combine(env.WebRootPath, "index.html");
    if (!File.Exists(path)) return Results.Text("UI not built: wwwroot/index.html missing", statusCode: 500);
    var html = File.ReadAllText(path).Replace("__AMC_TOKEN__", token.Value);
    return Results.Content(html, "text/html; charset=utf-8");
});

ApiEndpoints.Map(app);
app.MapCollectorEndpoints();

// Rebuild state from the log before any collector produces events.
app.Services.GetRequiredService<EventPipeline>().Restore();

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Overhive listening on http://{Addr}:{Port}", options.BindAddress, options.Port);
    if (options.AllowedHosts.Count > 0)
        app.Logger.LogWarning("Also accepting Host names {Hosts} (AllowedHosts). Anyone who can reach them through your proxy gets full access", string.Join(", ", options.AllowedHosts));
});

app.Run();
return 0;

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
