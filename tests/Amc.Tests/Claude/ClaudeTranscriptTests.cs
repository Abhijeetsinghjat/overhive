using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Providers.Claude;

namespace Amc.Tests.Claude;

public sealed class ClaudeTranscriptTests : IDisposable
{
    private readonly ClaudeHarness _h = new();
    private const string S = ClaudeHarness.SessionId;
    private static readonly string Main = Ids.MainAgent("claude", S);
    private static readonly string A100 = Ids.SubAgent("claude", S, "a100");
    private static readonly string A200 = Ids.SubAgent("claude", S, "a200");

    public void Dispose() => _h.Dispose();

    private async Task<TranscriptIngestor> IngestFixtures()
    {
        _h.CopyFixtures();
        var ing = _h.Ingestor();
        await ing.ScanAllAsync(CancellationToken.None);
        return ing;
    }

    [Fact]
    public async Task Prompts_skip_meta_and_tool_results_and_use_prompt_ids()
    {
        await IngestFixtures();
        var prompts = _h.Sink.OfType(EventTypes.AgentPrompt).Where(e => e.AgentId == Main).ToList();
        Assert.Equal(["Add a greeting feature and run the tests", "Thanks, ship it"], prompts.Select(p => p.Payload<AgentTextData>()!.Text));
        Assert.Equal($"prompt:{S}:p-1", prompts[0].DedupeKey);
        Assert.Equal($"prompt:{S}:p-2", prompts[1].DedupeKey);
        Assert.Equal(EventSources.Transcript, prompts[0].Source);
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T10:00:00Z"), prompts[0].Ts);
    }

    [Fact]
    public async Task Thinking_is_never_ingested()
    {
        await IngestFixtures();
        Assert.DoesNotContain(_h.Sink.All, e => e.Data.ToJsonString().Contains("SECRET-THINKING"));
        var outputs = _h.Sink.OfType(EventTypes.AgentOutput).Where(e => e.AgentId == Main).Select(e => e.Payload<AgentTextData>()!.Text);
        Assert.Equal(["I'll plan this first.", "All done."], outputs);
    }

    [Fact]
    public async Task Tools_files_tests_and_todos_are_mapped()
    {
        await IngestFixtures();
        var s = _h.Sink;
        Assert.Contains(s.Stored, e => e.Type == EventTypes.ToolStarted && e.DedupeKey == "tool:toolu_write:start");
        Assert.Contains(s.Stored, e => e.Type == EventTypes.ToolCompleted && e.DedupeKey == "tool:toolu_write:end");

        var created = Assert.Single(s.OfType(EventTypes.FileCreated));
        Assert.Equal("src/Greeting.cs", created.FilePath);
        Assert.Equal(FakeWorktreeIndex.WtId("/work/repo"), created.WorktreeId);
        Assert.Equal("repo:repo-1", created.RepositoryId);
        Assert.Equal("file:toolu_write", created.DedupeKey);
        Assert.Equal(3, created.Payload<FileChangeData>()!.Added);

        var modified = Assert.Single(s.OfType(EventTypes.FileModified));
        var fc = modified.Payload<FileChangeData>()!;
        Assert.Equal("src/Program.cs", modified.FilePath);
        Assert.Equal((2, 1), (fc.Added!.Value, fc.Removed!.Value));
        Assert.StartsWith("@@ -1,3 +1,4 @@\n x\n-a();\n+b();\n+c();\n y", fc.Patch);

        Assert.Contains(s.OfType(EventTypes.FileRead), e => e.FilePath == "README.md" && e.AgentId == Main);
        Assert.Contains(s.OfType(EventTypes.FileRead), e => e.FilePath == "src/Greeting.cs" && e.AgentId == A100
            && e.WorktreeId == FakeWorktreeIndex.WtId("/work/wt-feature"));

        var cmd = Assert.Single(s.OfType(EventTypes.CommandStarted), e => e.DedupeKey == "tool:toolu_test:start");
        Assert.Equal(Evidence.Inferred, cmd.Evidence); // worktree from `cd` heuristics
        Assert.Equal(FakeWorktreeIndex.WtId("/work/repo"), cmd.WorktreeId);
        Assert.Single(s.OfType(EventTypes.CommandCompleted), e => e.Payload<CommandData>()!.ExitCode == 0 && e.Payload<CommandData>()!.DurationMs == 4000);
        Assert.Single(s.OfType(EventTypes.TestStarted), e => e.DedupeKey == "test:toolu_test:start");
        var passed = Assert.Single(s.OfType(EventTypes.TestPassed));
        Assert.Equal((12, 0, 1), (passed.Payload<TestRunData>()!.Passed!.Value, passed.Payload<TestRunData>()!.Failed!.Value, passed.Payload<TestRunData>()!.Skipped!.Value));
        Assert.Equal("test:toolu_test:end", passed.DedupeKey);

        var failed = Assert.Single(s.OfType(EventTypes.CommandFailed));
        Assert.Equal(A200, failed.AgentId);
        Assert.Equal(1, failed.Payload<CommandData>()!.ExitCode);
        Assert.Equal(FakeWorktreeIndex.WtId("/work/wt-feature"), failed.WorktreeId); // git -C, not the record cwd

        var todo = Assert.Single(s.OfType(EventTypes.TasksUpdated)).Payload<TaskListData>()!;
        Assert.Equal([("Write greeting", TaskState.InProgress, "Writing greeting"), ("Run tests", TaskState.Pending, "Running tests")],
            todo.Tasks.Select(t => (t.Title, t.State, t.ActiveForm!)));
    }

    [Fact]
    public async Task Title_branch_and_model_updates()
    {
        await IngestFixtures();
        var state = _h.Sink.Fold();
        var main = state.Agents[Main];
        Assert.Equal("Greeting feature", main.Title);
        Assert.Equal("feature/greeting", main.GitBranch);
        Assert.Equal("claude-opus-5-5", main.Model);
        Assert.Equal("/work/repo", main.Cwd);
        Assert.EndsWith(S + ".jsonl", main.TranscriptPath);
    }

    [Fact]
    public async Task Sub_agent_tree_depth_two_with_completion()
    {
        await IngestFixtures();
        var spawns = _h.Sink.OfType(EventTypes.AgentSpawned);
        var s1 = Assert.Single(spawns, e => e.AgentId == A100).Payload<AgentCreatedData>()!;
        Assert.Equal((Main, "Explore", "Review greeting", "toolu_spawn1"), (s1.ParentId!, s1.AgentType!, s1.Title!, s1.SpawnToolUseId!));
        var s2 = Assert.Single(spawns, e => e.AgentId == A200).Payload<AgentCreatedData>()!;
        Assert.Equal((A100, "general-purpose", "toolu_spawn2"), (s2.ParentId!, s2.AgentType!, s2.SpawnToolUseId!));
        Assert.Contains(spawns, e => e.DedupeKey == $"spawn:{A100}");

        var state = _h.Sink.Fold();
        Assert.Equal([A100], state.Agents[Main].ChildIds);
        Assert.Equal([A200], state.Agents[A100].ChildIds);
        Assert.Equal(AgentStatus.Completed, state.Agents[A100].Status);
        Assert.Equal(AgentStatus.Completed, state.Agents[A200].Status);
        Assert.Equal(AgentKind.Sub, state.Agents[A200].Kind);
        Assert.Contains(_h.Sink.Stored, e => e.Type == EventTypes.AgentCompleted && e.DedupeKey == $"end:{A200}");
        Assert.Equal("Review the greeting feature", state.Agents[A100].LastPrompt);
        Assert.Equal(1, state.Agents[A200].ToolFailures);
    }

    [Fact]
    public async Task Restart_with_persisted_offsets_does_not_reingest()
    {
        await IngestFixtures();
        var count = _h.Sink.All.Count;
        await _h.Ingestor().ScanAllAsync(CancellationToken.None); // fresh ingestor, same cursor store
        Assert.Equal(count, _h.Sink.All.Count);
    }

    [Fact]
    public async Task Partial_trailing_line_is_read_once_complete()
    {
        _h.CopyFixtures();
        var ing = _h.Ingestor();
        await ing.ScanAllAsync(CancellationToken.None);
        var line = """{"type":"assistant","uuid":"late-1","timestamp":"2026-10-07T10:05:00Z","sessionId":"S","cwd":"/work/repo","message":{"role":"assistant","content":[{"type":"text","text":"late reply"}]}}""".Replace("\"S\"", $"\"{S}\"");
        await File.AppendAllTextAsync(_h.MainTranscript, line[..40]);
        await ing.ScanAllAsync(CancellationToken.None);
        Assert.DoesNotContain(_h.Sink.All, e => e.DedupeKey == "out:late-1:0");
        await File.AppendAllTextAsync(_h.MainTranscript, line[40..] + "\n");
        await ing.ScanAllAsync(CancellationToken.None);
        Assert.Single(_h.Sink.All, e => e.DedupeKey == "out:late-1:0");
    }

    [Fact]
    public async Task Truncated_file_is_read_from_the_start()
    {
        _h.CopyFixtures();
        var ing = _h.Ingestor();
        await ing.ScanAllAsync(CancellationToken.None);
        var line = $$$"""{"type":"user","uuid":"new-1","promptId":"p-9","timestamp":"2026-10-07T11:00:00Z","sessionId":"{{{S}}}","cwd":"/work/repo","message":{"role":"user","content":"after rotation"}}""";
        await File.WriteAllTextAsync(_h.MainTranscript, line + "\n");
        await ing.ScanAllAsync(CancellationToken.None);
        Assert.Single(_h.Sink.Stored, e => e.DedupeKey == $"prompt:{S}:p-9");
    }

    [Fact]
    public async Task Files_older_than_backfill_window_are_skipped_but_tailed()
    {
        _h.CopyFixtures();
        foreach (var f in Directory.EnumerateFiles(_h.ProjectsDir, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-3));
        var ing = _h.Ingestor();
        await ing.ScanAllAsync(CancellationToken.None);
        Assert.Empty(_h.Sink.All);
        var line = $$$"""{"type":"user","uuid":"new-2","promptId":"p-10","timestamp":"2026-10-07T11:00:00Z","sessionId":"{{{S}}}","cwd":"/work/repo","message":{"role":"user","content":"new work"}}""";
        await File.AppendAllTextAsync(_h.MainTranscript, line + "\n");
        await ing.ScanAllAsync(CancellationToken.None);
        Assert.Contains(_h.Sink.Stored, e => e.Type == EventTypes.AgentCreated && e.AgentId == Main);
        Assert.Single(_h.Sink.Stored, e => e.Type == EventTypes.AgentPrompt);
    }

    [Fact]
    public async Task Async_agent_completes_via_task_notification()
    {
        var dir = Path.Combine(_h.ProjectsDir, "-work-repo");
        Directory.CreateDirectory(Path.Combine(dir, S, "subagents"));
        string Rec(string json) => json.Replace("SID", S);
        await File.WriteAllLinesAsync(Path.Combine(dir, S + ".jsonl"), [
            Rec("""{"type":"assistant","uuid":"x1","timestamp":"2026-10-07T10:00:00Z","sessionId":"SID","cwd":"/work/repo","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_bg","name":"Agent","input":{"description":"bg","subagent_type":"Explore","prompt":"go","run_in_background":true}}]}}"""),
            Rec("""{"type":"user","uuid":"x2","timestamp":"2026-10-07T10:00:01Z","sessionId":"SID","cwd":"/work/repo","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_bg","content":"launched"}]},"toolUseResult":{"status":"async_launched","agentId":"abg","isAsync":true}}"""),
            Rec("""{"type":"user","uuid":"x3","timestamp":"2026-10-07T10:05:00Z","sessionId":"SID","cwd":"/work/repo","origin":{"kind":"task-notification","producer":"session-task"},"message":{"role":"user","content":"<task-notification>\n<task-id>abg</task-id>\n<tool-use-id>toolu_bg</tool-use-id>\n<status>completed</status>\n<summary>done</summary>\n</task-notification>"}}"""),
        ]);
        await File.WriteAllTextAsync(Path.Combine(dir, S, "subagents", "agent-abg.meta.json"), """{"agentType":"Explore","description":"bg","toolUseId":"toolu_bg","spawnDepth":1}""");
        await File.WriteAllTextAsync(Path.Combine(dir, S, "subagents", "agent-abg.jsonl"),
            Rec("""{"type":"user","uuid":"y1","agentId":"abg","timestamp":"2026-10-07T10:00:02Z","sessionId":"SID","cwd":"/work/repo","message":{"role":"user","content":"go"}}""") + "\n");
        await _h.Ingestor().ScanAllAsync(CancellationToken.None);
        var child = Ids.SubAgent("claude", S, "abg");
        var state = _h.Sink.Fold();
        Assert.Equal(AgentStatus.Completed, state.Agents[child].Status);
        Assert.Equal("Background sub-agent completed", state.Agents[child].StatusReason);
        Assert.DoesNotContain(_h.Sink.Stored, e => e.Type == EventTypes.AgentPrompt && e.AgentId == Main); // notification is not a user prompt
    }

    [Fact]
    public void Prompt_cleaning()
    {
        Assert.Equal("/review 12", TranscriptIngestor.CleanPrompt("<command-name>/review</command-name>\n<command-args>12</command-args>"));
        Assert.Null(TranscriptIngestor.CleanPrompt("<local-command-stdout>ok</local-command-stdout>"));
        Assert.Equal("!ls", TranscriptIngestor.CleanPrompt("<bash-input>ls</bash-input>"));
        Assert.Equal("hello", TranscriptIngestor.CleanPrompt("  hello "));
    }

    [Fact]
    public void Bash_path_candidates()
    {
        var c = ClaudeEventMapper.BashPathCandidates("cd /work/repo/src && git -C /work/wt-feature status; cat /dev/null /work/x.txt", "/home/u").ToList();
        Assert.Equal(["/work/repo/src", "/work/wt-feature", "/work/x.txt", "/home/u"], c);
        Assert.Equal(["/home/u/sub", "/home/u"], ClaudeEventMapper.BashPathCandidates("cd sub && make", "/home/u").ToList());
    }
}
