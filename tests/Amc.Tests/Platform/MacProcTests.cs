using System.Buffers.Binary;
using System.Text;
using Amc.Daemon.Platform;

namespace Amc.Tests.Platform;

public class MacProcTests
{
    [Fact]
    public void Procargs_layout_is_split_into_argv_and_environment()
    {
        var body = Encoding.UTF8.GetBytes("/usr/bin/node\0\0\0\0node\0/opt/codex.js\0--model\0gpt-5\0HOME=/Users/a\0TERM=xterm\0\0\0");
        var b = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(b, 4);
        body.CopyTo(b, 4);
        var (argv, env) = MacProc.ParseProcArgs(b);
        Assert.Equal(["node", "/opt/codex.js", "--model", "gpt-5"], argv);
        Assert.Equal(["HOME=/Users/a", "TERM=xterm"], env);
    }

    // Live checks against this test process; they only mean something on macOS.
    [Fact]
    public void Reads_this_process_on_macOS()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var self = Environment.ProcessId;
        Assert.Contains(self, MacProc.ListPids());

        var p = MacProc.Basics(self)!;
        Assert.Equal(self, p.Pid);
        Assert.True(p.Ppid > 0);
        Assert.False(string.IsNullOrEmpty(p.Name));
        Assert.Equal('R', p.State is 'S' ? 'R' : p.State);
        Assert.True(p.CpuTime > TimeSpan.Zero);
        Assert.True(p.RssBytes > 1 << 20);
        var started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        Assert.InRange((p.StartedAt.UtcDateTime - started).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal(p.StartId, MacProc.Basics(self)!.StartId);

        var cwd = MacProc.Cwd(self)!;
        // macOS reports the real path (/private/var/…), the runtime may report the symlinked one (/var/…).
        Assert.True(Directory.Exists(cwd));
        Assert.Equal(Path.GetFileName(Environment.CurrentDirectory.TrimEnd('/')), Path.GetFileName(cwd.TrimEnd('/')));

        var (argv, env) = MacProc.Args(self)!.Value;
        Assert.NotEmpty(argv);
        Assert.Contains(env, e => e.StartsWith("PATH=", StringComparison.Ordinal));
    }
}
