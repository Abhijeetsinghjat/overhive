using Amc.Core.Domain;

namespace Amc.Cli.Infrastructure;

public static class AgentRef
{
    /// <summary>Last id segment (session id for main agents, agent id for sub-agents).</summary>
    public static string LocalPart(string id)
    {
        var colon = id.IndexOf(':');
        var rest = colon >= 0 ? id[(colon + 1)..] : id;
        var slash = rest.LastIndexOf('/');
        return slash >= 0 ? rest[(slash + 1)..] : rest;
    }

    /// <summary>The id users type and see: first 8 characters of <see cref="LocalPart"/>.</summary>
    public static string Short(string id)
    {
        var l = LocalPart(id);
        return l.Length <= 8 ? l : l[..8];
    }

    public static string Label(Agent a) => a.Title is { Length: > 0 } t ? $"{Short(a.Id)} {Fmt.Trunc(t, 40)}" : Short(a.Id);
}

/// <summary>
/// Resolves a user-supplied agent reference: exact id, exact short/local id, unique id prefix,
/// then unique case-insensitive title substring. Ambiguity is an error listing the candidates.
/// </summary>
public static class AgentResolver
{
    public static Agent Resolve(IReadOnlyCollection<Agent> agents, string query)
    {
        query = query.Trim();
        if (query.Length == 0) throw new UsageException("empty agent reference");

        var exact = agents.FirstOrDefault(a => a.Id == query);
        if (exact is not null) return exact;

        var local = agents.Where(a => AgentRef.LocalPart(a.Id) == query || a.Id.AfterProvider() == query).ToList();
        if (local.Count == 1) return local[0];
        if (local.Count > 1) throw Ambiguous(query, local);

        var prefix = agents.Where(a =>
            a.Id.StartsWith(query, StringComparison.Ordinal) ||
            a.Id.AfterProvider().StartsWith(query, StringComparison.Ordinal) ||
            AgentRef.LocalPart(a.Id).StartsWith(query, StringComparison.Ordinal)).ToList();
        if (prefix.Count == 1) return prefix[0];
        if (prefix.Count > 1) throw Ambiguous(query, prefix);

        var titled = agents.Where(a => a.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (titled.Count == 1) return titled[0];
        if (titled.Count > 1)
        {
            var exactTitle = titled.Where(a => string.Equals(a.Title, query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exactTitle.Count == 1) return exactTitle[0];
            throw Ambiguous(query, titled);
        }
        throw new CliException($"no agent matches '{query}' (try `agentctl agents --all`)");
    }

    private static string AfterProvider(this string id)
    {
        var colon = id.IndexOf(':');
        return colon >= 0 ? id[(colon + 1)..] : id;
    }

    private static CliException Ambiguous(string query, IReadOnlyList<Agent> matches)
    {
        var lines = matches.OrderByDescending(a => a.LastActivityAt).Take(10)
            .Select(a => $"  {AgentRef.Short(a.Id),-8}  {Fmt.Status(a.Status),-12}  {Fmt.Trunc(a.Title, 50)}   ({a.Id})");
        var more = matches.Count > 10 ? $"\n  … and {matches.Count - 10} more" : "";
        return new UsageException($"'{query}' is ambiguous; it matches {matches.Count} agents:\n{string.Join('\n', lines)}{more}\nUse a longer id prefix.");
    }
}

public static class WorktreeRef
{
    public static string Name(Worktree w) => Path.GetFileName(w.Root.TrimEnd('/')) is { Length: > 0 } n ? n : w.Root;

    /// <summary>Exact id, id prefix, root path (absolute or relative to cwd, or any path inside it), exact branch, then root-name substring.</summary>
    public static Worktree Resolve(IReadOnlyCollection<Worktree> worktrees, string query, string cwd)
    {
        var live = worktrees.Where(w => !w.Removed).ToList();
        var byId = worktrees.FirstOrDefault(w => w.Id == query || w.Id == "wt:" + query);
        if (byId is not null) return byId;

        var idPrefix = worktrees.Where(w => w.Id.StartsWith(query, StringComparison.Ordinal) || w.Id.StartsWith("wt:" + query, StringComparison.Ordinal)).ToList();
        if (idPrefix.Count == 1) return idPrefix[0];

        if (query.StartsWith('/') || query.StartsWith('.') || query.StartsWith('~'))
        {
            var expanded = query.StartsWith('~')
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), query[1..].TrimStart('/'))
                : Path.Combine(cwd, query);
            var full = Path.GetFullPath(expanded).TrimEnd('/');
            var containing = live.Where(w => full == w.Root.TrimEnd('/') || full.StartsWith(w.Root.TrimEnd('/') + "/", StringComparison.Ordinal))
                .OrderByDescending(w => w.Root.Length).FirstOrDefault();
            if (containing is not null) return containing;
            throw new CliException($"no known worktree contains {full}");
        }

        var branch = live.Where(w => w.Branch == query).ToList();
        if (branch.Count == 1) return branch[0];

        var named = live.Where(w => Name(w).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 1) return named[0];
        var exactName = named.Where(w => Name(w) == query).ToList();
        if (exactName.Count == 1) return exactName[0];

        var candidates = branch.Count > 1 ? branch : named.Count > 1 ? named : idPrefix;
        if (candidates.Count > 1)
            throw new UsageException($"'{query}' is ambiguous; it matches:\n" +
                                     string.Join('\n', candidates.Select(w => $"  {w.Id}  {w.Root}  [{w.Branch}]")));
        throw new CliException($"no worktree matches '{query}' (see `agentctl worktrees`)");
    }
}
