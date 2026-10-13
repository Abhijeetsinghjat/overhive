using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Providers;
using Amc.Daemon.Infrastructure;

namespace Amc.Daemon.Tools;

/// <summary>Tool-neutral integration: tool registry, generic process discovery, open protocol and providers.</summary>
public static class ToolsModule
{
    public static IServiceCollection AddTools(this IServiceCollection s, AmcOptions options)
    {
        s.AddSingleton<ToolRegistry>();
        s.AddSingleton<ProtocolInbox>();
        s.AddSingleton<ProtocolReceiver>();
        s.AddSingleton<SessionLauncher>();
        s.AddSingleton<GenericToolCollector>();
        s.AddHostedService(sp => sp.GetRequiredService<GenericToolCollector>());
        s.AddSingleton<ToolHookReceiver>();
        // One provider per tool that has no native adapter (native ones register their own).
        foreach (var tool in AgentToolCatalog.Merge(options.Tools).Where(t => t.Enabled && t.Adapter is null))
            s.AddSingleton<IAgentProvider>(sp => new GenericToolProvider(tool, sp.GetRequiredService<ITerminalHub>(),
                sp.GetRequiredService<ILiveProcesses>(), sp.GetRequiredService<ProtocolInbox>()));
        return s;
    }

    public static void MapToolEndpoints(WebApplication app)
    {
        // Known tools and whether each is installed (for the launch dialog and the Integrations view).
        app.MapGet("/api/tools", (ToolRegistry tools) => Results.Ok(tools.All.Select(t => new
        {
            t.Id, t.Name, t.Command, t.Models, canPickModel = t.ModelFlag is not null, takesPrompt = t.PromptArg is not null,
            integration = t.Adapter is null ? "generic" : "native", installed = ToolRegistry.FindExecutable(t.Command) is not null,
        })));

        // Start a new session of any configured tool in a Overhive-owned terminal.
        app.MapPost("/api/sessions", async (LaunchRequest body, SessionLauncher launcher, CancellationToken ct) =>
        {
            var r = await launcher.LaunchAsync(body, ct);
            return Results.Json(r, statusCode: r.StatusCode);
        });

        // Native hook adapters (Codex, Gemini CLI, Grok, Cursor, Copilot, …): the tool's hook POSTs its payload here.
        app.MapPost("/hooks/{tool}", async (string tool, HttpRequest req, ToolHookReceiver receiver, CancellationToken ct) =>
        {
            if (tool == "claude" || !AgentTool.ValidId(tool)) return Results.NotFound();
            JsonObject? payload;
            try { payload = await JsonNode.ParseAsync(req.Body, cancellationToken: ct) as JsonObject; }
            catch (JsonException) { payload = null; }
            if (payload is null) return Results.NoContent(); // fail open: never feed errors to the agent
            var reply = await receiver.HandleAsync(tool, req.Query["e"].FirstOrDefault(), payload, ct);
            return reply is null ? Results.NoContent() : Results.Text(reply.ToJsonString(), "application/json");
        });

        // Open protocol v1 (auth: the private socket or the API token, like the rest of /api).
        app.MapPost("/api/v1/report", async (HttpRequest req, ProtocolReceiver receiver, CancellationToken ct) =>
        {
            JsonObject? body;
            try { body = await JsonNode.ParseAsync(req.Body, cancellationToken: ct) as JsonObject; }
            catch (JsonException) { body = null; }
            if (body is null) return Results.BadRequest(new { error = "JSON object body required" });
            var r = await receiver.ReportAsync(body, ct);
            return r.Ok ? Results.Ok(new { ok = true, agentId = r.AgentId }) : Results.BadRequest(new { error = r.Error });
        });
        app.MapPost("/api/v1/inbox", async (HttpRequest req, ProtocolInbox inbox, CancellationToken ct) =>
        {
            JsonObject? body;
            try { body = await JsonNode.ParseAsync(req.Body, cancellationToken: ct) as JsonObject; }
            catch (JsonException) { body = null; }
            var id = ProtocolReceiver.AgentIdFor((string?)body?["tool"], (string?)body?["agent"]);
            if (id is null) return Results.BadRequest(new { error = "tool and agent are required" });
            var wait = TimeSpan.FromSeconds(Math.Clamp(body?["wait"] is JsonValue w && w.TryGetValue<int>(out var s) ? s : 0, 0, 50));
            var msgs = await inbox.TakeAsync(id, wait, ct);
            return msgs.Count == 0 ? Results.NoContent()
                : Results.Ok(new { messages = msgs.Select(m => new { id = m.MessageId, text = m.Text, sentAt = m.QueuedAt }) });
        });
    }
}
