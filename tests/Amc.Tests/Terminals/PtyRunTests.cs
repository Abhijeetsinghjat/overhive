using System.Text;
using Amc.Cli.Run;
using Amc.Core.Events;

namespace Amc.Tests.Terminals;

/// <summary>Real PTY tests: only processes started by these tests are ever signalled.</summary>
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

    [Fact]
    public async Task CapturesOutput_AndExitCode()
    {
        var (code, output) = await Run(["/bin/sh", "-c", "printf hi; exit 3"]);
        Assert.Equal(3, code);
        Assert.Equal("hi", output);
    }

    [Fact]
    public async Task ChildHasControllingPts()
    {
        var (code, output) = await Run(["/bin/sh", "-c", "tty; echo; test -t 0 && test -t 1 && test -t 2 && echo ALLTTY"]);
        Assert.Equal(0, code);
        Assert.Matches(@"^/dev/pts/\d+", output); // note: this distro's tty(1) omits the trailing newline
        Assert.Contains("ALLTTY", output);
    }

    [Fact]
    public async Task ChildIsSessionLeader_WithTerminalIdEnv_AndCwd_AndSize()
    {
        var dir = Path.GetTempPath().TrimEnd('/');
        var (code, output) = await Run(
            ["/bin/sh", "-c", "echo id=$AMC_TERMINAL_ID; pwd; stty size; [ \"$(ps -o sid= -p $$ | tr -d ' ')\" = \"$$\" ] && echo LEADER"],
            new RunOptions { TerminalId = "TEST123", Cwd = dir, Cols = 101, Rows = 33 });
        Assert.Equal(0, code);
        Assert.Contains("id=TEST123", output);
        Assert.Contains(Path.GetFullPath(dir), output);
        Assert.Contains("33 101", output);
        Assert.Contains("LEADER", output);
    }

    [Fact]
    public async Task InputIsRelayed()
    {
        var (code, output) = await Run(["/bin/sh", "-c", "read x; echo got:$x"], new RunOptions { Input = new MemoryStream("abc\n"u8.ToArray()) });
        Assert.Equal(0, code);
        Assert.Contains("got:abc", output);
    }

    [Fact]
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
    }

    [Fact]
    public async Task DaemonUnreachable_StillRunsLocally()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (code, output) = await Run(["/bin/sh", "-c", "printf ok; exit 5"], new RunOptions { DaemonUrl = "http://127.0.0.1:1", Token = "x" });
        Assert.Equal(5, code);
        Assert.Equal("ok", output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ScriptFallback_CapturesOutput_AndExitCode()
    {
        if (!File.Exists("/usr/bin/script") && !File.Exists("/bin/script")) return;
        var (code, output) = await Run(["/bin/sh", "-c", "printf hi; exit 3"], new RunOptions { UseScriptFallback = true });
        Assert.Equal(3, code);
        Assert.Contains("hi", output);
    }

    [Fact]
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
