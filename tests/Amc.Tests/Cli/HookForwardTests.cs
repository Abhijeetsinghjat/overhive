using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Amc.Cli.Hooks;
using Amc.Core.Providers;
using Amc.Core.Providers.Claude;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Amc.Tests.Cli;

/// <summary>
/// The Windows hook transport: command shapes (run on every OS through the OS flag) and agentctl hook-forward
/// against a real AF_UNIX socket served by Kestrel, in-process and as a real agentctl process.
/// </summary>
public sealed class HookForwardTests : IDisposable
{
    private readonly CliHarness _h = new() { Windows = true };
    private readonly string _dir = Directory.CreateTempSubdirectory("amc-fwd-").FullName;
    private string Socket => Path.Combine(_dir, ClaudeHooksConfig.SocketFileName);
    private string Settings => Path.Combine(_h.Dir, ".claude", "settings.json");
    private JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    private string HookSocket => Path.Combine(_h.Dir, "state", ClaudeHooksConfig.SocketFileName);
    private const string Exe = @"C:\Users\Jane O'Neil\AppData\Local\Overhive\agentctl.exe";

    public void Dispose()
    {
        _h.Dispose();
        Fixtures.TempDirs.Delete(_dir);
    }

    // ---- command shapes ----

    [Fact]
    public async Task Windows_claude_hooks_use_the_exec_form_forwarder_with_no_shell_curl_or_token()
    {
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        var s = Read(Settings);
        Assert.True(ClaudeHooksConfig.IsInstalled(s));
        Assert.True(ClaudeHooksConfig.IsRewakeInstalled(s));

        var sync = s["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!;
        Assert.Equal(Exe, (string?)sync["command"]);
        Assert.Equal(["hook-forward", "claude", "--socket", HookSocket, "--tag", ClaudeHooksConfig.Marker],
            sync["args"]!.AsArray().Select(a => (string?)a));
        Assert.Equal(ClaudeHooksConfig.TimeoutSeconds, (int)sync["timeout"]!);

        var rewake = s["hooks"]!["Stop"]!.AsArray().SelectMany(g => g!["hooks"]!.AsArray()).Single(h => h?["asyncRewake"] is not null)!;
        Assert.Equal(Exe, (string?)rewake["command"]);
        Assert.Contains("--wait", rewake["args"]!.AsArray().Select(a => (string?)a));

        var text = File.ReadAllText(Settings);
        Assert.DoesNotContain("curl", text);
        Assert.DoesNotContain("X-Amc-Token", text);
        Assert.DoesNotContain("test-token", text);
        Assert.DoesNotContain("127.0.0.1", text);

        // Idempotent and recognised as current by status; uninstall removes every entry.
        Assert.Equal(0, await _h.Run("hooks", "install", "--settings", Settings, "--yes"));
        Assert.Contains("already installed and up to date", _h.Out.ToString());
        _h.Out.GetStringBuilder().Clear();
        Assert.Equal(0, await _h.Run("hooks", "status", "--settings", Settings, "--json"));
        var status = JsonNode.Parse(_h.Out.ToString())!;
        Assert.True((bool)status["upToDate"]!);
        Assert.True(status.AsObject().ContainsKey("forwarder"));
        Assert.Null(ClaudeHooksConfig.Uninstall(Read(Settings))["hooks"]);
    }

    [Fact]
    public void Posix_claude_hook_does_not_match_the_windows_one_so_switching_os_reports_outdated()
    {
        var f = new HookForwarder(Exe);
        var win = ClaudeHooksConfig.BuildWindowsCommand(f, @"C:\s\amcd.sock");
        var s = ClaudeHooksConfig.Install(new JsonObject(), ClaudeHooksConfig.BuildCommand("/s/amcd.sock"));
        var hook = s["hooks"]!["Stop"]![0]!["hooks"]![0]!;
        Assert.False(win.Matches(hook));
        Assert.True(((ClaudeHooksConfig.HookCommand)ClaudeHooksConfig.BuildCommand("/s/amcd.sock")).Matches(hook));
        Assert.Null(hook["args"]); // POSIX entries keep their exact shape
    }

    [Fact]
    public void Cmd_and_powershell_lines_quote_paths_for_their_shell()
    {
        var f = new HookForwarder(@"C:\Program Files\Overhive\agentctl.exe");
        var args = f.Args("codex", "PreToolUse", @"C:\Users\Jane O'Neil\state\amcd.sock");
        Assert.Equal(@"""C:\Program Files\Overhive\agentctl.exe"" hook-forward codex --event PreToolUse --socket ""C:\Users\Jane O'Neil\state\amcd.sock"" --tag agent-mission-control",
            f.CmdLine(args));
        Assert.Equal(@"& 'C:\Program Files\Overhive\agentctl.exe' hook-forward codex --event PreToolUse --socket 'C:\Users\Jane O''Neil\state\amcd.sock' --tag agent-mission-control",
            f.PowerShellLine(args));
        Assert.Equal("'a\u2019\u2019b'", HookForwarder.PowerShellQuote("a\u2019b")); // PowerShell also ends strings at ’
        Assert.Equal("\"C:\\a&b\\x.exe\"", HookForwarder.CmdQuote(@"C:\a&b\x.exe"));
        Assert.Throws<ArgumentException>(() => HookForwarder.CmdQuote(@"C:\100%\amcd.sock")); // cmd would expand %…%
    }

    [Fact]
    public void Windows_tool_hooks_use_each_tools_shell_and_stay_recognisable()
    {
        var f = new HookForwarder(Exe);
        JsonObject Install(string tool) => ToolHooksConfig.Install(ToolHooksConfig.Profiles[tool], new JsonObject(), @"C:\s\amcd.sock", f);

        var codex = Install("codex")["hooks"]!["Stop"]![0]!["hooks"]![0]!;
        Assert.StartsWith($"\"{Exe}\" hook-forward codex --event Stop", (string?)codex["command"]);
        Assert.Equal((string?)codex["command"], (string?)codex["commandWindows"]);

        foreach (var (tool, evt) in new[] { ("gemini", "AfterTool"), ("cursor", "stop") })
        {
            var s = Install(tool);
            var entry = tool == "cursor" ? s["hooks"]![evt]![0]! : s["hooks"]![evt]![0]!["hooks"]![0]!;
            Assert.StartsWith(@"& 'C:\Users\Jane O''Neil\AppData\Local\Overhive\agentctl.exe' hook-forward " + tool, (string?)entry["command"]);
        }

        var copilot = Install("copilot");
        var c = copilot["hooks"]!["agentStop"]![0]!;
        Assert.Null(c["bash"]);
        Assert.StartsWith("& '", (string?)c["powershell"]);
        Assert.Equal(5, (int)c["timeoutSec"]!);

        Assert.StartsWith($"\"{Exe}\" hook-forward grok", (string?)Install("grok")["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]);

        foreach (var (tool, p) in ToolHooksConfig.Profiles)
        {
            var s = Install(tool);
            Assert.True(ToolHooksConfig.IsInstalled(p, s), tool);
            Assert.DoesNotContain("curl", s.ToJsonString());
            Assert.Null(ToolHooksConfig.Uninstall(p, s)["hooks"]);
        }
    }

    [Theory]
    [InlineData("copilot", true)]
    [InlineData("copilot", false)]
    [InlineData("cursor", true)]
    [InlineData("cursor", false)]
    public async Task Tool_install_through_the_cli_writes_flat_hook_lists(string tool, bool windows)
    {
        // Cursor and Copilot list entries directly under each event (no matcher groups); the plan must see them.
        _h.Windows = windows;
        var path = Path.Combine(_h.Dir, tool + ".json");
        Assert.Equal(0, await _h.Run("hooks", "install", "--tool", tool, "--settings", path, "--yes"));
        Assert.Contains("+ sessionStart: add agent-mission-control hook", _h.Out.ToString());
        var entry = Read(path)["hooks"]!["sessionStart"]![0]!;
        var cmd = (string?)entry[tool == "copilot" ? (windows ? "powershell" : "bash") : "command"];
        Assert.Contains(windows ? $"hook-forward {tool}" : "--unix-socket", cmd);
        Assert.Equal(0, await _h.Run("hooks", "install", "--tool", tool, "--settings", path, "--yes"));
        Assert.Contains("already installed and up to date", _h.Out.ToString());
        Assert.Equal(0, await _h.Run("hooks", "uninstall", "--tool", tool, "--settings", path, "--yes"));
        Assert.Null(Read(path)["hooks"]);
    }

    [Fact]
    public void Parse_is_lenient_and_rejects_nonsense_without_throwing()
    {
        var r = HookForward.Parse(["claude", "--socket", "/s/amcd.sock", "--tag", "agent-mission-control", "--bogus"], () => null)!;
        Assert.Equal("/hooks/claude", r.PathAndQuery);
        Assert.Equal(TimeSpan.FromSeconds(3), r.Timeout);
        Assert.Equal("/hooks/claude/wait", HookForward.Parse(["claude", "--wait", "--socket", "/s"], () => null)!.PathAndQuery);
        Assert.Equal("/hooks/codex?e=PreToolUse", HookForward.Parse(["codex", "--event", "PreToolUse"], () => "/d/amcd.sock")!.PathAndQuery);
        Assert.Null(HookForward.Parse([], () => "/s"));
        Assert.Null(HookForward.Parse(["../etc"], () => "/s"));
        Assert.Null(HookForward.Parse(["claude", "--socket"], () => null));
    }

    // ---- forwarding through a real socket ----

    private static MemoryStream In(string s) => new(Encoding.UTF8.GetBytes(s));

    private async Task<(int Code, string Out, string Err)> Forward(string tool, string payload, bool wait = false, string? evt = null, TimeSpan? timeout = null)
    {
        var o = new MemoryStream();
        var e = new MemoryStream();
        var code = await HookForward.RunAsync(new HookForward.Request(tool, evt, wait, Socket), In(payload), o, e, CancellationToken.None, timeout);
        return (code, Encoding.UTF8.GetString(o.ToArray()), Encoding.UTF8.GetString(e.ToArray()));
    }

    /// <summary>A stand-in amcd: Kestrel on a Unix socket, the way Program.cs listens.</summary>
    private async Task<WebApplication> Daemon(Action<WebApplication> routes)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(Socket));
        var app = b.Build();
        routes(app);
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Daemon_down_means_silent_exit_0()
    {
        var sw = Stopwatch.StartNew();
        Assert.Equal((0, "", ""), await Forward("claude", """{"hook_event_name":"Stop"}"""));
        Assert.Equal((0, "", ""), await Forward("codex", "{}", evt: "Stop"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());

        // The waiter logs its attempt the way the shell waiter does.
        Assert.Equal((0, "", ""), await Forward("claude", "{}", wait: true));
        var log = File.ReadAllText(Path.Combine(_dir, ClaudeHooksConfig.RewakeLogFileName));
        Assert.Contains($"start pid={Environment.ProcessId}", log);
        Assert.Contains("result=7 bytes=0", log);
    }

    [Fact]
    public async Task Round_trip_posts_stdin_and_prints_the_reply_and_errors_stay_silent()
    {
        var seen = new List<(string Path, string ContentType, string Body)>();
        await using var app = await Daemon(a =>
        {
            async Task<string> Body(HttpRequest r) => await new StreamReader(r.Body).ReadToEndAsync();
            a.MapPost("/hooks/claude", async (HttpRequest r) =>
            {
                seen.Add((r.Path + r.QueryString, r.ContentType ?? "", await Body(r)));
                return Results.Text("""{"hookSpecificOutput":{"additionalContext":"hi"}}""", "application/json");
            });
            a.MapPost("/hooks/claude/wait", async (HttpRequest r) => Results.Text((await Body(r)).Contains("msg") ? "focus on tests\n" : ""));
            a.MapPost("/hooks/{tool}", async (string tool, HttpRequest r) =>
            {
                seen.Add((r.Path + r.QueryString, r.ContentType ?? "", await Body(r)));
                return tool == "gemini" ? Results.StatusCode(500) : Results.Text("""{"decision":"block"}""");
            });
        });

        var payload = """{"hook_event_name":"PreToolUse","tool_input":{"command":"echo ü"}}""";
        Assert.Equal((0, """{"hookSpecificOutput":{"additionalContext":"hi"}}""", ""), await Forward("claude", payload));
        Assert.Equal(("/hooks/claude", "application/json", payload), seen[0]);

        Assert.Equal((0, """{"decision":"block"}""", ""), await Forward("codex", "{}", evt: "Stop"));
        Assert.Equal("/hooks/codex?e=Stop", seen[1].Path);
        Assert.Equal((0, "", ""), await Forward("gemini", "{}", evt: "AfterTool")); // HTTP error: like curl -f, nothing

        // asyncRewake: a message wakes Claude (stderr + exit 2); no message is a silent 0.
        Assert.Equal((2, "", "focus on tests\n"), await Forward("claude", """{"msg":1}""", wait: true));
        Assert.Equal((0, "", ""), await Forward("claude", "{}", wait: true));
        Assert.Contains("result=0 bytes=14", File.ReadAllText(Path.Combine(_dir, ClaudeHooksConfig.RewakeLogFileName)));
        await app.StopAsync();
    }

    [Fact]
    public async Task Slow_daemon_times_out_silently()
    {
        await using var app = await Daemon(a => a.MapPost("/hooks/claude", async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Results.Text("late");
        }));
        var sw = Stopwatch.StartNew();
        Assert.Equal((0, "", ""), await Forward("claude", "{}", timeout: TimeSpan.FromMilliseconds(300)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
        await app.StopAsync();
    }

    /// <summary>The dotnet host by absolute path (on Windows "C:\Program Files\dotnet\dotnet.exe": a space to quote).</summary>
    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hp && File.Exists(hp)) return hp;
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists) ?? "dotnet";
    }

    /// <summary>agentctl as installed hooks run it: the apphost next to agentctl.dll when present, else dotnet + dll.</summary>
    private static HookForwarder RealForwarder()
    {
        var dll = typeof(HookForward).Assembly.Location;
        var apphost = Path.ChangeExtension(dll, OperatingSystem.IsWindows() ? ".exe" : null);
        return File.Exists(apphost) ? new HookForwarder(apphost) : new HookForwarder(DotnetHost(), [dll]);
    }

    private static async Task<(int Code, string Out, string Err)> Spawn(ProcessStartInfo psi, string stdin)
    {
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.StandardInputEncoding = psi.StandardOutputEncoding = psi.StandardErrorEncoding = new UTF8Encoding(false);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(TimeSpan.FromSeconds(60)), "forwarder did not exit");
        return (p.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// The installed exec-form hook for real: a separate agentctl process, its stdin piped like a tool does, talking
    /// to a real socket. Also the waiter, and the daemon being down.
    /// </summary>
    [Fact]
    public async Task Real_agentctl_process_forwards_a_hook()
    {
        var f = RealForwarder();
        var cmd = ClaudeHooksConfig.BuildWindowsCommand(f, Socket);
        ProcessStartInfo Psi(ClaudeHooksConfig.HookCommand c)
        {
            var psi = new ProcessStartInfo(c.Command);
            foreach (var a in c.Args!) psi.ArgumentList.Add(a);
            return psi;
        }
        Assert.Equal((0, "", ""), await Spawn(Psi(cmd), "{}")); // daemon down

        await using var app = await Daemon(a =>
        {
            a.MapPost("/hooks/claude", async (HttpRequest r) => Results.Text("echo:" + await new StreamReader(r.Body).ReadToEndAsync()));
            a.MapPost("/hooks/claude/wait", () => Results.Text("wake up"));
        });
        Assert.Equal((0, """echo:{"hook_event_name":"SessionStart","x":"ü"}""", ""), await Spawn(Psi(cmd), """{"hook_event_name":"SessionStart","x":"ü"}"""));
        Assert.Equal((2, "", "wake up\n"), await Spawn(Psi(ClaudeHooksConfig.BuildWindowsRewakeCommand(f, Socket)), "{}"));
        await app.StopAsync();
    }

    /// <summary>The shell-form lines through the shells that run them on Windows, with a socket path that needs quoting.</summary>
    [Amc.Tests.Platform.WindowsFact]
    public async Task Cmd_and_powershell_lines_run_in_the_real_shells()
    {
        var dir = Path.Combine(_dir, "it's a dir");
        Directory.CreateDirectory(dir);
        var socket = Path.Combine(dir, ClaudeHooksConfig.SocketFileName);
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket));
        await using var app = b.Build();
        app.MapPost("/hooks/{tool}", async (string tool, HttpRequest r) => Results.Text($"{tool}{r.QueryString}:" + await new StreamReader(r.Body).ReadToEndAsync()));
        await app.StartAsync();

        var f = new HookForwarder(DotnetHost(), [typeof(HookForward).Assembly.Location]);
        var codex = ToolHooksConfig.BuildWindowsCommand(ToolHooksConfig.Profiles["codex"], f, socket, "Stop");
        var gemini = ToolHooksConfig.BuildWindowsCommand(ToolHooksConfig.Profiles["gemini"], f, socket, "AfterTool");
        const string payload = """{"session_id":"s1"}""";

        // Codex: cmd.exe /C "<line>" (raw); Node's shell:true: cmd.exe /d /s /c "<line>".
        Assert.Equal((0, "codex?e=Stop:" + payload, ""), await Spawn(new ProcessStartInfo("cmd.exe") { Arguments = $"/C \"{codex}\"" }, payload));
        Assert.Equal((0, "codex?e=Stop:" + payload, ""), await Spawn(new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{codex}\"" }, payload));
        // Gemini CLI: powershell.exe -NoProfile -NonInteractive -Command <line> (pwsh when installed).
        var ps = new ProcessStartInfo("powershell.exe");
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", gemini }) ps.ArgumentList.Add(a);
        Assert.Equal((0, "gemini?e=AfterTool:" + payload, ""), await Spawn(ps, payload));
        await app.StopAsync();
    }
}
