using System.Text.Json;
using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Domain;
using Amc.Core.Events;

namespace Amc.Cli.Commands;

public static class InspectCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var all = await Api.Agents(ctx, ct);
        var agent = AgentResolver.Resolve(all, args.Positionals[0]);
        var detailRaw = (await ctx.Client.GetAsync("api/agents/" + AmcClient.Seg(agent.Id), ct)).Body;
        var filesRaw = (await ctx.Client.GetAsync("api/files" + AmcClient.Query(("agent", agent.Id)), ct)).Body;

        if (args.Json)
        {
            var node = ApiResponse.TryParse(detailRaw) as JsonObject ?? new JsonObject();
            node["files"] = JsonNode.Parse(filesRaw);
            Api.PrintJson(ctx, node);
            return ExitCodes.Ok;
        }

        var detail = JsonNode.Parse(detailRaw);
        var full = detail?["agent"].Deserialize<Agent>(Json.Options) ?? agent;
        var files = JsonSerializer.Deserialize<List<FileChange>>(filesRaw, Json.Options) ?? [];
        var worktrees = (await Api.Worktrees(ctx, ct)).ToDictionary(w => w.Id);
        Render(ctx, full, detail?["capabilities"], files, all, worktrees);
        return ExitCodes.Ok;
    }

    public static void Render(CliContext ctx, Agent a, JsonNode? caps, IReadOnlyList<FileChange> files,
        IReadOnlyCollection<Agent> all, IReadOnlyDictionary<string, Worktree> worktrees)
    {
        var s = ctx.Style;
        var w = ctx.Out;
        var now = ctx.Now();
        var byId = all.ToDictionary(x => x.Id);
        void Kv(string k, string? v) { if (!string.IsNullOrEmpty(v)) w.WriteLine($"  {s.Dim(k.PadRight(13))} {v}"); }

        w.WriteLine($"{s.Bold(AgentRef.Short(a.Id))} {a.Title}");
        Kv("id", a.Id);
        Kv("status", $"{s.Status(a.Status, Fmt.Status(a.Status))} ({Fmt.Evidence(a.StatusEvidence)})" + (a.StatusReason is { } r ? " — " + r : ""));
        Kv("kind", $"{Fmt.JsonName(a.Kind)}" + (a.AgentType is { } t ? $" · {t}" : "") + $" · provider {a.Provider}");
        if (a.ParentId is { } p) Kv("parent", byId.TryGetValue(p, out var pa) ? AgentRef.Label(pa) : p);
        Kv("description", a.Description is { } d ? Fmt.Trunc(d, 160) : null);
        Kv("model", a.Model is null ? null : a.Model + (a.Version is { } ver ? $" (claude {ver})" : ""));
        Kv("session", a.SessionId);
        Kv("pid", a.Pid?.ToString());
        Kv("terminal", a.TerminalId);
        Kv("cwd", Fmt.HomeRelative(a.Cwd, ctx.Home));
        Kv("branch", a.GitBranch);
        Kv("created", $"{a.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({Fmt.Age(now, a.CreatedAt)} ago)");
        Kv("last active", $"{Fmt.Age(now, a.LastActivityAt)} ago");
        if (a.CompletedAt is { } c) Kv("ended", $"{c.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        if (!a.IsTerminal) Kv("now", AgentTree.Action(a));
        Kv("last prompt", a.LastPrompt is { } lp ? Fmt.Trunc(lp, 160) : null);
        Kv("last output", a.LastOutput is { } lo ? Fmt.Trunc(lo, 160) : null);
        Kv("tools", $"{a.ToolCount} calls, {a.ToolFailures} failed, {a.FilesChanged} files changed");
        Kv("transcript", Fmt.HomeRelative(a.TranscriptPath, ctx.Home));

        if (a.Worktrees.Count > 0)
        {
            w.WriteLine(s.Bold("Worktrees"));
            foreach (var (id, at) in a.Worktrees.OrderByDescending(kv => kv.Value))
            {
                var label = worktrees.TryGetValue(id, out var wt) ? $"{Fmt.HomeRelative(wt.Root, ctx.Home)} [{wt.Branch ?? "detached"}]" : id;
                w.WriteLine($"  {label}  {s.Dim("inf · " + Fmt.Age(now, at) + " ago")}" + (id == a.CurrentWorktreeId ? s.Cyan("  (current)") : ""));
            }
        }

        w.WriteLine(s.Bold("Capabilities"));
        RenderCapabilities(ctx, caps);

        w.WriteLine(s.Bold($"Tasks ({a.Tasks.Count})"));
        if (a.Tasks.Count == 0) w.WriteLine(s.Dim("  none reported"));
        foreach (var task in a.Tasks)
        {
            var (icon, text) = task.State switch
            {
                TaskState.Completed => (s.Green("✓"), s.Dim(task.Title)),
                TaskState.InProgress => (s.Yellow("▶"), s.Bold(task.ActiveForm ?? task.Title)),
                TaskState.Blocked => (s.Red("!"), task.Title),
                TaskState.Cancelled => (s.Dim("✗"), s.Dim(task.Title)),
                _ => ("○", task.Title),
            };
            w.WriteLine($"  {icon} {text}");
        }

        w.WriteLine(s.Bold($"Files ({files.Count})"));
        if (files.Count == 0) w.WriteLine(s.Dim("  none attributed"));
        else
        {
            var table = new TextTable("ST", "+/-", "PATH", "EVIDENCE");
            foreach (var f in files.Take(50))
            {
                var ev = f.Agents.TryGetValue(a.Id, out var at) ? $"{Fmt.Evidence(at.Evidence)} · {at.Edits} edit(s)" : "";
                table.Add("  " + FilesCommand.StatusLetter(f, s), FilesCommand.PlusMinus(f, s), FilesCommand.DisplayPath(f, worktrees, multi: true), s.Dim(ev));
            }
            table.Write(w, s, header: false);
            if (files.Count > 50) w.WriteLine(s.Dim($"  … {files.Count - 50} more (agentctl files --agent {AgentRef.Short(a.Id)})"));
        }

        var kids = all.Where(x => x.ParentId == a.Id).OrderBy(x => x.CreatedAt).ToList();
        if (kids.Count > 0)
        {
            w.WriteLine(s.Bold($"Children ({kids.Count})"));
            foreach (var k in kids)
                w.WriteLine($"  {s.Bold(AgentRef.Short(k.Id))}  {s.Status(k.Status, Fmt.Status(k.Status)),-10}  {Fmt.Trunc(k.Title ?? k.AgentType, 60)}  {s.Dim(Fmt.Age(now, k.LastActivityAt))}");
        }
    }

    private static void RenderCapabilities(CliContext ctx, JsonNode? caps)
    {
        var s = ctx.Style;
        var w = ctx.Out;
        if (caps is null)
        {
            w.WriteLine(s.Dim("  unknown (no provider loaded for this agent)"));
            return;
        }
        var transports = caps["messageTransports"] as JsonArray;
        w.WriteLine($"  {s.Dim("messaging".PadRight(13))} " +
                    (transports is { Count: > 0 } ? string.Join(", ", transports.Select(x => x?.ToString())) : s.Yellow("no transport — messages cannot be delivered")));
        if (caps["controls"] is JsonObject controls)
            foreach (var (action, reason) in controls)
            {
                var supported = reason is null || reason.GetValueKind() == JsonValueKind.Null;
                w.WriteLine($"  {s.Ok(supported)} {action.ToLowerInvariant(),-11} {(supported ? "" : s.Dim(reason!.ToString()))}");
            }
    }
}
