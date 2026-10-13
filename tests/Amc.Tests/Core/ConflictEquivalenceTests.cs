using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Tests.Core;

/// <summary>Incremental conflict detection must always equal the full reference detection.</summary>
public class ConflictEquivalenceTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Incremental_matches_full_detection_on_random_histories(int seed)
    {
        var rnd = new Random(seed);
        var f = new EventFactory();
        var s = new WorldState();
        string[] agents = ["m", "a", "b", "c"];
        string[] wts = ["w1", "w2", "w3"];
        string[] paths = ["A.cs", "B.cs", "C.cs", "D.cs"];
        Reducer.ApplyBatch(s, [f.Main("m"), f.Sub("a", "m"), f.Sub("b", "m"), f.Sub("c", "m"), f.Repo("r"),
            f.Worktree("w1", "/r", "r"), f.Worktree("w2", "/wt/2", "r"), f.Worktree("w3", "/wt/3", "r")]);

        for (var batch = 0; batch < 150; batch++)
        {
            var events = new List<AgentEvent>();
            for (var i = 0; i < rnd.Next(1, 6); i++)
            {
                var wt = wts[rnd.Next(wts.Length)];
                var path = paths[rnd.Next(paths.Length)];
                var agent = agents[rnd.Next(agents.Length)];
                events.Add(rnd.Next(7) switch
                {
                    0 or 1 => f.Edit(agent, wt, path),
                    2 => f.GitStatus(wt, path, rnd.Next(3) == 0 ? "clean" : "M"),
                    3 => f.Status(agent, rnd.Next(2) == 0 ? AgentStatus.Completed : AgentStatus.Running),
                    4 => f.Worktree(wt, wt == "w1" ? "/r" : "/wt/" + wt[1], "r", conflicted: rnd.Next(4) == 0 ? 1 : 0),
                    5 => f.Make(EventTypes.WorktreeDeleted, new WorktreeData("/x", "r"), wt: wt, evidence: Evidence.Observed),
                    _ => f.Make(EventTypes.ToolStarted, new ToolCallData("t", "Read"), agent),
                });
                if (rnd.Next(10) == 0) f.Advance(TimeSpan.FromMinutes(rnd.Next(1, 15)));
            }
            Reducer.ApplyBatch(s, events);

            var expected = ConflictDetector.Detect(s).Select(c => (c.Key, string.Join(",", c.AgentIds), string.Join(",", c.WorktreeIds))).Order().ToList();
            var actual = s.Conflicts.Values.Select(c => (c.Key, string.Join(",", c.AgentIds), string.Join(",", c.WorktreeIds))).Order().ToList();
            Assert.Equal(expected, actual);
        }
    }
}
