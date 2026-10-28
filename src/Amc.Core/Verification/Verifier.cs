using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Core.Verification;

/// <summary>
/// Checks an agent's claims against evidence already in state. Pure: no IO, no clock.
/// Evidence considered is the agent's own plus its sub-agents' (a session's tests are often run by a sub-agent).
/// </summary>
public static class Verifier
{
    /// <summary>A command that ended this long after the claim's text was written still counts as "before" it (collector timestamps differ slightly).</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);
    /// <summary>How far before a "committed" claim a commit may be.</summary>
    public static readonly TimeSpan CommitLookBack = TimeSpan.FromMinutes(30);
    /// <summary>How far after a "committed" claim a commit may be (clock skew, late Git refresh).</summary>
    public static readonly TimeSpan CommitLookAhead = TimeSpan.FromMinutes(2);

    public static VerificationReport Evaluate(WorldState s, string agentId)
    {
        var tree = Tree(s, agentId);
        var logs = tree.Select(id => s.Verification.GetValueOrDefault(id)).OfType<AgentVerification>().ToList();
        var runs = logs.SelectMany(l => l.Runs).OrderBy(r => r.EndedAt).ToList();
        var edits = logs.SelectMany(l => l.Edits).OrderBy(x => x.At).ToList();
        var wts = TreeWorktrees(s, agentId);
        var claims = s.Verification.GetValueOrDefault(agentId)?.Claims ?? [];

        var checks = claims.Select(c => Check(s, agentId, c, tree, runs, edits, wts)).ToList();
        var flags = TestChangeFlag(runs, edits) is { } f ? new List<VerificationFlag> { f } : [];
        var summary = new VerificationSummary(
            checks.Count(c => c.Status == CheckStatus.Confirmed),
            checks.Count(c => c.Status == CheckStatus.Contradicted),
            checks.Count(c => c.Status == CheckStatus.Unverified),
            flags.Count);
        return new VerificationReport(agentId, summary, checks, flags);
    }

    /// <summary>The agent and all its descendants.</summary>
    public static HashSet<string> Tree(WorldState s, string agentId)
    {
        var result = new HashSet<string>();
        var stack = new Stack<string>([agentId]);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id)) continue;
            if (s.Agents.TryGetValue(id, out var a)) foreach (var c in a.ChildIds) stack.Push(c);
        }
        return result;
    }

    /// <summary>Worktrees Overhive watches that the agent or its sub-agents touched.</summary>
    public static HashSet<string> TreeWorktrees(WorldState s, string agentId)
    {
        var set = new HashSet<string>();
        foreach (var id in Tree(s, agentId))
            if (s.Agents.TryGetValue(id, out var a))
                foreach (var w in a.Worktrees.Keys)
                    if (s.Worktrees.TryGetValue(w, out var wt) && !wt.Removed) set.Add(w);
        return set;
    }

    private static VerificationCheck Check(WorldState s, string agentId, Claim c, HashSet<string> tree,
        List<CheckRun> runs, List<EditMark> edits, HashSet<string> wts)
    {
        VerificationCheck R(CheckStatus st, Evidence? ev, string obs, DateTimeOffset? at = null, string? reference = null) =>
            new(c.Id, c.Kind, agentId, c.Excerpt, c.Path, c.At, st, ev, obs, at, reference);

        switch (c.Kind)
        {
            case ClaimKind.TestsPass:
            case ClaimKind.BuildSucceeds:
            {
                var test = c.Kind == ClaimKind.TestsPass;
                var what = test ? "test" : "build";
                var run = runs.LastOrDefault(r => (test ? r.IsTest : r.IsBuild) && r.EndedAt <= c.At + Slack && (r.StartedAt is null || r.StartedAt < c.At));
                if (run is null) return R(CheckStatus.Unverified, null, $"No {what} run seen before this claim");
                var cmd = $"`{Reducer.Truncate(run.Command, 80)}`";
                var parsed = test ? s.TestRuns.GetValueOrDefault(run.ToolUseId) : null;
                var counts = parsed is null ? "" : Counts(parsed);
                if (run.Outcome == "interrupted")
                    return R(CheckStatus.Unverified, Evidence.Reported, $"{cmd} was interrupted", run.EndedAt, run.ToolUseId);
                if (run.Outcome == "failed")
                {
                    // "dotnet build && dotnet test" failing says nothing certain about the build.
                    if (!test && run.IsTest)
                        return R(CheckStatus.Unverified, Evidence.Reported, $"{cmd} {Exit(run)}, but it also runs tests, so the build itself is not certain", run.EndedAt, run.ToolUseId);
                    return R(CheckStatus.Contradicted, Evidence.Reported, $"{cmd} {Exit(run)}{counts}", run.EndedAt, run.ToolUseId);
                }
                if (parsed is { Failed: > 0 })
                    return R(CheckStatus.Contradicted, Evidence.Inferred, $"{cmd} exited 0 but its output reports {parsed.Failed} failed test(s){counts}", run.EndedAt, run.ToolUseId);
                var later = edits.Where(x => x.At > run.EndedAt && x.At <= c.At + Slack && !VerificationRules.IsDocFile(x.Path))
                    .Select(x => x.Path).Distinct().ToList();
                if (later.Count > 0)
                    return R(CheckStatus.Unverified, Evidence.Reported,
                        $"{cmd} passed, but {later.Count} file(s) changed after that run ({string.Join(", ", later.Take(3))}{(later.Count > 3 ? ", …" : "")})",
                        run.EndedAt, run.ToolUseId);
                return R(CheckStatus.Confirmed, Evidence.Reported, $"{cmd} {Exit(run)}{counts}", run.EndedAt, run.ToolUseId);
            }

            case ClaimKind.Committed:
            {
                var commit = s.Commits.Values
                    .Where(x => (x.AgentId is not null ? tree.Contains(x.AgentId) : wts.Contains(x.WorktreeId))
                                && x.At >= c.At - CommitLookBack && x.At <= c.At + CommitLookAhead)
                    .MaxBy(x => x.At);
                if (commit is not null)
                    return R(CheckStatus.Confirmed, Evidence.Observed,
                        $"Commit {commit.Sha[..Math.Min(7, commit.Sha.Length)]} “{Reducer.Truncate(commit.Subject, 80)}”{(commit.Branch is null ? "" : " on " + commit.Branch)}",
                        commit.At, commit.Sha);
                if (wts.Count == 0) return R(CheckStatus.Unverified, null, "Overhive does not know which worktree this agent works in");
                if (!c.Settled) return R(CheckStatus.Unverified, null, "Waiting for Git to show the commit");
                return R(CheckStatus.Contradicted, Evidence.Observed, $"No commit observed in {Where(s, wts)} in the 30 minutes before the claim");
            }

            default:
                return CheckFile(s, c, wts, R);
        }
    }

    private static VerificationCheck CheckFile(WorldState s, Claim c, HashSet<string> wts,
        Func<CheckStatus, Evidence?, string, DateTimeOffset?, string?, VerificationCheck> R)
    {
        if (c.Path is null) return R(CheckStatus.Unverified, null, "No path in the claim", null, null);
        if (wts.Count == 0) return R(CheckStatus.Unverified, null, "Overhive does not know which worktree this agent works in", null, null);
        var f = FindFile(s, c.Path, wts);
        var deleted = c.Kind == ClaimKind.FileDeleted;
        if (f is not null)
        {
            var git = f.GitStatus;
            var gitDeleted = git == "D";
            var reportedDeleted = f.LastChangeKind == "deleted";
            if (deleted)
            {
                if (gitDeleted) return R(CheckStatus.Confirmed, Evidence.Observed, $"git status: {f.Path} deleted", f.LastChangedAt, null);
                if (reportedDeleted) return R(CheckStatus.Confirmed, Evidence.Reported, $"Deletion of {f.Path} reported by the tool", f.LastChangedAt, null);
                if (c.Settled && c.Exists == true) return R(CheckStatus.Contradicted, Evidence.Observed, $"{f.Path} still exists", null, null);
                return R(CheckStatus.Unverified, Evidence.Observed, $"{f.Path} changed, but no deletion was seen", f.LastChangedAt, null);
            }
            if (git is not null and not "clean" and not "D")
                return R(CheckStatus.Confirmed, Evidence.Observed, $"git status: {f.Path} {GitWord(git)}{Lines(f)}", f.LastChangedAt, null);
            if (gitDeleted) return R(CheckStatus.Unverified, Evidence.Observed, $"git status shows {f.Path} deleted", f.LastChangedAt, null);
            if (f.Agents.Count > 0)
            {
                var ev = f.Agents.Values.Min(x => x.Evidence);
                return R(CheckStatus.Confirmed, ev, $"Change to {f.Path} seen{(git == "clean" ? " (now committed or reverted)" : "")}", f.LastChangedAt, null);
            }
            if (git == "clean") return R(CheckStatus.Unverified, Evidence.Observed, $"git status shows {f.Path} unchanged", f.LastChangedAt, null);
        }
        if (!c.Settled) return R(CheckStatus.Unverified, null, "Waiting for Git to show the change", null, null);
        if (c.Exists == false)
            return R(CheckStatus.Contradicted, Evidence.Observed,
                deleted ? "The file does not exist, but no deletion was seen (it may never have existed)" : $"No change to this path was seen and the file does not exist in {Where(s, wts)}",
                null, null);
        if (deleted) return R(CheckStatus.Contradicted, Evidence.Observed, "The file still exists", null, null);
        return R(CheckStatus.Unverified, c.Exists == true ? Evidence.Observed : null,
            c.Exists == true ? "The file exists, but Git showed no change to it (it may be ignored or unchanged)" : "No change to this path was seen", null, null);
    }

    /// <summary>
    /// Review hint (inferred): within the agent's session a test run failed, test files were then
    /// changed, and a later run passed — while non-test source files were also changed.
    /// </summary>
    public static VerificationFlag? TestChangeFlag(IReadOnlyList<CheckRun> runs, IReadOnlyList<EditMark> edits)
    {
        if (!edits.Any(x => !x.IsTest && !VerificationRules.IsDocFile(x.Path))) return null;
        var tests = runs.Where(r => r.IsTest && r.Outcome != "interrupted").OrderBy(r => r.EndedAt).ToList();
        for (var i = tests.Count - 1; i > 0; i--)
        {
            var pass = tests[i];
            if (pass.Outcome != "passed") continue;
            var fail = tests.Take(i).LastOrDefault(r => r.Outcome == "failed");
            if (fail is null) continue;
            var until = pass.StartedAt ?? pass.EndedAt;
            var changed = edits.Where(x => x.IsTest && x.At > fail.EndedAt && x.At <= until).Select(x => x.Path).Distinct().ToList();
            if (changed.Count == 0) continue;
            return new VerificationFlag("tests_changed",
                $"Tests went from failing to passing after test files were changed, in a session that also changed source code. Review the test diff to make sure the tests still check the intended behaviour.",
                Evidence.Inferred, changed.Take(10).ToList(), fail.EndedAt, pass.EndedAt);
        }
        return null;
    }

    private static FileChange? FindFile(WorldState s, string path, HashSet<string> wts)
    {
        var abs = PathText.IsAbsolute(path);
        if (!abs) path = PathText.Slashes(path);
        foreach (var w in wts)
        {
            var rel = abs ? PathText.Relative(s.Worktrees[w].Root, path) : path;
            if (rel is not null && s.Files.TryGetValue(Ids.FileKey(w, rel), out var f)) return f;
        }
        if (abs) return null;
        // A path relative to a sub-directory: match by suffix within the agent's worktrees.
        var suffix = "/" + path;
        foreach (var w in wts)
            if (s.Index.FilesByWorktree.TryGetValue(w, out var keys))
                foreach (var k in keys)
                    if (s.Files.TryGetValue(k, out var f) && f.Path.EndsWith(suffix, StringComparison.Ordinal)) return f;
        return null;
    }

    private static string Exit(CheckRun r) => r.ExitCode is { } x ? $"exited {x}" : r.Outcome == "failed" ? "failed" : "succeeded";

    private static string Counts(TestRun t)
    {
        var parts = new List<string>();
        if (t.Passed is { } p) parts.Add($"{p} passed");
        if (t.Failed is { } f) parts.Add($"{f} failed");
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    private static string GitWord(string status) => status switch
    {
        "A" => "added", "M" => "modified", "R" => "renamed", "U" => "unmerged", "?" => "new (untracked)", _ => status,
    };

    private static string Lines(FileChange f) => f.Added is null && f.Removed is null ? "" : $" (+{f.Added ?? 0} −{f.Removed ?? 0})";

    private static string Where(WorldState s, HashSet<string> wts)
    {
        var names = wts.Select(w => s.Worktrees[w]).Select(w => w.Branch ?? Path.GetFileName(w.Root.TrimEnd('/'))).Distinct().Take(3).ToList();
        return (names.Count == 1 ? "worktree " : "worktrees ") + string.Join(", ", names);
    }
}
