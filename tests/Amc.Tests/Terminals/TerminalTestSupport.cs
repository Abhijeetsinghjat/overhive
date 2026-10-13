using System.Collections.Concurrent;
using Amc.Core.Events;
using Amc.Daemon;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Terminals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Terminals;

internal sealed class CapturingSink : IEventSink
{
    public ConcurrentQueue<AgentEvent> Events { get; } = new();
    public bool TryPublish(AgentEvent e) { Events.Enqueue(e); return true; }
    public ValueTask PublishAsync(AgentEvent e, CancellationToken ct = default) { Events.Enqueue(e); return ValueTask.CompletedTask; }
    public Task<AgentEvent?> PublishAndWaitAsync(AgentEvent e, CancellationToken ct = default) { Events.Enqueue(e); return Task.FromResult<AgentEvent?>(e); }

    public async Task<AgentEvent> WaitFor(string type, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (Events.FirstOrDefault(e => e.Type == type) is { } e) return e;
            await Task.Delay(20);
        }
        throw new TimeoutException("no " + type + " event");
    }
}

/// <summary>A minimal daemon host with just the terminal module (TestServer or real loopback Kestrel).</summary>
internal sealed class TerminalHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required CapturingSink Sink { get; init; }
    public required string DataDir { get; init; }
    public TerminalHub Hub => App.Services.GetRequiredService<TerminalHub>();

    public static async Task<TerminalHost> StartAsync(bool kestrel = false)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "amc-term-tests-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        if (kestrel) builder.WebHost.UseUrls("http://127.0.0.1:0");
        else builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var sink = new CapturingSink();
        builder.Services.AddSingleton<IEventSink>(sink);
        builder.Services.AddSingleton<HealthRegistry>();
        builder.Services.AddSingleton<StateStore>();
        builder.Services.AddSingleton<IOptions<AmcOptions>>(Options.Create(new AmcOptions { DataDir = dataDir }));
        builder.Services.AddTerminals();
        var app = builder.Build();
        app.UseWebSockets();
        app.MapTerminalEndpoints();
        await app.StartAsync();
        return new TerminalHost { App = app, Sink = sink, DataDir = dataDir };
    }

    public string BaseUrl => App.Urls.First();

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        try { Directory.Delete(DataDir, true); } catch (IOException) { }
    }
}
