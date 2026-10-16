using Amc.Core.Events;
using Amc.Core.State;
using Amc.Core.Verification;
using Amc.Daemon.Verification;

namespace Amc.Tests.Core;

public class ClaimExtractorTests
{
    [Theory]
    [InlineData("All tests pass.")]
    [InlineData("The tests are passing now.")]
    [InlineData("✅ All 42 tests pass")]
    [InlineData("I ran the suite: 128 passed, 0 failed.")]
    [InlineData("Result: all unit tests passed")]
    [InlineData("The test suite is green.")]
    [InlineData("After the fix, the failing tests now pass.")]
    public void Detects_tests_pass(string text) =>
        Assert.Contains(ClaimExtractor.Extract(text), c => c.Kind == ClaimKind.TestsPass);

    [Theory]
    [InlineData("Tests may pass once the fixture is updated.")]
    [InlineData("If the tests pass, I'll commit.")]
    [InlineData("Once tests pass we can merge.")]
    [InlineData("Not all tests pass yet.")]
    [InlineData("Make sure all tests pass before merging.")]
    [InlineData("Do the tests pass?")]
    [InlineData("All tests pass except the flaky integration one.")]
    [InlineData("10 tests passed, 2 failed.")]
    [InlineData("The tests should pass now.")]
    [InlineData("I'll run the tests to check they pass.")]
    [InlineData("```\n$ npm test\nall tests pass\n```")]
    public void Ignores_hedged_or_qualified_test_statements(string text) =>
        Assert.DoesNotContain(ClaimExtractor.Extract(text), c => c.Kind == ClaimKind.TestsPass);

    [Theory]
    [InlineData("The build succeeds.", true)]
    [InlineData("It builds cleanly with no warnings.", true)]
    [InlineData("The project compiled successfully.", true)]
    [InlineData("The release build is green.", true)]
    [InlineData("If the build succeeds, deploy it.", false)]
    [InlineData("The build should succeed now.", false)]
    [InlineData("The build fails on CI.", false)]
    [InlineData("Let me check whether it builds cleanly.", false)]
    public void Detects_build_claims_precisely(string text, bool expected) =>
        Assert.Equal(expected, ClaimExtractor.Extract(text).Any(c => c.Kind == ClaimKind.BuildSucceeds));

    [Theory]
    [InlineData("I committed the changes.", true)]
    [InlineData("I've committed everything to the feature branch.", true)]
    [InlineData("Created a commit with the fix.", true)]
    [InlineData("All changes have been committed.", true)]
    [InlineData("Committed as 3f2a1bc.", true)]
    [InlineData("I'll commit once you approve.", false)]
    [InlineData("I haven't committed anything yet.", false)]
    [InlineData("The changes are not committed.", false)]
    [InlineData("There are uncommitted changes in the worktree.", false)]
    [InlineData("Should I commit these changes?", false)]
    public void Detects_commit_claims_precisely(string text, bool expected) =>
        Assert.Equal(expected, ClaimExtractor.Extract(text).Any(c => c.Kind == ClaimKind.Committed));

    [Theory]
    [InlineData("I created src/util/strings.ts with the helper.", ClaimKind.FileCreated, "src/util/strings.ts")]
    [InlineData("Updated `README.md` with the new flag.", ClaimKind.FileUpdated, "README.md")]
    [InlineData("I deleted ./old/legacy.cs.", ClaimKind.FileDeleted, "old/legacy.cs")]
    [InlineData("Added a new file called `tests/strings.test.ts`.", ClaimKind.FileCreated, "tests/strings.test.ts")]
    [InlineData("Modified /home/me/repo/src/App.tsx to use the hook.", ClaimKind.FileUpdated, "/home/me/repo/src/App.tsx")]
    public void Detects_file_claims_with_paths(string text, ClaimKind kind, string path)
    {
        var c = Assert.Single(ClaimExtractor.Extract(text), x => x.Path is not null);
        Assert.Equal(kind, c.Kind);
        Assert.Equal(path, c.Path);
    }

    [Theory]
    [InlineData("I updated Node.js to the latest version.")]
    [InlineData("Added tests for the parser.")]
    [InlineData("Removed the unused import.")]
    [InlineData("I will create src/new.ts next.")]
    [InlineData("I did not modify src/a.ts.")]
    [InlineData("Updated the docs, see https://example.com/a.html for details.")]
    [InlineData("Changed the version to 1.2.3.")]
    public void Ignores_non_paths_and_unclaimed_files(string text) =>
        Assert.DoesNotContain(ClaimExtractor.Extract(text), c => c.Path is not null);

    [Fact]
    public void Excerpt_is_redacted_and_capped()
    {
        var c = Assert.Single(ClaimExtractor.Extract("All tests pass with token=supersecretvalue123 " + new string('x', 400)));
        Assert.DoesNotContain("supersecretvalue123", c.Excerpt);
        Assert.True(c.Excerpt.Length <= ClaimExtractor.MaxExcerpt + 1);
    }

    [Fact]
    public void One_claim_per_kind_per_text()
    {
        var claims = ClaimExtractor.Extract("All tests pass. I committed the changes. Tests are passing.");
        Assert.Equal(2, claims.Count);
    }

    [Theory]
    [InlineData("tests/foo.cs", true)] [InlineData("src/__tests__/a.ts", true)] [InlineData("src/a.test.ts", true)]
    [InlineData("pkg/a_test.go", true)] [InlineData("test_a.py", true)] [InlineData("Amc.Tests/ReducerTests.cs", true)]
    [InlineData("src/latest.ts", false)] [InlineData("src/Reducer.cs", false)] [InlineData("contest/a.go", false)]
    public void Recognizes_test_files(string path, bool expected) => Assert.Equal(expected, VerificationRules.IsTestFile(path));

    [Theory]
    [InlineData("dotnet build -c Release", true)] [InlineData("npm run build", true)] [InlineData("cd x && make", true)]
    [InlineData("npx tsc --noEmit", true)] [InlineData("cargo build --release", true)] [InlineData("make test", false)]
    [InlineData("cmake .", false)] [InlineData("npm test", false)] [InlineData("go vet ./...", false)]
    public void Recognizes_build_commands(string command, bool expected) => Assert.Equal(expected, VerificationRules.IsBuildCommand(command));
}

public class VerifierTests
{
    private readonly EventFactory _f = new();
    private readonly WorldState _s = new();
    private int _tool;

    public VerifierTests()
    {
        Apply(_f.Main("m"), _f.Repo("r"), _f.Worktree("w", "/r", "r"),
            _f.Make(EventTypes.ToolStarted, new ToolCallData("t0", "Read"), "m", wt: "w"));
    }

    private HashSet<EntityRef> Apply(params AgentEvent[] events) => Reducer.ApplyBatch(_s, events);

    private AgentEvent[] Run(string command, int exit, string agent = "m", bool viaToolEnd = false)
    {
        var id = "cmd" + ++_tool;
        var start = _f.Make(EventTypes.CommandStarted, new CommandData(id, command), agent, wt: "w");
        var end = viaToolEnd
            ? _f.Make(exit == 0 ? EventTypes.ToolCompleted : EventTypes.ToolFailed, new ToolCallData(id, "shell"), agent)
            : _f.Make(exit == 0 ? EventTypes.CommandCompleted : EventTypes.CommandFailed, new CommandData(id, command, ExitCode: exit), agent, wt: "w");
        return [start, end];
    }

    private AgentEvent Say(string text, string agent = "m") => _f.Make(EventTypes.AgentOutput, new AgentTextData(text), agent);

    private AgentEvent Settle(string agent, string claimId, bool? exists = null) =>
        _f.Make(EventTypes.VerificationSettled, new VerificationSettledData(claimId, exists), agent, evidence: Evidence.Observed);

    private VerificationCheck Only(string agent = "m") => Assert.Single(Verifier.Evaluate(_s, agent).Checks);

    [Fact]
    public void Tests_pass_claim_is_confirmed_by_a_passing_run()
    {
        Apply([.. Run("npm test", 0), Say("All tests pass.")]);
        var c = Only();
        Assert.Equal(CheckStatus.Confirmed, c.Status);
        Assert.Equal(Evidence.Reported, c.Evidence);
        Assert.Contains("`npm test` exited 0", c.Observation);
        Assert.Equal(new VerificationSummary(1, 0, 0, 0), _s.Agents["m"].Verification);
    }

    [Fact]
    public void Tests_pass_claim_is_contradicted_by_a_failing_last_run()
    {
        Apply([.. Run("npm test", 0), .. Run("npm test", 1), Say("All tests pass.")]);
        var c = Only();
        Assert.Equal(CheckStatus.Contradicted, c.Status);
        Assert.Contains("exited 1", c.Observation);
        Assert.Equal(1, _s.Agents["m"].Verification!.Contradicted);
    }

    [Fact]
    public void Tests_pass_claim_without_any_run_is_unverified()
    {
        Apply(Say("All tests pass."));
        var c = Only();
        Assert.Equal(CheckStatus.Unverified, c.Status);
        Assert.Null(c.Evidence);
        Assert.Contains("No test run", c.Observation);
    }

    [Fact]
    public void Runs_after_the_claim_do_not_count()
    {
        Apply(Say("All tests pass."));
        Apply(Run("npm test", 1));
        Assert.Equal(CheckStatus.Unverified, Only().Status);
    }

    [Fact]
    public void Code_changed_after_the_passing_run_leaves_the_claim_unverified()
    {
        Apply([.. Run("dotnet test", 0), _f.Edit("m", "w", "src/A.cs"), Say("Tests are passing.")]);
        var c = Only();
        Assert.Equal(CheckStatus.Unverified, c.Status);
        Assert.Contains("src/A.cs", c.Observation);
    }

    [Fact]
    public void Sub_agent_runs_count_for_the_parent_claim()
    {
        Apply([_f.Sub("s", "m"), .. Run("pytest -q", 1, agent: "s"), Say("All tests pass.")]);
        Assert.Equal(CheckStatus.Contradicted, Only().Status);
        Assert.Equal(1, _s.Agents["m"].Verification!.Contradicted);
    }

    [Fact]
    public void Parsed_failures_contradict_even_with_exit_code_zero()
    {
        var run = Run("npm test", 0);
        Apply([run[0], run[1],
            _f.Make(EventTypes.TestFailed, new TestRunData("cmd1", "js", "npm test", Passed: 10, Failed: 2), "m", evidence: Evidence.Inferred),
            Say("All tests pass.")]);
        var c = Only();
        Assert.Equal(CheckStatus.Contradicted, c.Status);
        Assert.Equal(Evidence.Inferred, c.Evidence);
    }

    [Fact]
    public void Protocol_tool_end_without_command_still_records_the_run()
    {
        Apply([.. Run("go test ./...", 1, viaToolEnd: true), Say("All tests pass.")]);
        Assert.Equal(CheckStatus.Contradicted, Only().Status);
    }

    [Fact]
    public void Build_claim_uses_build_commands_and_is_cautious_with_combined_commands()
    {
        Apply([.. Run("dotnet build", 0), Say("The build succeeds.")]);
        Assert.Equal(CheckStatus.Confirmed, Only().Status);

        Apply([.. Run("dotnet build && dotnet test", 1), Say("The build succeeded.")]);
        var last = Verifier.Evaluate(_s, "m").Checks[^1];
        Assert.Equal(CheckStatus.Unverified, last.Status);
    }

    [Fact]
    public void Commit_claim_confirmed_by_observed_commit_in_its_worktree()
    {
        Apply(_f.Make(EventTypes.GitCommitCreated, new GitCommitData("abcdef123456", "Fix parser", null, null, "main"), wt: "w", evidence: Evidence.Observed),
            Say("I committed the changes."));
        var c = Only();
        Assert.Equal(CheckStatus.Confirmed, c.Status);
        Assert.Equal(Evidence.Observed, c.Evidence);
        Assert.Contains("abcdef1", c.Observation);
    }

    [Fact]
    public void Commit_claim_waits_for_settle_then_is_contradicted()
    {
        Apply(Say("I committed the changes."));
        var c = Only();
        Assert.Equal(CheckStatus.Unverified, c.Status);
        Apply(Settle("m", c.Id));
        Assert.Equal(CheckStatus.Contradicted, Only().Status);
        Assert.Equal(1, _s.Agents["m"].Verification!.Contradicted);

        // A late Git refresh that does show the commit corrects it.
        Apply(_f.Make(EventTypes.GitCommitCreated, new GitCommitData("0123456789ab", "wip", null, c.ClaimedAt, "main"), wt: "w", evidence: Evidence.Observed));
        Assert.Equal(CheckStatus.Confirmed, Only().Status);
        Assert.Equal(0, _s.Agents["m"].Verification!.Contradicted);
    }

    [Fact]
    public void Commit_attributed_to_another_agent_does_not_confirm()
    {
        Apply(_f.Main("other"),
            _f.Make(EventTypes.GitCommitCreated, new GitCommitData("abcdef123456", "theirs", null, null, "main"), "other", wt: "w", evidence: Evidence.Observed),
            Say("I committed the changes."));
        Assert.Equal(CheckStatus.Unverified, Only().Status);
    }

    [Fact]
    public void File_claim_confirmed_by_git_status()
    {
        Apply(_f.GitStatus("w", "src/new.ts", "?"), Say("I created src/new.ts."));
        var c = Only();
        Assert.Equal(CheckStatus.Confirmed, c.Status);
        Assert.Equal(Evidence.Observed, c.Evidence);
    }

    [Fact]
    public void File_claim_matches_paths_relative_to_a_subdirectory()
    {
        Apply(_f.Edit("m", "w", "web/src/app.ts"), Say("Updated `src/app.ts`."));
        Assert.Equal(CheckStatus.Confirmed, Only().Status);
    }

    [Fact]
    public void File_claim_contradicted_when_unchanged_and_absent()
    {
        Apply(Say("I created src/missing.ts."));
        var c = Only();
        Assert.Equal(CheckStatus.Unverified, c.Status);
        Apply(Settle("m", c.Id, exists: false));
        Assert.Equal(CheckStatus.Contradicted, Only().Status);
    }

    [Fact]
    public void File_claim_present_but_unchanged_stays_unverified()
    {
        Apply(Say("Updated src/same.ts."));
        Apply(Settle("m", Only().Id, exists: true));
        Assert.Equal(CheckStatus.Unverified, Only().Status);
    }

    [Fact]
    public void Delete_claim_confirmed_by_git_and_contradicted_when_file_remains()
    {
        Apply(_f.GitStatus("w", "old.cs", "D"), Say("I deleted old.cs."));
        Assert.Equal(CheckStatus.Confirmed, Only().Status);

        var s2 = new VerifierTests();
        s2.Apply(s2.Say("I removed legacy/x.cs."));
        s2.Apply(s2.Settle("m", s2.Only().Id, exists: true));
        Assert.Equal(CheckStatus.Contradicted, s2.Only().Status);
    }

    [Fact]
    public void Flags_tests_changed_between_failing_and_passing_runs()
    {
        Apply([_f.Edit("m", "w", "src/Parser.cs"), .. Run("dotnet test", 1),
            _f.Edit("m", "w", "tests/ParserTests.cs"), .. Run("dotnet test", 0)]);
        var flag = Assert.Single(Verifier.Evaluate(_s, "m").Flags);
        Assert.Equal("tests_changed", flag.Kind);
        Assert.Equal(Evidence.Inferred, flag.Evidence);
        Assert.Equal(["tests/ParserTests.cs"], flag.Files);
        Assert.Equal(1, _s.Agents["m"].Verification!.Flags);
    }

    [Fact]
    public void No_flag_when_tests_changed_before_the_failure_or_no_source_changed()
    {
        Apply([_f.Edit("m", "w", "src/Parser.cs"), _f.Edit("m", "w", "tests/ParserTests.cs"), .. Run("dotnet test", 1),
            _f.Edit("m", "w", "src/Parser.cs"), .. Run("dotnet test", 0)]);
        Assert.Empty(Verifier.Evaluate(_s, "m").Flags);

        var only = new VerifierTests();
        only.Apply([.. only.Run("npm test", 1), only._f.Edit("m", "w", "src/a.test.ts"), .. only.Run("npm test", 0)]);
        Assert.Empty(Verifier.Evaluate(only._s, "m").Flags); // only tests changed: nothing to hide
    }

    [Fact]
    public void Flag_spans_sub_agents_of_the_session()
    {
        Apply([_f.Sub("s", "m"), _f.Edit("s", "w", "lib/core.go"), .. Run("go test ./...", 1, agent: "s"),
            _f.Edit("m", "w", "lib/core_test.go"), .. Run("go test ./...", 0, agent: "s")]);
        Assert.Single(Verifier.Evaluate(_s, "m").Flags);
        Assert.Empty(Verifier.Evaluate(_s, "s").Flags); // the sub-agent alone did not change tests
    }

    [Fact]
    public void Same_result_whether_events_arrive_in_one_batch_or_many()
    {
        var f = new EventFactory();
        var events = new List<AgentEvent>
        {
            f.Main("m"), f.Repo("r"), f.Worktree("w", "/r", "r"), f.Sub("s", "m"),
            f.Make(EventTypes.CommandStarted, new CommandData("c1", "npm test"), "s", wt: "w"),
            f.Make(EventTypes.CommandFailed, new CommandData("c1", "npm test", ExitCode: 1), "s", wt: "w"),
            f.Edit("s", "w", "src/a.ts"), f.Edit("m", "w", "src/a.test.ts"),
            f.Make(EventTypes.CommandStarted, new CommandData("c2", "npm test"), "s", wt: "w"),
            f.Make(EventTypes.CommandCompleted, new CommandData("c2", "npm test", ExitCode: 0), "s", wt: "w"),
            f.Make(EventTypes.AgentOutput, new AgentTextData("All tests pass. I committed the changes. Created src/b.ts."), "m"),
            f.GitStatus("w", "src/b.ts", "?"),
        };
        var one = new WorldState();
        Reducer.ApplyBatch(one, events);
        var many = new WorldState();
        foreach (var e in events) Reducer.ApplyBatch(many, [e]);
        Assert.Equal(one.Agents["m"].Verification, many.Agents["m"].Verification);
        Assert.Equal(Json.Serialize(Verifier.Evaluate(one, "m")), Json.Serialize(Verifier.Evaluate(many, "m")));
        Assert.Equal(new VerificationSummary(2, 0, 1, 1), one.Agents["m"].Verification);
    }

    [Fact]
    public void Survives_a_snapshot_round_trip()
    {
        Apply([.. Run("npm test", 1), _f.Edit("m", "w", "src/a.ts"), Say("All tests pass. I created src/a.ts.")]);
        Apply(Settle("m", Verifier.Evaluate(_s, "m").Checks[1].Id, exists: true));
        var restored = Json.Deserialize<WorldState>(Json.Serialize(_s))!;
        Assert.Equal(Json.Serialize(Verifier.Evaluate(_s, "m")), Json.Serialize(Verifier.Evaluate(restored, "m")));
        Assert.Equal(_s.Agents["m"].Verification, restored.Agents["m"].Verification);
    }

    [Fact]
    public void Monitor_settles_due_claims_and_probes_only_inside_worktrees()
    {
        var root = Path.Combine(Path.GetTempPath(), "amc-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            File.WriteAllText(Path.Combine(root, "src", "here.ts"), "");
            var f = new EventFactory();
            var s = new WorldState();
            Reducer.ApplyBatch(s, [f.Main("m"), f.Repo("r"), f.Worktree("w", root, "r"),
                f.Make(EventTypes.ToolStarted, new ToolCallData("t0", "Read"), "m", wt: "w"),
                f.Make(EventTypes.AgentOutput, new AgentTextData("Created src/here.ts. Created src/gone.ts. All tests pass."), "m")]);

            Assert.Empty(VerificationMonitor.Due(s, f.Now)); // too early
            var due = VerificationMonitor.Due(s, f.Now + VerificationMonitor.SettleAfter + TimeSpan.FromSeconds(1));
            Assert.Equal(2, due.Count); // the test claim needs no settling
            Assert.Equal(new bool?[] { true, false }, due.Select(VerificationMonitor.Exists));

            var outside = due[0] with { Candidates = [Path.Combine(root, "..", "elsewhere.ts")] };
            Assert.Null(VerificationMonitor.Exists(outside));
            Assert.Empty(VerificationMonitor.Due(s, f.Now + VerificationMonitor.MaxAge + TimeSpan.FromMinutes(1)));
        }
        finally { Directory.Delete(root, true); }
    }
}
