using System.Text.Json.Nodes;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Tools;
using Amc.Tests.Claude;
using Microsoft.Extensions.Options;

namespace Amc.Tests.Tools;

public class ToolCatalogTests
{
    [Fact]
    public void User_tools_override_and_extend_the_builtin_catalog_and_invalid_entries_are_dropped()
    {
        var merged = AgentToolCatalog.Merge([
            new AgentTool { Id = "codex", Name = "Codex", Command = "codex", ModelFlag = "-m" },
            new AgentTool { Id = "mytool", Command = "mytool" },
            new AgentTool { Id = "Bad Id", Command = "x" },
            new AgentTool { Id = "rel", Command = "bin/x" },
        ]);
        Assert.Equal("-m", merged.Single(t => t.Id == "codex").ModelFlag);
        Assert.Equal(["mytool"], merged.Single(t => t.Id == "mytool").ProcessNames);
        Assert.DoesNotContain(merged, t => t.Id is "Bad Id" or "rel");
        Assert.Equal("claude", merged.Single(t => t.Id == "claude").Adapter);
        Assert.Null(merged.Single(t => t.Id == "gemini").Adapter);
    }
}

public class ProtocolReceiverTests
{
    private readonly CollectingSink _sink = new();
    private readonly FakeWorktreeIndex _wt = new();

    private ProtocolReceiver New() =>
        new(new ToolRegistry(Options.Create(new AmcOptions())), _sink, _wt, TimeProvider.System);

    private static JsonObject R(string kind, JsonObject? data = null, string? parent = null) => new()
    {
        ["tool"] = "codex", ["kind"] = kind,
        ["agent"] = new JsonObject { ["id"] = parent is null ? "s1" : "s1-sub", ["parent"] = parent, ["cwd"] = "/work/repo", ["title"] = "Fix tests" },
        ["data"] = data ?? new JsonObject(),
    };

    [Fact]
    public async Task Reports_map_to_normal_events_with_agent_tree_files_usage_and_end()
    {
        var r = New();
        Assert.True((await r.ReportAsync(R("start"), default)).Ok);
        await r.ReportAsync(R("tool_start", new() { ["id"] = "t1", ["name"] = "shell", ["command"] = "npm test" }), default);
        await r.ReportAsync(R("tool_end", new() { ["id"] = "t1", ["command"] = "npm test", ["exitCode"] = 1 }), default);
        await r.ReportAsync(R("file", new() { ["path"] = "/work/repo/src/a.ts", ["change"] = "created" }, parent: "s1"), default);
        await r.ReportAsync(R("usage", new() { ["id"] = "m1", ["model"] = "gpt-5", ["input"] = 10, ["output"] = 20 }), default);
        await r.ReportAsync(R("end"), default);

        var s = _sink.Fold();
        var main = s.Agents["codex:s1"];
        Assert.Equal("codex", main.Provider);
        Assert.Equal(AgentStatus.Completed, main.Status);
        Assert.Equal(["codex:s1-sub"], main.ChildIds);
        Assert.Equal(1, main.ToolFailures);
        Assert.Equal(20, main.Usage.Output);
        var f = Assert.Single(s.Files.Values);
        Assert.Equal(("src/a.ts", Evidence.Reported), (f.Path, f.Agents["codex:s1-sub"].Evidence));
        Assert.Contains(_wt.Activity, a => a.AgentId == "codex:s1" && a.Kind == ActivityKinds.Shell);
    }

    [Fact]
    public async Task Invalid_reports_are_rejected_and_native_tools_must_use_their_adapter()
    {
        var r = New();
        Assert.False((await r.ReportAsync(new JsonObject { ["tool"] = "codex", ["kind"] = "start" }, default)).Ok);
        Assert.False((await r.ReportAsync(R("teleport"), default)).Ok);
        var claude = R("start"); claude["tool"] = "claude";
        Assert.False((await r.ReportAsync(claude, default)).Ok);
        Assert.False((await r.ReportAsync(R("file", new() { ["path"] = "/etc/passwd" }), default)).Ok);
    }
}

public class ToolHookPathTests
{
    [Fact]
    public void Patch_and_path_fields_are_made_absolute_against_the_cwd_lexically()
    {
        var input = new JsonObject { ["input"] = "*** Begin Patch\n*** Add File: src/new.ts\n+x\n*** Update File: /work/repo/src/old.ts\n*** Delete File: ../other/x.ts\n*** End Patch" };
        Assert.Equal([("/work/repo/src/new.ts", "created"), ("/work/repo/src/old.ts", "modified"), ("/work/other/x.ts", "deleted")],
            ToolHookReceiver.FilesOf(input, "/work/repo"));
        Assert.Equal([("/work/repo/app.py", "modified")], ToolHookReceiver.FilesOf(new JsonObject { ["file_path"] = "app.py" }, "/work/repo"));
        Assert.Empty(ToolHookReceiver.FilesOf(new JsonObject { ["file_path"] = "app.py" }, null)); // no cwd: not attributable
    }

    [Fact]
    public void Windows_tool_paths_come_out_in_canonical_form()
    {
        if (!OperatingSystem.IsWindows()) return; // drive letters only mean something there
        Assert.Equal([("C:/work/repo/src/a.ts", "modified")], ToolHookReceiver.FilesOf(new JsonObject { ["file_path"] = @"src\a.ts" }, @"c:\work\repo"));
        Assert.Equal([("D:/x/b.ts", "modified")], ToolHookReceiver.FilesOf(new JsonObject { ["path"] = @"D:\x\b.ts" }, @"C:\work"));
    }
}
