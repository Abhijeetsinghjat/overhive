using System.Text.Json.Nodes;
using Amc.Cli.Infrastructure;
using Amc.Core.Domain;

namespace Amc.Cli.Commands;

public static partial class WorktreesCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        if (args.Positionals.Count > 0)
        {
            if (args.Positionals[0] != "clean") throw new UsageException("usage: agentctl worktrees [--all] | worktrees clean [<path>] [--dry-run] [--yes]");
            return await CleanAsync(ctx, args, args.Positionals.Count > 1 ? args.Positionals[1] : null, ct);
        }
        if (args.Json)
        {
            Api.PrintJson(ctx, (await ctx.Client.GetAsync("api/worktrees", ct)).Body);
            return ExitCodes.Ok;
        }
        var worktrees = await Api.Worktrees(ctx, ct);
        var repos = (await Api.Repositories(ctx, ct)).ToDictionary(r => r.Id);
        var agents = (await Api.Agents(ctx, ct)).ToDictionary(a => a.Id);
        if (worktrees.Count == 0)
        {
            ctx.Out.WriteLine("No worktrees discovered yet.");
            return ExitCodes.Ok;
        }
        Build(worktrees, repos, agents, ctx.Style, ctx.Home, args.Flag("all")).Write(ctx.Out, ctx.Style);
        return ExitCodes.Ok;
    }

    public static TextTable Build(IEnumerable<Worktree> worktrees, IReadOnlyDictionary<string, Repository> repos,
        IReadOnlyDictionary<string, Agent> agents, Style s, string home, bool includeRemoved)
    {
        var t = new TextTable("REPO", "ROOT", "BRANCH", "HEAD", "STATE", "M/A/D/?/U", "↑↓", "MERGED", "AGENTS");
        foreach (var w in worktrees.Where(w => includeRemoved || !w.Removed)
                     .OrderBy(w => repos.GetValueOrDefault(w.RepositoryId)?.Name, StringComparer.Ordinal).ThenBy(w => !w.IsMain).ThenBy(w => w.Root, StringComparer.Ordinal))
        {
            var lifecycle = w.Lifecycle;
            var state = lifecycle switch
            {
                "dirty" => s.Yellow(lifecycle),
                "clean" => s.Green(lifecycle),
                "deleted" => s.Dim(lifecycle),
                "detached" or "assigned" => s.Cyan(lifecycle),
                _ => s.Red(lifecycle), // merge / rebase / conflict operations
            };
            var counts = $"{w.Modified}/{w.Added}/{w.DeletedFiles}/{w.Untracked}/{w.Conflicted}";
            var owners = w.Agents.OrderByDescending(kv => kv.Value).Select(kv =>
            {
                var a = agents.GetValueOrDefault(kv.Key);
                var label = AgentRef.Short(kv.Key);
                return a is null ? s.Dim(label) : a.IsTerminal ? s.Dim(label) : s.Status(a.Status, label);
            });
            t.Add(
                repos.GetValueOrDefault(w.RepositoryId)?.Name ?? w.RepositoryId,
                Fmt.HomeRelative(w.Root, home) + (w.IsMain ? s.Dim(" (main)") : "") + (w.Locked ? s.Dim(" 🔒") : ""),
                w.Detached ? s.Dim("(detached)") : w.Branch ?? "",
                w.Head is { Length: >= 7 } h ? h[..7] : w.Head ?? "",
                state,
                w.Conflicted > 0 ? s.Red(counts) : w.Dirty ? s.Yellow(counts) : s.Dim(counts),
                w.Ahead + w.Behind > 0 ? $"{w.Ahead}↑{w.Behind}↓" : "",
                MergedLabel(w, s),
                string.Join(",", owners));
        }
        return t;
    }
}

public static partial class WorktreesCommand
{
    /// <summary>"PR #12 (gh)", "likely (git)", the last failed cleanup, or empty.</summary>
    public static string MergedLabel(Worktree w, Style s)
    {
        if (w.Removed || w.Merged is not { } m) return "";
        var label = m.Pr is { } pr ? s.Green($"PR #{pr} (gh)") : s.Yellow("likely (git)");
        return w.LastCleanup is { } lc ? label + " " + s.Red(lc.Outcome) : label;
    }

    /// <summary>
    /// `worktrees clean [path]`: for one worktree, or every merged one, prints what would be removed and the result of every
    /// safety check. Nothing is removed without --yes (and never with --dry-run).
    /// </summary>
    public static async Task<int> CleanAsync(CliContext ctx, ParsedArgs args, string? query, CancellationToken ct)
    {
        var s = ctx.Style;
        var worktrees = await Api.Worktrees(ctx, ct);
        var targets = query is not null
            ? [WorktreeRef.Resolve(worktrees, query, ctx.Cwd)]
            : worktrees.Where(w => !w.Removed && !w.IsMain && w.Merged is not null).OrderBy(w => w.Root, StringComparer.Ordinal).ToList();
        var apply = args.Flag("yes") && !args.Flag("dry-run");
        var results = new JsonArray();
        if (targets.Count == 0 && !args.Json) ctx.Out.WriteLine("No merged worktrees to clean up.");
        var failed = 0;
        foreach (var w in targets)
        {
            var path = $"api/worktrees/{w.Id}/cleanup"; // ids ("wt:<hex>") are safe path segments
            var res = await ctx.Client.SendAsync(HttpMethod.Post, path, new { confirm = apply, dryRun = !apply }, ct);
            if (res.Status == 401) throw AmcClient.ToException(res, "worktrees clean");
            var node = res.Node as JsonObject;
            if (node is not null) results.Add(node.DeepClone());
            if (!res.Success) failed++;
            if (args.Json) continue;

            var plan = node?["plan"] as JsonObject;
            var canRemove = plan?["checks"] is JsonArray checks && checks.All(c => c?["ok"]?.GetValue<bool>() == true);
            var verb = !apply ? (canRemove ? s.Green("would remove") : s.Yellow("would keep")) : res.Success ? s.Green("removed") : s.Red(node?["outcome"]?.ToString() ?? "failed");
            ctx.Out.WriteLine($"{s.Bold(Fmt.HomeRelative(w.Root, ctx.Home))}  {verb}");
            if (plan is not null)
            {
                ctx.Out.WriteLine($"  worktree       {plan["root"]}");
                if (plan["branch"] is { } b) ctx.Out.WriteLine($"  local branch   {b}");
                if (plan["remoteBranch"] is { } rb) ctx.Out.WriteLine($"  remote branch  {rb}");
                foreach (var c in (plan["checks"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var ok = c["ok"]?.GetValue<bool>() == true;
                    ctx.Out.WriteLine($"  {(ok ? s.Green("✓") : s.Red("✗"))} {c["name"],-15}{(ok ? s.Dim(c["detail"]?.ToString() ?? "") : c["detail"]?.ToString())}");
                }
            }
            foreach (var st in (node?["steps"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var o = st["outcome"]?.ToString();
                ctx.Out.WriteLine($"  {(o == "ok" ? s.Green("✓") : o == "skipped" ? s.Dim("–") : s.Red("✗"))} $ {st["command"]}" + (st["detail"] is { } d ? s.Dim(" — " + d) : ""));
            }
            var detail = node?["detail"]?.ToString() ?? node?["error"]?.ToString() ?? res.ErrorText();
            (res.Success ? ctx.Out : ctx.Err).WriteLine("  " + detail);
        }
        if (args.Json) Api.PrintJson(ctx, results);
        else if (!apply && targets.Count > 0) ctx.Out.WriteLine(s.Dim("Dry run: nothing was removed. Run with --yes to remove the worktrees marked 'would remove'."));
        return failed == 0 ? ExitCodes.Ok : ExitCodes.Error;
    }
}

public static class FilesCommand
{
    public static async Task<int> RunAsync(CliContext ctx, ParsedArgs args, CancellationToken ct)
    {
        string? agentId = null, worktreeId = null;
        if (args.Opt("agent") is { } aq) agentId = (await Api.ResolveAgent(ctx, aq, ct)).Id;
        var worktreeList = await Api.Worktrees(ctx, ct);
        if (args.Opt("worktree") is { } wq) worktreeId = WorktreeRef.Resolve(worktreeList, wq, ctx.Cwd).Id;

        var path = "api/files" + AmcClient.Query(("agent", agentId), ("worktree", worktreeId));
        if (args.Json)
        {
            Api.PrintJson(ctx, (await ctx.Client.GetAsync(path, ct)).Body);
            return ExitCodes.Ok;
        }
        var files = await ctx.Client.GetAsync<List<FileChange>>(path, ct);
        if (files.Count == 0)
        {
            ctx.Out.WriteLine("No changed files.");
            return ExitCodes.Ok;
        }
        Build(files, worktreeList.ToDictionary(w => w.Id), ctx.Style).Write(ctx.Out, ctx.Style);
        return ExitCodes.Ok;
    }

    public static TextTable Build(IReadOnlyList<FileChange> files, IReadOnlyDictionary<string, Worktree> worktrees, Style s)
    {
        var multi = files.Select(f => f.WorktreeId).Distinct().Count() > 1;
        var t = new TextTable("ST", "+/-", "PATH", "AGENTS");
        foreach (var f in files)
            t.Add(StatusLetter(f, s), PlusMinus(f, s), DisplayPath(f, worktrees, multi),
                string.Join(" ", f.Agents.OrderByDescending(kv => kv.Value.LastAt)
                    .Select(kv => $"{AgentRef.Short(kv.Key)}{s.Dim("(" + Fmt.Evidence(kv.Value.Evidence) + ")")}")));
        return t;
    }

    /// <summary>Observed git status letter; falls back to the agent-reported change kind in lower case (not yet confirmed by git).</summary>
    public static string StatusLetter(FileChange f, Style s)
    {
        var st = f.GitStatus;
        if (st == "clean") return s.Dim("·");
        if (st is { Length: > 0 })
            return st switch { "A" or "?" => s.Green(st), "D" => s.Red(st), "U" => s.Red(s.Bold(st)), _ => s.Yellow(st) };
        return s.Dim(f.LastChangeKind switch { "created" => "a", "deleted" => "d", "renamed" => "r", "modified" => "m", _ => "?" });
    }

    public static string PlusMinus(FileChange f, Style s) =>
        f.Added is null && f.Removed is null ? s.Dim("-") : $"{s.Green("+" + (f.Added ?? 0))}/{s.Red("-" + (f.Removed ?? 0))}";

    public static string DisplayPath(FileChange f, IReadOnlyDictionary<string, Worktree> worktrees, bool multi)
    {
        var path = f.OldPath is { } old ? $"{old} → {f.Path}" : f.Path;
        return multi && worktrees.TryGetValue(f.WorktreeId, out var w) ? $"{WorktreeRef.Name(w)}:{path}" : path;
    }
}
