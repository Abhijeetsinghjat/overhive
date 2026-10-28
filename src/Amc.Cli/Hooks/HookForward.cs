using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Amc.Cli.Infrastructure;
using Amc.Core.Providers;
using Amc.Core.Providers.Claude;

namespace Amc.Cli.Hooks;

/// <summary>
/// <c>agentctl hook-forward &lt;tool&gt; [--event E] [--wait] [--socket PATH]</c>: the native replacement for the curl
/// hook one-liner, used by the hooks installed on Windows. Reads the hook payload on stdin, POSTs it to the daemon's
/// private Unix socket and copies a successful reply to stdout. Fails open exactly like <c>curl -sf … || true</c>:
/// daemon down, slow, or answering with an error means no output and exit 0, so the tool is never blocked or fed
/// error text. With <c>--wait</c> it is Claude Code's asyncRewake waiter: a non-empty reply goes to stderr and the
/// exit code is 2 (wakes the session); every run is logged to rewake.log next to the socket.
/// </summary>
public static class HookForward
{
    /// <summary>One forward: the daemon route is derived from tool, event and wait.</summary>
    public sealed record Request(string Tool, string? Event, bool Wait, string SocketPath)
    {
        /// <summary>Same routes the curl commands use.</summary>
        public string PathAndQuery =>
            Tool == "claude" ? (Wait ? "/hooks/claude/wait" : "/hooks/claude")
            : $"/hooks/{Tool}?e={Uri.EscapeDataString(Event ?? "")}";

        /// <summary>curl's -m: 3 s per hook; the waiter is released by the daemon before Claude Code's hook timeout.</summary>
        public TimeSpan Timeout => Wait ? TimeSpan.FromSeconds(ClaudeHooksConfig.RewakeTimeoutSeconds - 60) : TimeSpan.FromSeconds(3);
    }

    private static readonly Regex ToolName = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses the arguments after <c>hook-forward</c>. Lenient on purpose (unknown tokens such as <c>--tag</c> are
    /// ignored) because a usage error must never become a hook failure. Null when there is nothing sensible to do.
    /// </summary>
    public static Request? Parse(IReadOnlyList<string> argv, Func<string?> defaultSocket)
    {
        string? tool = null, evt = null, socket = null;
        var wait = false;
        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            string? Next() => i + 1 < argv.Count ? argv[++i] : null;
            switch (a)
            {
                case "--event": evt = Next(); break;
                case "--socket": socket = Next(); break;
                case "--tag": Next(); break;
                case "--wait": wait = true; break;
                default:
                    if (!a.StartsWith('-') && tool is null) tool = a;
                    break;
            }
        }
        if (tool is null || !ToolName.IsMatch(tool)) return null;
        socket ??= defaultSocket();
        return socket is { Length: > 0 } ? new Request(tool, evt, wait, socket) : null;
    }

    /// <summary>Process entry (called before any other CLI setup): never throws, exit 0 unless a waiter wakes Claude.</summary>
    public static async Task<int> MainAsync(string[] argv)
    {
        try
        {
            var request = Parse(argv, DefaultSocket);
            if (request is null) return 0;
            using var stdin = Console.IsInputRedirected ? Console.OpenStandardInput() : Stream.Null;
            using var stdout = Console.OpenStandardOutput();
            using var stderr = Console.OpenStandardError();
            return await RunAsync(request, stdin, stdout, stderr, CancellationToken.None);
        }
        catch
        {
            return 0;
        }
    }

    private static string? DefaultSocket()
    {
        var settings = CliSettings.Load(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return Path.Combine(settings.DataDir, ClaudeHooksConfig.SocketFileName);
    }

    /// <summary>Forwards one hook. <paramref name="timeout"/> overrides <see cref="Request.Timeout"/> (tests).</summary>
    public static async Task<int> RunAsync(Request r, Stream stdin, Stream stdout, Stream stderr, CancellationToken ct, TimeSpan? timeout = null)
    {
        var log = r.Wait ? Path.Combine(Path.GetDirectoryName(r.SocketPath) ?? ".", ClaudeHooksConfig.RewakeLogFileName) : null;
        Log(log, $"start pid={Environment.ProcessId}");
        var (result, body) = await PostAsync(r, stdin, timeout ?? r.Timeout, ct);
        var text = body is null ? "" : Encoding.UTF8.GetString(body);
        Log(log, $"result={result} bytes={text.TrimEnd('\n').Length}");
        if (body is null) return 0;
        try
        {
            if (!r.Wait)
            {
                await stdout.WriteAsync(body, ct);
                await stdout.FlushAsync(ct);
                return 0;
            }
            // Like m=$(curl …): trailing newlines dropped; a non-empty message wakes the session (exit 2).
            text = text.TrimEnd('\n');
            if (text.Length == 0) return 0;
            await stderr.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"), ct);
            await stderr.FlushAsync(ct);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return 0; // the tool closed our output: nothing left to do
        }
    }

    /// <summary>
    /// The request. Result codes reuse curl's so rewake.log reads the same on every OS: 0 ok, 7 cannot connect,
    /// 22 HTTP error (no body), 28 timed out, 1 anything else.
    /// </summary>
    private static async Task<(int Result, byte[]? Body)> PostAsync(Request r, Stream stdin, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var connected = false;
        try
        {
            var payload = new MemoryStream();
            await stdin.CopyToAsync(payload, cts.Token);
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(r.SocketPath), token);
                        connected = true;
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var content = new ByteArrayContent(payload.ToArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await http.PostAsync(new Uri("http://localhost" + r.PathAndQuery), content, cts.Token);
            if (!response.IsSuccessStatusCode) return (22, null);
            return (0, await response.Content.ReadAsByteArrayAsync(cts.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (28, null);
        }
        catch (HttpRequestException)
        {
            return (connected ? 1 : 7, null);
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            return (1, null);
        }
    }

    private static void Log(string? path, string line)
    {
        if (path is null) return;
        try { File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss} {line}\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The forwarder for this agentctl: its apphost, or the dotnet host plus agentctl.dll.</summary>
    public static HookForwarder Current()
    {
        var process = Environment.ProcessPath ?? throw new CliException("cannot determine the path of agentctl");
        var name = Path.GetFileNameWithoutExtension(process);
        if (!name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return new HookForwarder(Path.GetFullPath(process));
        var dll = typeof(HookForward).Assembly.Location;
        return new HookForwarder(process, [dll]);
    }
}
