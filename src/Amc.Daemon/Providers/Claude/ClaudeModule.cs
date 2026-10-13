using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Providers;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Providers.Claude;

/// <summary>Claude Code provider module: registry collector, transcript tailer, hook receiver, provider.</summary>
public static class ClaudeModule
{
    public static IServiceCollection AddClaude(this IServiceCollection s)
    {
        s.TryAddSingleton(TimeProvider.System);
        s.TryAddSingleton<IProcFs, LinuxProcFs>();
        s.TryAddSingleton<ISignalSender, LibcSignalSender>();
        s.TryAddSingleton<ICursorStore>(sp => new EventStoreCursorStore(sp.GetRequiredService<EventStore>()));
        s.AddSingleton(sp => new ClaudeHooksState(Path.Combine(sp.GetRequiredService<IOptions<AmcOptions>>().Value.ClaudeHome, "settings.json")));
        s.AddSingleton<ClaudeContext>();
        s.AddSingleton<ClaudeEventMapper>();
        s.AddSingleton<TranscriptIngestor>();
        s.AddSingleton<ClaudeRewake>();
        s.AddSingleton<ClaudeHookReceiver>();
        s.AddSingleton<ClaudeRegistryCollector>();
        s.AddSingleton<ClaudeProvider>();
        s.AddSingleton<IAgentProvider>(sp => sp.GetRequiredService<ClaudeProvider>());
        s.AddHostedService<ClaudeMailboxRestorer>();
        s.AddHostedService(sp => sp.GetRequiredService<ClaudeRegistryCollector>());
        s.AddHostedService<TranscriptTailer>();
        s.AddHostedService(sp => sp.GetRequiredService<ClaudeHookReceiver>());
        return s;
    }

    /// <summary>POST /hooks/claude — token enforced by SecurityMiddleware. Empty body = no hook output.</summary>
    public static void MapClaudeEndpoints(WebApplication app)
    {
        app.MapPost("/hooks/claude", async (HttpRequest req, ClaudeHookReceiver receiver, CancellationToken ct) =>
        {
            JsonObject? payload;
            try { payload = await JsonNode.ParseAsync(req.Body, cancellationToken: ct) as JsonObject; }
            catch (JsonException) { payload = null; }
            if (payload is null) return Results.Empty; // fail open: never feed errors to the agent
            var response = await receiver.HandleAsync(payload, ct);
            return response is null ? Results.Empty : Results.Text(response.ToJsonString(), "application/json");
        });

        // Long-poll for the asyncRewake waiter hook (see ClaudeRewake). 204 = nothing to deliver (hook exits 0);
        // 200 + text = wake the idle session with this text (hook prints it to stderr and exits 2).
        app.MapPost("/hooks/claude/wait", async (HttpRequest req, ClaudeRewake rewake, CancellationToken ct) =>
        {
            JsonObject? payload;
            try { payload = await JsonNode.ParseAsync(req.Body, cancellationToken: ct) as JsonObject; }
            catch (JsonException) { payload = null; }
            if (payload is null) return Results.NoContent();
            var text = await rewake.WaitAsync(payload, ct);
            return text is null ? Results.NoContent() : Results.Text(text, "text/plain; charset=utf-8");
        });
    }
}

/// <summary>On startup, puts messages still queued for the hook mailbox back into the in-memory mailbox.</summary>
public sealed class ClaudeMailboxRestorer(StateStore state, Mailbox mailbox, HealthRegistry health) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        var queued = state.Read(s => s.Messages.Values
            .Where(m => m.Status == MessageStatus.Queued && m.Transport == Transports.HookMailbox
                        && s.Agents.TryGetValue(m.AgentId, out var a) && a.Provider == ClaudePaths.Provider && !a.IsTerminal)
            .OrderBy(m => m.SentAt)
            .Select(m => new PendingMessage(m.Id, m.AgentId, m.Text, m.SentAt))
            .ToList());
        foreach (var m in queued) mailbox.Enqueue(m);
        health.SetCounter(ClaudeHookReceiver.HealthName, "messages_restored", queued.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
