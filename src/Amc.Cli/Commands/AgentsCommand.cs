using Amc.Cli.Infrastructure;
using Amc.Core.Domain;

namespace Amc.Cli.Commands;

public static class AgentsCommand
{
    public static readonly TimeSpan HideTerminalAfter = TimeSpan.FromHours(1);

    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        if (args.Json)
        {
            Api.PrintJson(ctx, (await ctx.Client.GetAsync("api/agents", ct)).Body);
            return ExitCodes.Ok;
        }
        var agents = await Api.Agents(ctx, ct);
        var worktrees = (await Api.Worktrees(ctx, ct)).ToDictionary(w => w.Id);
        var (table, hidden) = AgentTree.Build(agents, worktrees, ctx.Now(), args.Flag("all"), ctx.Style, ctx.Home);
        if (table.Count == 0) ctx.Out.WriteLine(agents.Count == 0 ? "No agents observed yet." : "No active agents.");
        else table.Write(ctx.Out, ctx.Style);
        if (hidden > 0) ctx.Out.WriteLine(ctx.Style.Dim($"({hidden} finished agent{(hidden == 1 ? "" : "s")} older than 1h hidden; --all to show)"));
        return ExitCodes.Ok;
    }
}

/// <summary>Parent/child tree rendering of agents. Pure; unit-tested.</summary>
public static class AgentTree
{
    public static (TextTable Table, int Hidden) Build(
        IReadOnlyCollection<Agent> agents, IReadOnlyDictionary<string, Worktree> worktrees,
        DateTimeOffset now, bool all, Style style, string home)
    {
        var byId = agents.ToDictionary(a => a.Id);
        bool Stale(Agent a) => a.IsTerminal && now - (a.CompletedAt ?? a.LastActivityAt) > AgentsCommand.HideTerminalAfter;

        // Visible = not stale, or has a visible descendant (so the tree never loses its context).
        var visible = new HashSet<string>();
        foreach (var a in agents.Where(a => all || !Stale(a)))
            for (var cur = a; cur is not null && visible.Add(cur.Id); cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null) { }

        var children = agents
            .Where(a => a.ParentId is { } p && byId.ContainsKey(p))
            .GroupBy(a => a.ParentId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id, StringComparer.Ordinal).ToList());
        var roots = agents.Where(a => a.ParentId is null || !byId.ContainsKey(a.ParentId))
            .OrderBy(a => a.IsTerminal).ThenByDescending(a => a.LastActivityAt).ThenBy(a => a.Id, StringComparer.Ordinal);

        var table = new TextTable("AGENT", "STATUS", "EV", "ACTION", "WORKTREE", "AGE", "PID");
        void Emit(Agent a, string prefix, string childPrefix, HashSet<string> seen)
        {
            if (!seen.Add(a.Id)) return; // defensive against cycles
            table.Add(Row(a, prefix, worktrees, now, style, home));
            var kids = children.GetValueOrDefault(a.Id)?.Where(k => visible.Contains(k.Id)).ToList() ?? [];
            for (var i = 0; i < kids.Count; i++)
            {
                var last = i == kids.Count - 1;
                Emit(kids[i], childPrefix + (last ? "└─ " : "├─ "), childPrefix + (last ? "   " : "│  "), seen);
            }
        }
        var seenAll = new HashSet<string>();
        foreach (var r in roots.Where(r => visible.Contains(r.Id))) Emit(r, "", "", seenAll);
        return (table, agents.Count - visible.Count);
    }

    public static string[] Row(Agent a, string prefix, IReadOnlyDictionary<string, Worktree> worktrees, DateTimeOffset now, Style style, string home)
    {
        var title = a.Title ?? a.AgentType ?? a.Description;
        var name = prefix + style.Bold(AgentRef.Short(a.Id)) + (title is { Length: > 0 } ? " " + Fmt.Trunc(title, 36) : "");
        var action = a.IsTerminal ? (a.StatusReason ?? "") : Action(a);
        return
        [
            name,
            style.Status(a.Status, Fmt.Status(a.Status)),
            style.Dim(Fmt.Evidence(a.StatusEvidence)),
            Fmt.Trunc(action, 44),
            WorktreeLabel(a, worktrees, home),
            Fmt.Age(now, a.LastActivityAt),
            a.Pid?.ToString() ?? "",
        ];
    }

    public static string Action(Agent a) =>
        a.CurrentAction ?? (a.CurrentCommand is { } c ? "$ " + c : null) ?? (a.CurrentTool is { } t ? t + (a.CurrentFile is { } f ? " " + Path.GetFileName(f) : "") : null)
        ?? a.StatusReason ?? "";

    public static string WorktreeLabel(Agent a, IReadOnlyDictionary<string, Worktree> worktrees, string home)
    {
        var wtId = a.CurrentWorktreeId ?? a.Worktrees.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        if (wtId is not null && worktrees.TryGetValue(wtId, out var w))
            return WorktreeRef.Name(w) + (w.Branch is { } b ? "@" + b : w.Head is { } h ? "@" + h[..Math.Min(7, h.Length)] : "");
        return a.GitBranch is { Length: > 0 } br && br != "HEAD" ? "@" + br : "-";
    }
}
