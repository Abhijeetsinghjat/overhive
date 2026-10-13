using System.Text;
using Amc.Cli.Commands;
using Amc.Cli.Infrastructure;
using Amc.Core.Domain;
using Amc.Core.Events;
using static Amc.Tests.Cli.CliHarness;

namespace Amc.Tests.Cli;

public class FormattingTests
{
    private static string Render(IEnumerable<Agent> agents, bool all, out int hidden, Dictionary<string, Worktree>? wts = null, bool color = false)
    {
        var (table, h) = AgentTree.Build(agents.ToList(), wts ?? [], Now, all, new Style(color), "/home/u");
        hidden = h;
        var sw = new StringWriter();
        table.Write(sw, new Style(color));
        return sw.ToString();
    }

    private static readonly List<Agent> Family =
    [
        Agent("claude:main1111", "Lead", pid: 4242),
        Agent("claude:main1111/sub1aaaa", "Explore code", parent: "claude:main1111"),
        Agent("claude:main1111/sub2bbbb", "Write tests", parent: "claude:main1111"),
        Agent("claude:main1111/sub2bbbb/deep3ccc", "Nested helper", parent: "claude:main1111/sub2bbbb"),
        Agent("claude:other222", "Other session", AgentStatus.Waiting),
    ];

    [Fact]
    public void Tree_indents_children_under_parents()
    {
        var lines = Render(Family, all: false, out var hidden).TrimEnd().Split(Environment.NewLine);
        Assert.Equal(0, hidden);
        Assert.StartsWith("AGENT", lines[0]);
        Assert.StartsWith("main1111 Lead", lines[1]);
        Assert.StartsWith("├─ sub1aaaa Explore code", lines[2]);
        Assert.StartsWith("└─ sub2bbbb Write tests", lines[3]);
        Assert.StartsWith("   └─ deep3ccc Nested helper", lines[4]);
        Assert.StartsWith("other222 Other session", lines[5]);
        Assert.Contains("4242", lines[1]);
        Assert.Contains("running", lines[1]);
        Assert.Contains("waiting", lines[5]);
    }

    [Fact]
    public void Columns_are_aligned()
    {
        var lines = Render(Family, all: false, out _).TrimEnd().Split(Environment.NewLine);
        var statusCol = lines[0].IndexOf("STATUS", StringComparison.Ordinal);
        foreach (var l in lines.Skip(1))
            Assert.True(l[statusCol..].StartsWith("running") || l[statusCol..].StartsWith("waiting"), l);
    }

    [Fact]
    public void Alignment_ignores_ansi_colour_codes()
    {
        var lines = Render(Family, all: false, out _, color: true).TrimEnd().Split(Environment.NewLine);
        var plain = lines.Select(l => System.Text.RegularExpressions.Regex.Replace(l, "\u001b\\[[0-9;]*m", "")).ToList();
        Assert.Contains(lines, l => l.Contains('\u001b'));
        var statusCol = plain[0].IndexOf("STATUS", StringComparison.Ordinal);
        Assert.All(plain.Skip(1), l => Assert.NotEqual(' ', l[statusCol]));
    }

    [Fact]
    public void Old_terminal_agents_are_hidden_unless_all()
    {
        var agents = new List<Agent>(Family)
        {
            Agent("claude:done0000", "Finished long ago", AgentStatus.Completed, lastActivityAgo: TimeSpan.FromHours(3)),
            Agent("claude:done1111", "Finished recently", AgentStatus.Completed, lastActivityAgo: TimeSpan.FromMinutes(10)),
        };
        var text = Render(agents, all: false, out var hidden);
        Assert.Equal(1, hidden);
        Assert.DoesNotContain("done0000", text);
        Assert.Contains("done1111", text);

        text = Render(agents, all: true, out hidden);
        Assert.Equal(0, hidden);
        Assert.Contains("done0000", text);
    }

    [Fact]
    public void Hidden_parent_is_kept_when_a_child_is_visible()
    {
        var agents = new List<Agent>
        {
            Agent("claude:oldparent", "Old parent", AgentStatus.Completed, lastActivityAgo: TimeSpan.FromHours(5)),
            Agent("claude:oldparent/livechld", "Live child", parent: "claude:oldparent"),
        };
        var text = Render(agents, all: false, out var hidden);
        Assert.Equal(0, hidden);
        Assert.Contains("oldparen", text);
        Assert.Contains("└─ livechld", text);
    }

    [Fact]
    public void Orphans_render_as_roots()
    {
        var text = Render([Agent("claude:x/orphan99", "Orphan", parent: "claude:x")], all: false, out _);
        Assert.Contains("orphan99 Orphan", text);
    }

    [Fact]
    public void Worktree_column_shows_name_and_branch()
    {
        var a = Agent("claude:main1111", "Lead");
        a.CurrentWorktreeId = "wt:1";
        var wts = new Dictionary<string, Worktree> { ["wt:1"] = new() { Id = "wt:1", Root = "/home/u/dev/app-wt/cli", RepositoryId = "r", Branch = "agent/cli" } };
        Assert.Contains("cli@agent/cli", Render([a], false, out _, wts));
    }

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(125, "2m")]
    [InlineData(7200, "2h")]
    [InlineData(3 * 86400, "3d")]
    public void Ages_are_compact(int seconds, string expected) => Assert.Equal(expected, Fmt.Age(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Since_parses_relative_and_absolute_times()
    {
        Assert.Equal(Now.AddMinutes(-10), Fmt.ParseSince("10m", Now));
        Assert.Equal(Now.AddHours(-2), Fmt.ParseSince("2h", Now));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), Fmt.ParseSince("2026-10-01T00:00:00Z", Now));
        Assert.Throws<UsageException>(() => Fmt.ParseSince("yesterday", Now));
    }

    [Fact]
    public void Enum_wire_names_match_the_api()
    {
        Assert.Equal("in_progress", Fmt.JsonName(TaskState.InProgress));
        Assert.Equal("same_file", Fmt.JsonName(ConflictKind.SameFile));
        Assert.Equal("running", Fmt.Status(AgentStatus.Running));
    }

    [Fact]
    public void Files_table_shows_status_counts_path_and_attribution()
    {
        var f = new FileChange
        {
            Key = "wt:1|src/A.cs", WorktreeId = "wt:1", Path = "src/A.cs", GitStatus = "M", Added = 12, Removed = 3,
            Agents = { ["claude:main1111"] = new Attribution(Evidence.Reported, Now, 2), ["claude:other222"] = new Attribution(Evidence.Inferred, Now.AddMinutes(-1), 1) },
        };
        var g = new FileChange { Key = "wt:1|new.txt", WorktreeId = "wt:1", Path = "new.txt", LastChangeKind = "created" };
        var sw = new StringWriter();
        FilesCommand.Build([f, g], new Dictionary<string, Worktree>(), new Style(false)).Write(sw, new Style(false));
        var lines = sw.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Matches(@"^M\s+\+12/-3\s+src/A\.cs\s+main1111\(rep\) other222\(inf\)$", lines[1]);
        Assert.Matches(@"^a\s+-\s+new\.txt$", lines[2]); // lower-case: reported by agent, not yet confirmed by git
    }

    [Fact]
    public void Event_lines_summarise_payloads()
    {
        using var h = new CliHarness();
        var ctx = h.Context();
        var e = new AgentEvent
        {
            Seq = 42, Ts = Now, Type = EventTypes.CommandCompleted, Source = EventSources.Hook, Evidence = Evidence.Reported,
            AgentId = "claude:main1111", Data = Json.ToObject(new CommandData("t1", "dotnet test", ExitCode: 0, DurationMs: 2500)),
        };
        var line = EventFormatter.Line(e, ctx);
        Assert.Contains("command.completed", line);
        Assert.Contains("main1111", line);
        Assert.Contains("$ dotnet test  → exit 0 (2.5s)", line);
    }

    [Fact]
    public void Logs_show_bounded_stdout_and_stderr()
    {
        using var h = new CliHarness();
        var ctx = h.Context();
        var stdout = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));
        var e = new AgentEvent
        {
            Seq = 1, Ts = Now, Type = EventTypes.CommandFailed, Source = EventSources.Hook,
            Data = Json.ToObject(new CommandData("t1", "make", ExitCode: 2, Stdout: stdout, Stderr: "boom")),
        };
        var text = LogsCommand.Format(e, ctx, maxLines: 5);
        Assert.Contains("✗ $ make  exit 2", text);
        Assert.Contains("… 25 earlier line(s)", text);
        Assert.Contains("line 30", text);
        Assert.DoesNotContain("line 25\n", text);
        Assert.Contains("┃ boom", text);
    }

    [Fact]
    public void Diff_extraction_accepts_text_and_json_shapes()
    {
        const string patch = "--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n";
        Assert.Equal(patch, DiffCommand.ExtractDiff(new ApiResponse(200, patch, "text/plain")));
        Assert.Equal(patch, DiffCommand.ExtractDiff(new ApiResponse(200, Json.Serialize(new { diff = patch }), "application/json")));
        Assert.Equal(patch + patch, DiffCommand.ExtractDiff(new ApiResponse(200, Json.Serialize(new { files = new[] { new { path = "x", patch }, new { path = "y", patch } } }), "application/json")));
        var colored = DiffCommand.Colorize(patch, new Style(true));
        Assert.Contains("\u001b[32m+b", colored);
        Assert.Contains("\u001b[31m-a", colored);
    }

    [Fact]
    public void WebSocket_frames_advance_the_cursor_and_skip_duplicates()
    {
        var seen = new List<long>();
        long cursor = 10;
        byte[] Frame(long seq) => Encoding.UTF8.GetBytes(
            """{"t":"event","replay":true,"e":{"seq":""" + seq + ""","id":"x","ts":"2026-10-07T12:00:00Z","type":"tool.started","source":"hook","evidence":"reported","data":{}}}""");
        Assert.Null(EventsCommand.HandleFrame(Encoding.UTF8.GetBytes("""{"t":"hello","seq":12,"serverTime":"2026-10-07T12:00:00Z"}"""), ref cursor, e => seen.Add(e.Seq)));
        Assert.Equal(10, cursor); // hello never moves a non-zero cursor
        EventsCommand.HandleFrame(Frame(9), ref cursor, e => seen.Add(e.Seq));
        EventsCommand.HandleFrame(Frame(11), ref cursor, e => seen.Add(e.Seq));
        EventsCommand.HandleFrame(Frame(11), ref cursor, e => seen.Add(e.Seq));
        EventsCommand.HandleFrame(Frame(12), ref cursor, e => seen.Add(e.Seq));
        Assert.Equal([11, 12], seen);
        Assert.Equal(12, cursor);
        Assert.NotNull(EventsCommand.HandleFrame(Encoding.UTF8.GetBytes("""{"t":"resync","reason":"client too slow"}"""), ref cursor, _ => { }));
        Assert.Null(EventsCommand.HandleFrame(Encoding.UTF8.GetBytes("""{"t":"live","processes":[]}"""), ref cursor, _ => { }));
    }

    [Fact]
    public void WebSocket_uri_is_derived_from_the_daemon_url()
    {
        Assert.Equal("ws://127.0.0.1:47821/ws?since=99", EventsCommand.WebSocketUri(new Uri("http://127.0.0.1:47821/"), 99).ToString());
    }

    [Fact]
    public void Client_side_event_filter_matches_server_semantics()
    {
        var e = new AgentEvent { Type = "tool.started", Source = "hook", AgentId = "a", FilePath = "src/x.cs", WorktreeId = "wt:1" };
        Assert.True(new EventFilter("a", ["tool.*"]).Matches(e));
        Assert.True(new EventFilter(null, ["command.failed", "tool.started"], "src/x.cs", "wt:1").Matches(e));
        Assert.False(new EventFilter("b").Matches(e));
        Assert.False(new EventFilter(Types: ["tool.completed"]).Matches(e));
        Assert.False(new EventFilter(File: "src/y.cs").Matches(e));
    }
}
