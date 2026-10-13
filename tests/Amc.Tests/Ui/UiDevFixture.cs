using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;
using Amc.Daemon.Storage;

namespace Amc.Tests.Ui;

/// <summary>
/// Dev-only: writes a SQLite event log with clearly-labelled simulated data (Source = simulator)
/// so the UI can be developed and screenshotted before real collectors exist.
///
///   AMC_FIXTURE_DIR=/tmp/amc-fixture dotnet test --filter Category=DevFixture
///   AMC_DataDir=/tmp/amc-fixture AMC_Port=47900 dotnet run --project src/Amc.Daemon
///
/// Skipped (no-op) when AMC_FIXTURE_DIR is not set.
/// </summary>
public sealed class UiDevFixture
{
    private const string Sim = EventSources.Simulator;

    private readonly List<AgentEvent> _events = [];
    private DateTimeOffset _t;
    private int _tool;

    [Fact]
    [Trait("Category", "DevFixture")]
    public void GenerateUiFixture()
    {
        var dir = Environment.GetEnvironmentVariable("AMC_FIXTURE_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "amc.db*")) File.Delete(f);

        Build();

        using (var store = new EventStore(Path.Combine(dir, "amc.db")))
        {
            var stored = store.Append(_events);
            Assert.Equal(_events.Count, stored.Count);

            // Same fold the daemon performs on startup; sanity-check the fixture covers what the UI needs.
            var state = new WorldState();
            Reducer.ApplyBatch(state, stored);
            Assert.Contains(state.Agents.Values, a => a.Status == AgentStatus.Blocked);
            Assert.Contains(state.Agents.Values, a => a.Status == AgentStatus.Failed);
            Assert.Contains(state.Agents.Values, a => a.Status == AgentStatus.Disconnected);
            Assert.Contains(state.Conflicts.Values, c => c.Kind == ConflictKind.SameFile);
            Assert.Equal(2, state.Repositories.Count);
            Assert.Equal(3, state.Worktrees.Count);
            Assert.Contains(state.Messages.Values, m => m.Status == MessageStatus.Answered);
        }
    }

    // ---- scenario -------------------------------------------------------------

    private const string RepoAmc = "repo:a1b2c3d4e5f6";
    private const string RepoRdz = "repo:0f9e8d7c6b5a";
    private const string WtMain = "wt:111111111111";
    private const string WtUi = "wt:222222222222";
    private const string WtRdz = "wt:333333333333";

    private const string S1 = "c441e0f2-7a1d-4c59-9b0e-5d2f81a0c3aa";
    private const string S2 = "9d0b2a44-1e3f-4b8a-a6c1-77e2f0d9b812";
    private const string S3 = "51f7c3e9-0b2d-4f1a-8c6e-3a9d4b7e2f10";
    private static readonly string Main1 = Ids.MainAgent("claude", S1);
    private static readonly string Sub1 = Ids.SubAgent("claude", S1, "a319f0c2");
    private static readonly string Sub2 = Ids.SubAgent("claude", S1, "b72e11d9");
    private static readonly string Sub3 = Ids.SubAgent("claude", S1, "c05a7e44");   // nested under Sub1
    private static readonly string Sub4 = Ids.SubAgent("claude", S1, "d8812b6f");   // completed
    private static readonly string Main2 = Ids.MainAgent("claude", S2);
    private static readonly string Main3 = Ids.MainAgent("claude", S3);

    private void Build()
    {
        _t = DateTimeOffset.UtcNow.AddMinutes(-42);

        Add(EventTypes.RepositoryDiscovered, null, Evidence.Observed, new RepositoryData("agent-mission-control", "/home/dev/agent-mission-control/.git", "/home/dev/agent-mission-control"), repo: RepoAmc, source: Sim);
        Add(EventTypes.RepositoryDiscovered, null, Evidence.Observed, new RepositoryData("acme-backend", "/home/dev/acme-backend/.git", "/home/dev/acme-backend"), repo: RepoRdz);
        Add(EventTypes.WorktreeCreated, null, Evidence.Observed, new WorktreeData("/home/dev/agent-mission-control", RepoAmc, "main", "43f3aa3e1c9b", IsMain: true), repo: RepoAmc, wt: WtMain);
        Add(EventTypes.WorktreeCreated, null, Evidence.Observed, new WorktreeData("/home/dev/agent-mission-control-worktrees/ui", RepoAmc, "agent/ui", "43f3aa3e1c9b"), repo: RepoAmc, wt: WtUi);
        Add(EventTypes.WorktreeCreated, null, Evidence.Observed, new WorktreeData("/home/dev/acme-backend", RepoRdz, "feature/billing-export", "9f81c2d07ab4", IsMain: true, Upstream: "origin/feature/billing-export", Ahead: 1), repo: RepoRdz, wt: WtRdz);

        // Session 1: main agent orchestrating sub-agents in the agent-mission-control repo.
        Step(5);
        Add(EventTypes.AgentCreated, Main1, Evidence.Reported, new AgentCreatedData(AgentKind.Main, "claude", Title: "Build Overhive UI", Cwd: "/home/dev/agent-mission-control", Pid: 48211, ProcStart: "88123411", Model: "claude-opus-5-5", Version: "2.3.1"), session: S1);
        Add(EventTypes.AgentUpdated, Main1, Evidence.Observed, new AgentUpdatedData(GitBranch: "main", Transports: ["hook-mailbox"]), session: S1);
        Add(EventTypes.AgentPrompt, Main1, Evidence.Reported, new AgentTextData("Build the web UI for Overhive: graph, worktrees & files, activity, timeline, processes, health. Use the dev fixture to verify."), session: S1);
        Add(EventTypes.TasksUpdated, Main1, Evidence.Reported, new TaskListData([
            new("1", "Read architecture and contracts", TaskState.Completed),
            new("2", "Spawn UI and backend sub-agents", TaskState.Completed),
            new("3", "Review sub-agent output", TaskState.InProgress, "Reviewing sub-agent output"),
            new("4", "Merge worktrees", TaskState.Pending),
        ], "TodoWrite"), session: S1);
        Tool(Main1, S1, "Read", "Read docs/ARCHITECTURE.md", WtMain, RepoAmc, "docs/ARCHITECTURE.md", read: true);
        Tool(Main1, S1, "Grep", "Grep \"EntityKinds\" in src/", WtMain, RepoAmc);

        Step(10);
        Add(EventTypes.AgentSpawned, Sub1, Evidence.Reported, new AgentCreatedData(AgentKind.Sub, "claude", Main1, "general-purpose", "Frontend agent", "Implement the web UI in src/Amc.Daemon/wwwroot", SpawnToolUseId: "toolu_01SPAWN1"), session: S1);
        Add(EventTypes.AgentSpawned, Sub2, Evidence.Reported, new AgentCreatedData(AgentKind.Sub, "claude", Main1, "general-purpose", "Process collector", "Implement /proc scanning and live process overlay", SpawnToolUseId: "toolu_01SPAWN2"), session: S1);
        Add(EventTypes.AgentSpawned, Sub4, Evidence.Reported, new AgentCreatedData(AgentKind.Sub, "claude", Main1, "Explore", "Survey hook payloads", "Find all Claude Code hook payload shapes on this machine", SpawnToolUseId: "toolu_01SPAWN4"), session: S1);
        Add(EventTypes.TasksUpdated, Sub1, Evidence.Reported, new TaskListData([
            new("1", "Vendor xterm.js", TaskState.Completed),
            new("2", "Store + WebSocket sync", TaskState.Completed),
            new("3", "Graph and worktree views", TaskState.InProgress, "Building graph and worktree views"),
            new("4", "Detail panel and drawer", TaskState.Pending),
            new("5", "Screenshots", TaskState.Pending),
        ], "TodoWrite"), session: S1);

        Tool(Sub4, S1, "Glob", "Glob ~/.claude/projects/**/*.jsonl", null, null);
        Tool(Sub4, S1, "Read", "Read hook-payloads.jsonl", WtMain, RepoAmc, "docs/hooks.md", read: true);
        Step(30);
        Add(EventTypes.AgentOutput, Sub4, Evidence.Reported, new AgentTextData("Found 9 hook event types; PreToolUse/PostToolUse carry tool_use_id."), session: S1);
        Add(EventTypes.AgentCompleted, Sub4, Evidence.Reported, new AgentStatusData(AgentStatus.Completed, "SubagentStop"), session: S1);

        // Sub1 works in the UI worktree.
        Edit(Sub1, S1, WtUi, RepoAmc, "src/Amc.Daemon/wwwroot/index.html", "created", 40, 0,
            "@@ -0,0 +1,6 @@\n+<!doctype html>\n+<html lang=\"en\" data-theme=\"dark\">\n+<head>\n+  <meta charset=\"utf-8\">\n+  <title>Overhive</title>\n+</head>");
        Edit(Sub1, S1, WtUi, RepoAmc, "src/Amc.Daemon/wwwroot/js/store.js", "created", 212, 0,
            "@@ -0,0 +1,5 @@\n+export class Store {\n+  constructor() {\n+    this.entities = new Map();\n+  }\n+}");
        Edit(Sub1, S1, WtUi, RepoAmc, "src/Amc.Daemon/wwwroot/js/store.js", "modified", 14, 3,
            "@@ -1,5 +1,9 @@\n export class Store {\n   constructor() {\n-    this.entities = new Map();\n+    this.kinds = new Map();\n+    this.seq = 0;\n+    this.listeners = new Set();\n   }\n+  apply(patch) { /* … */ }\n }");
        Cmd(Sub1, S1, WtUi, RepoAmc, "dotnet build src/Amc.Daemon", "Build daemon", 0, "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:00:07.41");
        Edit(Sub1, S1, WtUi, RepoAmc, "src/Amc.Daemon/Program.cs", "modified", 3, 1,
            "@@ -58,7 +58,9 @@ app.MapGet(\"/\", (IWebHostEnvironment env, ApiToken token) =>\n {\n     var path = Path.Combine(env.WebRootPath, \"index.html\");\n-    if (!File.Exists(path)) return Results.Text(\"UI not built\", statusCode: 500);\n+    if (!File.Exists(path))\n+        return Results.Text(\"UI not built: wwwroot/index.html missing\", statusCode: 500);\n+    // token embedded for loopback clients only\n     var html = File.ReadAllText(path).Replace(\"__AMC_TOKEN__\", token.Value);");
        Status(WtUi, RepoAmc, "src/Amc.Daemon/wwwroot/index.html", "A", 40, 0);
        Status(WtUi, RepoAmc, "src/Amc.Daemon/wwwroot/js/store.js", "A", 226, 0);
        Status(WtUi, RepoAmc, "src/Amc.Daemon/Program.cs", "M", 5, 1);

        // Nested sub-agent spawned by Sub1.
        Step(20);
        Add(EventTypes.AgentSpawned, Sub3, Evidence.Reported, new AgentCreatedData(AgentKind.Sub, "claude", Sub1, "general-purpose", "Diff viewer", "Write the unified diff parser and viewer", SpawnToolUseId: "toolu_01SPAWN3"), session: S1);
        Edit(Sub3, S1, WtUi, RepoAmc, "src/Amc.Daemon/wwwroot/js/diff.js", "created", 96, 0,
            "@@ -0,0 +1,4 @@\n+export function parseUnifiedDiff(text) {\n+  const files = [];\n+  return files;\n+}");
        // Same file as Sub1 → same-file conflict (both active).
        Edit(Sub3, S1, WtUi, RepoAmc, "src/Amc.Daemon/Program.cs", "modified", 2, 0,
            "@@ -70,6 +70,8 @@ ApiEndpoints.Map(app);\n app.MapCollectorEndpoints();\n+// diff endpoint registered by git module\n+app.MapDiffEndpoints();\n \n app.Services.GetRequiredService<EventPipeline>().Restore();");
        Status(WtUi, RepoAmc, "src/Amc.Daemon/wwwroot/js/diff.js", "?", 96, 0);
        Status(WtUi, RepoAmc, "src/Amc.Daemon/Program.cs", "M", 7, 1);
        Add(EventTypes.WorktreeChanged, null, Evidence.Observed, new WorktreeData("/home/dev/agent-mission-control-worktrees/ui", RepoAmc, "agent/ui", "43f3aa3e1c9b", Modified: 1, Added: 2, Untracked: 1), repo: RepoAmc, wt: WtUi);

        // Sub2: process collector — fails with a test failure.
        Tool(Sub2, S1, "Read", "Read src/Amc.Daemon/Contracts.cs", WtMain, RepoAmc, "src/Amc.Daemon/Contracts.cs", read: true);
        Edit(Sub2, S1, WtMain, RepoAmc, "src/Amc.Daemon/Collectors/ProcessCollector.cs", "created", 188, 0,
            "@@ -0,0 +1,6 @@\n+namespace Amc.Daemon.Collectors;\n+\n+public sealed class ProcessCollector : BackgroundService, ILiveProcesses\n+{\n+    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);\n+}");
        Status(WtMain, RepoAmc, "src/Amc.Daemon/Collectors/ProcessCollector.cs", "?", 188, 0);
        TestRun(Sub2, S1, WtMain, RepoAmc, "dotnet test --filter Category=Unit", passed: 41, failed: 2, ["ProcessCollectorTests.ParsesStat", "ProcessCollectorTests.HandlesPidReuse"]);
        Step(15);
        Add(EventTypes.AgentFailed, Sub2, Evidence.Reported, new AgentStatusData(AgentStatus.Failed, "Tool error ended the agent: 2 tests failing"), session: S1);

        // Main1 commit in the main worktree.
        Cmd(Main1, S1, WtMain, RepoAmc, "git commit -m \"Claude hooks config contract\"", "Commit hooks contract", 0, "[main 43f3aa3] Claude hooks config contract\n 2 files changed, 118 insertions(+)");
        Add(EventTypes.GitCommitCreated, Main1, Evidence.Observed, new GitCommitData("43f3aa3e1c9b7d2f", "Claude hooks config contract", "Claude", _t, "main", 2), repo: RepoAmc, wt: WtMain);
        Add(EventTypes.WorktreeChanged, null, Evidence.Observed, new WorktreeData("/home/dev/agent-mission-control", RepoAmc, "main", "43f3aa3e1c9b", IsMain: true, Untracked: 1), repo: RepoAmc, wt: WtMain);

        // Session 2: blocked waiting for permission in acme-backend.
        Step(30);
        Add(EventTypes.AgentCreated, Main2, Evidence.Reported, new AgentCreatedData(AgentKind.Main, "claude", Title: "Add CSV billing export", Cwd: "/home/dev/acme-backend", Pid: 51022, ProcStart: "88200017", Model: "claude-sonnet-5"), session: S2);
        Add(EventTypes.AgentUpdated, Main2, Evidence.Observed, new AgentUpdatedData(GitBranch: "feature/billing-export", TerminalId: "pty-7f3a", Transports: ["pty", "hook-mailbox"]), session: S2);
        Add(EventTypes.TerminalStarted, Main2, Evidence.Observed, new TerminalData("pty-7f3a", 51022, "claude", "/home/dev/acme-backend"), session: S2);
        Add(EventTypes.AgentPrompt, Main2, Evidence.Reported, new AgentTextData("Add a CSV export endpoint for invoices, with tests."), session: S2);
        Edit(Main2, S2, WtRdz, RepoRdz, "src/Billing/InvoiceExport.cs", "modified", 64, 12,
            "@@ -12,9 +12,14 @@ public sealed class InvoiceExport\n {\n-    public string Export(IEnumerable<Invoice> invoices) => string.Empty;\n+    public string Export(IEnumerable<Invoice> invoices)\n+    {\n+        var sb = new StringBuilder(\"number,date,total\\n\");\n+        foreach (var i in invoices) sb.AppendLine($\"{i.Number},{i.Date:yyyy-MM-dd},{i.Total}\");\n+        return sb.ToString();\n+    }\n }");
        Status(WtRdz, RepoRdz, "src/Billing/InvoiceExport.cs", "M", 64, 12);
        TestRun(Main2, S2, WtRdz, RepoRdz, "dotnet test tests/Billing.Tests", passed: 128, failed: 0, null);
        Add(EventTypes.GitCommitCreated, Main2, Evidence.Observed, new GitCommitData("9f81c2d07ab4e611", "Billing: CSV invoice export", "Claude", _t, "feature/billing-export", 3), repo: RepoRdz, wt: WtRdz);
        Add(EventTypes.WorktreeChanged, null, Evidence.Observed, new WorktreeData("/home/dev/acme-backend", RepoRdz, "feature/billing-export", "9f81c2d07ab4", IsMain: true, Modified: 1, Upstream: "origin/feature/billing-export", Ahead: 1), repo: RepoRdz, wt: WtRdz);
        Step(10);
        Add(EventTypes.AgentNotification, Main2, Evidence.Reported, new AgentTextData("Claude needs your permission to use Bash: git push origin feature/billing-export", "permission_prompt"), session: S2);
        Add(EventTypes.AgentStatusChanged, Main2, Evidence.Reported, new AgentStatusData(AgentStatus.Blocked, "Waiting for permission: Bash(git push origin feature/billing-export)"), session: S2);

        // Session 3: process vanished → disconnected.
        Add(EventTypes.AgentCreated, Main3, Evidence.Reported, new AgentCreatedData(AgentKind.Main, "claude", Title: "Investigate flaky sync test", Cwd: "/home/dev/acme-backend", Pid: 50117, ProcStart: "88190002"), session: S3);
        Add(EventTypes.AgentPrompt, Main3, Evidence.Reported, new AgentTextData("Why does SyncTests.ReplaysInOrder fail intermittently?"), session: S3);
        Tool(Main3, S3, "Read", "Read tests/SyncTests.cs", WtRdz, RepoRdz, "tests/SyncTests.cs", read: true);
        Step(20);
        Add(EventTypes.ProcessExited, Main3, Evidence.Observed, new ProcessExitData(50117, "88190002", null, "Process exited unexpectedly (no SessionEnd)"), session: S3, source: EventSources.Process);

        // Messaging: queued, delivered, answered, failed.
        Msg(Main1, S1, "Prioritise the conflict banner, the reviewers asked for it.", deliver: true, reply: "Understood — moving the conflict banner up; it will link to the file and both agents.");
        Msg(Sub1, S1, "Please keep the diff viewer under 300 lines.", deliver: true, reply: null);
        Msg(Main2, S2, "Do not push yet; wait for review.", deliver: false, reply: null);
        Msg(Main3, S3, "Are you still there?", deliver: false, reply: null, fail: "Agent process is gone; no transport available");

        // Bulk activity so lists must virtualize (thousands of events).
        var tools = new[] { "Read", "Grep", "Glob", "Edit", "Bash" };
        var files = new[] { "js/app.js", "js/views/graph.js", "js/views/worktrees.js", "js/views/timeline.js", "css/app.css" };
        for (var i = 0; i < 1500; i++)
        {
            _t = _t.AddMilliseconds(300);
            var agent = i % 3 == 0 ? Sub3 : Sub1;
            var tool = tools[i % tools.Length];
            var f = "src/Amc.Daemon/wwwroot/" + files[i % files.Length];
            if (tool == "Bash") Cmd(agent, S1, WtUi, RepoAmc, $"grep -n \"render\" {f} | head -20", "Find render calls", 0, $"{i % 97}:  render();", step: false);
            else Tool(agent, S1, tool, $"{tool} {f}", WtUi, RepoAmc, f, read: tool != "Edit", step: false);
        }
        _t = DateTimeOffset.UtcNow.AddSeconds(-40);
        Tool(Sub1, S1, "Edit", "Edit src/Amc.Daemon/wwwroot/js/views/graph.js", WtUi, RepoAmc, "src/Amc.Daemon/wwwroot/js/views/graph.js", read: false, finish: false);
        Add(EventTypes.ToolStarted, Sub3, Evidence.Reported, new ToolCallData("toolu_live_sub3", "Edit", "Edit src/Amc.Daemon/Program.cs", "src/Amc.Daemon/Program.cs"), session: S1, repo: RepoAmc, wt: WtUi, file: "src/Amc.Daemon/Program.cs");
        Add(EventTypes.AgentStatusChanged, Main1, Evidence.Reported, new AgentStatusData(AgentStatus.Waiting, "Waiting for sub-agents"), session: S1);
    }

    // ---- helpers --------------------------------------------------------------

    private void Step(int seconds) => _t = _t.AddSeconds(seconds);

    private void Add<T>(string type, string? agent, Evidence ev, T data, string? session = null, string? repo = null,
        string? wt = null, string? file = null, string source = Sim, string? dedupe = null) where T : class
    {
        _t = _t.AddMilliseconds(350);
        _events.Add(new AgentEvent
        {
            Ts = _t, Type = type, Source = source, Evidence = ev, AgentId = agent, SessionId = session,
            RepositoryId = repo, WorktreeId = wt, FilePath = file, DedupeKey = dedupe ?? $"fixture:{_events.Count}",
            Data = Json.ToObject(data),
        });
    }

    private void Tool(string agent, string session, string tool, string summary, string? wt, string? repo, string? file = null,
        bool read = false, bool step = true, bool finish = true)
    {
        var id = $"toolu_{++_tool:D6}";
        if (step) Step(4);
        Add(EventTypes.ToolStarted, agent, Evidence.Reported, new ToolCallData(id, tool, summary, file), session, repo, wt, file);
        if (read && file is not null) Add(EventTypes.FileRead, agent, Evidence.Reported, new FileChangeData(id, tool), session, repo, wt, file);
        if (finish) Add(EventTypes.ToolCompleted, agent, Evidence.Reported, new ToolCallData(id, tool, summary, file, DurationMs: 120 + _tool % 900), session, repo, wt, file);
    }

    private void Edit(string agent, string session, string wt, string repo, string file, string kind, int added, int removed, string patch)
    {
        var id = $"toolu_{++_tool:D6}";
        Step(6);
        var tool = kind == "created" ? "Write" : "Edit";
        Add(EventTypes.ToolStarted, agent, Evidence.Reported, new ToolCallData(id, tool, $"{tool} {file}", file), session, repo, wt, file);
        Add("file." + kind, agent, Evidence.Reported, new FileChangeData(id, tool, null, added, removed, patch, "/abs/" + file), session, repo, wt, file);
        Add(EventTypes.ToolCompleted, agent, Evidence.Reported, new ToolCallData(id, tool, $"{tool} {file}", file, DurationMs: 85), session, repo, wt, file);
    }

    private void Status(string wt, string repo, string file, string status, int added, int removed) =>
        Add(EventTypes.GitFileStatus, null, Evidence.Observed, new GitFileStatusData(status, added, removed), repo: repo, wt: wt, file: file, source: Sim);

    private void Cmd(string agent, string session, string wt, string repo, string command, string description, int exit, string stdout, bool step = true)
    {
        var id = $"toolu_{++_tool:D6}";
        if (step) Step(5);
        Add(EventTypes.CommandStarted, agent, Evidence.Reported, new CommandData(id, command, description, "/home/dev"), session, repo, wt);
        Add(exit == 0 ? EventTypes.CommandCompleted : EventTypes.CommandFailed, agent, Evidence.Reported,
            new CommandData(id, command, description, "/home/dev", exit, stdout, null, 1800), session, repo, wt);
    }

    private void TestRun(string agent, string session, string wt, string repo, string command, int passed, int failed, IReadOnlyList<string>? failedTests)
    {
        var id = $"toolu_{++_tool:D6}";
        Step(8);
        Add(EventTypes.CommandStarted, agent, Evidence.Reported, new CommandData(id, command, "Run tests"), session, repo, wt);
        Add(EventTypes.TestStarted, agent, Evidence.Inferred, new TestRunData(id, "dotnet", command), session, repo, wt);
        var output = failed > 0
            ? $"Failed!  - Failed: {failed}, Passed: {passed}, Skipped: 0, Total: {passed + failed}\n  Failed {string.Join("\n  Failed ", failedTests ?? [])}"
            : $"Passed!  - Failed: 0, Passed: {passed}, Skipped: 0, Total: {passed}";
        Add(failed > 0 ? EventTypes.CommandFailed : EventTypes.CommandCompleted, agent, Evidence.Reported,
            new CommandData(id, command, "Run tests", "/home/dev", failed > 0 ? 1 : 0, output, null, 21400), session, repo, wt);
        Add(failed > 0 ? EventTypes.TestFailed : EventTypes.TestPassed, agent, Evidence.Inferred,
            new TestRunData(id, "dotnet", command, passed, failed, 0, 21400, failedTests), session, repo, wt);
    }

    private void Msg(string agent, string session, string text, bool deliver, string? reply, string? fail = null)
    {
        var id = Ulid.NewId();
        Step(5);
        Add(EventTypes.MessageSent, agent, Evidence.Observed, new MessageData(id, text, "hook-mailbox", "user"), session, source: Sim);
        if (fail is not null)
            Add(EventTypes.MessageFailed, agent, Evidence.Observed, new MessageStatusData(id, "hook-mailbox", fail), session, source: Sim);
        else if (deliver)
        {
            Step(12);
            Add(EventTypes.MessageDelivered, agent, Evidence.Observed, new MessageStatusData(id, "hook-mailbox", "Returned as additionalContext on PostToolUse"), session, source: Sim);
        }
        if (reply is not null)
        {
            Step(9);
            Add(EventTypes.MessageReceived, agent, Evidence.Inferred, new MessageReplyData(id, reply), session, source: Sim);
        }
    }
}
