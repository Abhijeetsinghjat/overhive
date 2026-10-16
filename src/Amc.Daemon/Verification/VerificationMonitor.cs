using Amc.Core.Events;
using Amc.Core.State;
using Amc.Core.Verification;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;

namespace Amc.Daemon.Verification;

/// <summary>
/// Settles "committed" and file claims once Git has had time to show their effect: after
/// <see cref="SettleAfter"/> it publishes <c>verification.settled</c> (observed), carrying whether a
/// claimed file path exists. Only existence is checked (never contents), only inside worktrees the
/// agent used. The reducer then lets the check say "contradicted" instead of "waiting", and stays pure.
/// </summary>
public sealed class VerificationMonitor(StateStore state, IEventSink sink, HealthRegistry health, TimeProvider clock) : BackgroundService
{
    public const string HealthName = "verification";
    public static readonly TimeSpan SettleAfter = TimeSpan.FromSeconds(60);
    /// <summary>Older claims (e.g. backfilled history) are left unverified: today's filesystem says little about them.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    public sealed record Pending(string AgentId, string? SessionId, string ClaimId, bool IsFile, IReadOnlyList<string> Candidates, IReadOnlyList<string> Roots);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        health.Register(HealthName, Interval * 4);
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            var n = Sweep(clock.GetUtcNow());
            health.Ok(HealthName, n == 0 ? "no claims waiting" : $"{n} claim(s) settled");
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public int Sweep(DateTimeOffset now)
    {
        var due = state.Read(s => Due(s, now));
        foreach (var p in due)
            sink.TryPublish(new AgentEvent
            {
                Type = EventTypes.VerificationSettled, Source = EventSources.System, Evidence = Evidence.Observed,
                AgentId = p.AgentId, SessionId = p.SessionId, Ts = now, DedupeKey = "verify:" + p.ClaimId,
                Data = Json.ToObject(new VerificationSettledData(p.ClaimId, p.IsFile ? Exists(p) : null)),
            });
        return due.Count;
    }

    /// <summary>Unsettled commit/file claims whose settle window has passed, with the paths to probe.</summary>
    public static List<Pending> Due(WorldState s, DateTimeOffset now)
    {
        var list = new List<Pending>();
        foreach (var (agentId, log) in s.Verification)
            foreach (var c in log.Claims)
            {
                if (c.Settled || c.Kind is ClaimKind.TestsPass or ClaimKind.BuildSucceeds) continue;
                if (now - c.At < SettleAfter || now - c.At > MaxAge) continue;
                var roots = Verifier.TreeWorktrees(s, agentId).Select(w => s.Worktrees[w].Root).ToList();
                var candidates = new List<string>();
                if (c.Path is { } path && roots.Count > 0)
                {
                    if (System.IO.Path.IsPathRooted(path)) candidates.Add(path);
                    else
                    {
                        candidates.AddRange(roots.Select(r => System.IO.Path.Combine(r, path)));
                        if (s.Agents.GetValueOrDefault(agentId)?.Cwd is { } cwd && System.IO.Path.IsPathRooted(cwd))
                            candidates.Add(System.IO.Path.Combine(cwd, path));
                    }
                }
                list.Add(new Pending(agentId, s.Agents.GetValueOrDefault(agentId)?.SessionId, c.Id, c.Path is not null, candidates, roots));
            }
        return list;
    }

    /// <summary>True if any candidate inside one of the roots exists; null if there was nothing safe to check.</summary>
    public static bool? Exists(Pending p)
    {
        var checkedAny = false;
        foreach (var candidate in p.Candidates)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(candidate); } catch { continue; }
            if (!p.Roots.Any(r => Inside(full, r))) continue; // never probe outside a worktree the agent used
            checkedAny = true;
            if (File.Exists(full) || Directory.Exists(full)) return true;
        }
        return checkedAny ? false : null;
    }

    private static bool Inside(string full, string root)
    {
        var r = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return full.StartsWith(r, cmp);
    }
}
