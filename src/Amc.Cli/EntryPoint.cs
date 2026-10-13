using System.Text;
using Amc.Cli.Infrastructure;

namespace Amc.Cli;

// Explicit entry class (no top-level statements) so it never collides with the daemon's
// global `Program` type, which the test project also references.
public static class EntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // second Ctrl+C: let the runtime terminate
            e.Cancel = true;
            cts.Cancel();
        };
        var ctx = CliContext.ForConsole();
        var code = await Router.RunAsync(args, ctx, cts.Token);
        await ctx.Out.FlushAsync();
        return code;
    }
}
