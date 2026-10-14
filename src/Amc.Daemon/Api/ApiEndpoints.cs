using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.Providers;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Messaging;
using Amc.Daemon.Pipeline;
using Amc.Daemon.Storage;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Api;

public sealed record MessageRequest(string Text);
public sealed record ControlRequest(string Action, bool Confirm = false);

public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (HealthRegistry health, EventPipeline pipeline, EventStore store, Broadcaster b, StateStore state) => Results.Ok(new
        {
            overall = health.Overall(),
            components = health.Snapshot(),
            database = new { ok = store.Ping(), events = store.Count() },
            queue = new { depth = pipeline.QueueDepth, dropped = pipeline.Dropped },
            websocket = new { clients = b.ClientCount },
            seq = state.Read(s => s.Seq),
            serverTime = DateTimeOffset.UtcNow,
            version = typeof(ApiEndpoints).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : null,
        }));

        api.MapGet("/state", (StateStore state) => Results.Content(state.SerializeState(), "application/json"));

        api.MapGet("/events", (HttpRequest r, EventStore store, StateStore state) =>
        {
            var q = r.Query;
            var agent = q["agent"].FirstOrDefault();
            IReadOnlyCollection<string>? agentIds = null;
            if (agent is not null && q["descendants"] == "true")
                agentIds = state.Read(s => Descendants(s, agent));
            var ev = store.Query(new EventQuery(
                After: Long(q["after"]), Before: Long(q["before"]), Limit: (int)(Long(q["limit"]) ?? 200),
                AgentId: agentIds is null ? agent : null, AgentIds: agentIds,
                RepositoryId: q["repo"].FirstOrDefault(), WorktreeId: q["worktree"].FirstOrDefault(),
                FilePath: q["file"].FirstOrDefault(),
                Types: q["type"].Where(t => !string.IsNullOrEmpty(t) && !t!.EndsWith('*')).Select(t => t!).ToList(),
                TypePrefix: q["type"].FirstOrDefault(t => t?.EndsWith('*') == true)?.TrimEnd('*'),
                From: Date(q["from"]), To: Date(q["to"]), Text: q["q"].FirstOrDefault(),
                Descending: q["order"] == "desc"));
            return Results.Ok(ev);
        });

        api.MapGet("/agents", (StateStore state) => Snap(state, s => s.Agents.Values.OrderBy(a => a.CreatedAt).ToList()));
        api.MapGet("/agents/{id}", (string id, StateStore state, AgentOperations ops) =>
        {
            var a = state.Read(s => s.Agents.GetValueOrDefault(id) is { } x ? Json.Serialize(x) : null);
            if (a is null) return Results.NotFound();
            var caps = ops.Capabilities(id);
            return Results.Content($$"""{"agent":{{a}},"capabilities":{{Json.Serialize(caps)}}}""", "application/json");
        });
        api.MapGet("/repositories", (StateStore state) => Snap(state, s => s.Repositories.Values.ToList()));
        api.MapGet("/worktrees", (StateStore state) => Snap(state, s => s.Worktrees.Values.OrderBy(w => w.Root).ToList()));
        api.MapGet("/files", (HttpRequest r, StateStore state) =>
        {
            var agent = r.Query["agent"].FirstOrDefault();
            var wt = r.Query["worktree"].FirstOrDefault();
            return Snap(state, s => s.Files.Values
                .Where(f => (agent is null || f.Agents.ContainsKey(agent)) && (wt is null || f.WorktreeId == wt))
                .OrderByDescending(f => f.LastChangedAt).ToList());
        });
        api.MapGet("/conflicts", (StateStore state) => Snap(state, s => s.Conflicts.Values.ToList()));
        api.MapGet("/messages", (HttpRequest r, StateStore state) =>
        {
            var agent = r.Query["agent"].FirstOrDefault();
            return Snap(state, s => s.Messages.Values.Where(m => agent is null || m.AgentId == agent).OrderBy(m => m.SentAt).ToList());
        });
        api.MapGet("/processes", (ILiveProcesses procs) => Results.Ok(procs.Snapshot()));

        api.MapPost("/agents/{id}/messages", async (string id, MessageRequest body, AgentOperations ops, CancellationToken ct) =>
        {
            var r = await ops.SendMessageAsync(id, body.Text ?? "", ct);
            return Results.Json(r, statusCode: r.StatusCode);
        });

        api.MapPost("/agents/{id}/control", async (string id, ControlRequest body, AgentOperations ops, IOptions<AmcOptions> opt, CancellationToken ct) =>
        {
            if (!opt.Value.EnableControl) return Results.Json(new OperationResult(false, "Control is disabled (EnableControl=false)", 403), statusCode: 403);
            if (!Enum.TryParse<ControlAction>(body.Action, ignoreCase: true, out var action))
                return Results.BadRequest(new { error = $"Unknown action '{body.Action}'" });
            var r = await ops.ControlAsync(id, action, body.Confirm, ct);
            return Results.Json(r, statusCode: r.StatusCode);
        });

        api.MapGet("/terminals/{id}/buffer", (string id, ITerminalHub hub) => Results.Bytes(hub.Buffer(id), "application/octet-stream"));

        app.Map("/ws", (HttpContext ctx, Broadcaster b, EventStore store, StateStore state, ILoggerFactory lf) =>
            WebSocketEndpoint.Handle(ctx, b, store, state, lf));
    }

    public static List<string> Descendants(Amc.Core.State.WorldState s, string root)
    {
        var result = new List<string>();
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (result.Contains(id)) continue;
            result.Add(id);
            if (s.Agents.TryGetValue(id, out var a)) foreach (var c in a.ChildIds) stack.Push(c);
        }
        return result;
    }

    /// <summary>Serializes inside the read lock: entities are mutable and must not be read after it is released.</summary>
    private static IResult Snap<T>(StateStore state, Func<Amc.Core.State.WorldState, T> f) =>
        Results.Content(state.Read(s => Json.Serialize(f(s))), "application/json");

    private static long? Long(Microsoft.Extensions.Primitives.StringValues v) => long.TryParse(v.FirstOrDefault(), out var x) ? x : null;
    private static DateTimeOffset? Date(Microsoft.Extensions.Primitives.StringValues v) =>
        DateTimeOffset.TryParse(v.FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
