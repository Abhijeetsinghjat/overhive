using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Domain;

namespace Amc.Cli.Commands;

/// <summary>Typed fetchers for the REST endpoints in ARCHITECTURE.md §11.</summary>
public static class Api
{
    public static Task<List<Agent>> Agents(CliContext ctx, CancellationToken ct) => ctx.Client.GetAsync<List<Agent>>("api/agents", ct);
    public static Task<List<Worktree>> Worktrees(CliContext ctx, CancellationToken ct) => ctx.Client.GetAsync<List<Worktree>>("api/worktrees", ct);
    public static Task<List<Repository>> Repositories(CliContext ctx, CancellationToken ct) => ctx.Client.GetAsync<List<Repository>>("api/repositories", ct);
    public static Task<List<Conflict>> Conflicts(CliContext ctx, CancellationToken ct) => ctx.Client.GetAsync<List<Conflict>>("api/conflicts", ct);
    public static Task<JsonNode> Health(CliContext ctx, CancellationToken ct) => ctx.Client.GetNodeAsync("api/health", ct);

    public static Task<List<FileChange>> Files(CliContext ctx, string? agentId, string? worktreeId, CancellationToken ct) =>
        ctx.Client.GetAsync<List<FileChange>>("api/files" + AmcClient.Query(("agent", agentId), ("worktree", worktreeId)), ct);

    public static async Task<Agent> ResolveAgent(CliContext ctx, string query, CancellationToken ct) =>
        AgentResolver.Resolve(await Agents(ctx, ct), query);

    public static async Task<Worktree> ResolveWorktree(CliContext ctx, string query, CancellationToken ct) =>
        WorktreeRef.Resolve(await Worktrees(ctx, ct), query, ctx.Cwd);

    /// <summary>Prints a JSON node indented (the --json output of every read command).</summary>
    public static void PrintJson(CliContext ctx, JsonNode? node) =>
        ctx.Out.WriteLine(node?.ToJsonString(Pretty) ?? "null");

    public static void PrintJson(CliContext ctx, string rawJson) => PrintJson(ctx, ApiResponse.TryParse(rawJson) ?? JsonValue.Create(rawJson));

    public static readonly System.Text.Json.JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
