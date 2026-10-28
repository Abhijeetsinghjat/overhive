using System.Text;
using Amc.Cli.Run;
using Amc.Core.Events;

namespace Amc.Tests.Terminals;

/// <summary>
/// Real PTY tests (Unix PTY or Windows ConPTY, by OS): only processes started by these tests are ever signalled.
/// ConPTY repaints the screen, so Windows assertions look for text inside the output rather than exact bytes.
/// </summary>
public class PtyRunTests
{
    private static async Task<(int Code, string Output)> Run(string[] cmd, RunOptions? o = null)
    {
        var output = new MemoryStream();
        o ??= new RunOptions();
        o.Output = output;
        o.Input ??= new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var code = await RunCommand.RunAsync(cmd, o, cts.Token);
        return (code, Encoding.UTF8.GetString(output.ToArray()));
    }

    [UnixFact]
    public async Task CapturesOutput_AndExitCode()
    {
        var (code, output) = await Run(["/bin/sh", "-c", "printf hi; exit 3"]);
        Assert.Equal(3, code);
        Assert.Equal("hi", output);
    }

    [UnixFact]
    public async Task ChildHasControllingPts()
    {
        var (code, output) = await Run(["/bin/sh", "-c", "tty; echo; test -t 0 && test -t 1 && test -t 2 && echo ALLTTY"]);
        Assert.Equal(0, code);
        Assert.Matches(@"^/dev/(pts/\d+|ttys\d+)", output); // Linux /dev/pts/N, macOS /dev/ttysNNN
        Assert.Contains("ALLTTY", output);
    }

    [UnixFact]
    public async Task ChildIsSessionLeader_WithTerminalIdEnv_AndCwd_AndSize()
    {
        var dir = Path.GetTempPath().TrimEnd('/');
        var (code, output) = await Run(
            ["/bin/sh", "-c", "echo id=$AMC_TERMINAL_ID; pwd; stty size; ps -o stat= -p $$ | grep -q s && echo LEADER"],
            new RunOptions { TerminalId = "TEST123", Cwd = dir, Cols = 101, Rows = 33 });
        Assert.Equal(0, code);
        Assert.Contains("id=TEST123", output);
        Assert.Contains(Path.GetFullPath(dir), output);
        Assert.Contains("33 101", output);
        Assert.Contains("LEADER", output);
    }

    [UnixFact]
    public async Task InputIsRelayed()
    {
        var (code, output) = await Run(["/bin/sh", "-c", "read x; echo got:$x"], new RunOptions { Input = new MemoryStream("abc\n"u8.ToArray()) });
        Assert.Equal(0, code);
        Assert.Contains("got:abc", output);
    }

    [UnixFact]
    public async Task KilledBySignal_Returns128PlusSignal()
    {
        var (code, _) = await Run(["/bin/sh", "-c", "kill -TERM $$"]);
        Assert.Equal(128 + 15, code);
    }

    [Fact]
    public async Task CommandNotFound_Returns127()
    {
        var (code, _) = await Run(["/definitely/not/here-amc"]);
        Assert.Equal(127, code);
        (code, _) = await Run(["definitely-not-here-amc"]);
        Assert.Equal(127, code);
    }

    // ---- Windows (ConPTY) -------------------------------------------------------------------------------

    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static string[] PowerShell(string script) => ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script];

    [WindowsFact]
    public async Task Win_CapturesOutput_AndExitCode()
    {
        var (code, output) = await Run([Cmd, "/d", "/c", "echo hi& exit 3"]);
        Assert.Equal(3, code);
        Assert.Contains("hi", output);
    }

    [WindowsFact]
    public async Task Win_ChildIsOnTheConsole_NotOnPipes()
    {
        var (code, output) = await Run(PowerShell("'redirected=' + [Console]::IsInputRedirected + '/' + [Console]::IsOutputRedirected"));
        Assert.Equal(0, code);
        Assert.Contains("redirected=False/False", output);
    }

    [WindowsFact]
    public async Task Win_TerminalIdEnv_AndCwd_AndSize()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "amc-cwd-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var (code, output) = await Run(
                PowerShell("'id=' + $env:AMC_TERMINAL_ID; 'cwd=' + (Split-Path -Leaf (Get-Location).Path); 'size=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight"),
                new RunOptions { TerminalId = "TEST123", Cwd = dir, Cols = 101, Rows = 33 });
            Assert.Equal(0, code);
            Assert.Contains("id=TEST123", output);
            Assert.Contains("cwd=" + Path.GetFileName(dir), output);
            Assert.Contains("size=101x33", output);
        }
        finally { Directory.Delete(dir, true); }
    }

    [WindowsFact]
    public async Task Win_InputIsRelayed()
    {
        var (code, output) = await Run([Cmd, "/d", "/v:on", "/c", "set /p x=& echo got:!x!"], new RunOptions { Input = new MemoryStream("abc\r"u8.ToArray()) });
        Assert.Equal(0, code);
        Assert.Contains("got:abc", output);
    }

    [WindowsFact]
    public async Task Win_DaemonUnreachable_StillRunsLocally()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (code, output) = await Run([Cmd, "/d", "/c", "echo ok& exit 5"], new RunOptions { DaemonUrl = "http://127.0.0.1:1", Token = "x" });
        Assert.Equal(5, code);
        Assert.Contains("ok", output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The grandchild gets its own (hidden) console, so closing the pseudo console does not reach it: only the
    /// kill-on-close job object ends it when the run is over.
    /// </summary>
    [WindowsFact]
    public async Task Win_ChildTree_IsKilled_WhenRunEnds()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "amc-grandchild-" + Guid.NewGuid().ToString("N") + ".pid");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var o = new RunOptions { Output = new MemoryStream(), Input = new BlockingInput() };
        var run = RunCommand.RunAsync(PowerShell(
            "$p = Start-Process -PassThru -WindowStyle Hidden -FilePath ping.exe -ArgumentList '-n','120','127.0.0.1'; " +
            $"Set-Content -Path '{pidFile}' -Value $p.Id; Start-Sleep -Seconds 120"), o, cts.Token);
        try
        {
            await TerminalHubTests.WaitUntil(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0, 30_000);
            using var grandchild = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim()));
            Assert.False(grandchild.HasExited);
            await cts.CancelAsync(); // like agentctl going away: hang up, then the job closes with the run
            await run.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(grandchild.WaitForExit(10_000), "grandchild survived the run");
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [WindowsFact]
    public async Task Win_EndToEnd_MirrorsToDaemon_AndAcceptsInjectedInput()
    {
        await using var host = await TerminalHost.StartAsync(kestrel: true);
        var input = new BlockingInput();
        var o = new RunOptions { DaemonUrl = host.BaseUrl, TerminalId = "E2EW", Input = input };
        var run = Run([Cmd, "/d", "/v:on", "/c", "echo ready& set /p x=& echo got:!x!& exit 7"], o);

        var started = await host.Sink.WaitFor(EventTypes.TerminalStarted);
        Assert.Equal("E2EW", started.Payload<TerminalData>()!.TerminalId);
        Assert.True(started.Payload<TerminalData>()!.Pid > 0);
        await TerminalHubTests.WaitUntil(() => Encoding.UTF8.GetString(host.Hub.Buffer("E2EW")).Contains("ready"));
        Assert.True(await host.Hub.WriteAsync("E2EW", "injected\r"u8.ToArray(), CancellationToken.None));

        var (code, output) = await run;
        input.Complete();
        Assert.Equal(7, code);
        Assert.Contains("got:injected", output);
        var exited = await host.Sink.WaitFor(EventTypes.TerminalExited);
        Assert.Equal(7, exited.Payload<TerminalData>()!.ExitCode);
        Assert.Contains("got:injected", Encoding.UTF8.GetString(host.Hub.Buffer("E2EW")));
    }

    [UnixFact]
    public async Task DaemonUnreachable_StillRunsLocally()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (code, output) = await Run(["/bin/sh", "-c", "printf ok; exit 5"], new RunOptions { DaemonUrl = "http://127.0.0.1:1", Token = "x" });
        Assert.Equal(5, code);
        Assert.Equal("ok", output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [UnixFact]
    public async Task ScriptFallback_CapturesOutput_AndExitCode()
    {
        if (!File.Exists("/usr/bin/script") && !File.Exists("/bin/script")) return;
        var (code, output) = await Run(["/bin/sh", "-c", "printf hi; exit 3"], new RunOptions { UseScriptFallback = true });
        Assert.Equal(3, code);
        Assert.Contains("hi", output);
    }

    [UnixFact]
    public async Task EndToEnd_MirrorsToDaemon_AndAcceptsInjectedInput()
    {
        await using var host = await TerminalHost.StartAsync(kestrel: true);
        var input = new BlockingInput();
        var o = new RunOptions { DaemonUrl = host.BaseUrl, TerminalId = "E2E1", Input = input };
        // The child waits for one line that only the daemon can provide.
        var run = Run(["/bin/sh", "-c", "printf ready; read x; echo got:$x; exit 7"], o);

        var started = await host.Sink.WaitFor(EventTypes.TerminalStarted);
        Assert.Equal("E2E1", started.Payload<TerminalData>()!.TerminalId);
        Assert.True(started.Payload<TerminalData>()!.Pid > 0);
        await TerminalHubTests.WaitUntil(() => Encoding.UTF8.GetString(host.Hub.Buffer("E2E1")).Contains("ready"));
        Assert.True(await host.Hub.WriteAsync("E2E1", "injected\n"u8.ToArray(), CancellationToken.None));

        var (code, output) = await run;
        input.Complete();
        Assert.Equal(7, code);
        Assert.Contains("got:injected", output);
        var exited = await host.Sink.WaitFor(EventTypes.TerminalExited);
        Assert.Equal(7, exited.Payload<TerminalData>()!.ExitCode);
        Assert.Contains("got:injected", Encoding.UTF8.GetString(host.Hub.Buffer("E2E1")));
    }

    /// <summary>A stdin that never yields data until completed (like an idle keyboard).</summary>
    private sealed class BlockingInput : Stream
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete() => _done.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _done.Task.WaitAsync(ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
