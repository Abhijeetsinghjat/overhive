using System.Net;
using System.Net.Sockets;
using System.Text;
using Amc.Cli;
using Amc.Cli.Infrastructure;
using Amc.Core.Domain;
using Amc.Core.Events;

namespace Amc.Tests.Cli;

/// <summary>Recorded request seen by <see cref="FakeDaemon"/>.</summary>
public sealed record SeenRequest(HttpMethod Method, Uri Uri, string? Body, string? Token);

/// <summary>In-memory stand-in for amcd: route table keyed by "METHOD path" (path without query).</summary>
public sealed class FakeDaemon : HttpMessageHandler
{
    private readonly Dictionary<string, Func<SeenRequest, (HttpStatusCode, string)>> _routes = new();
    public List<SeenRequest> Requests { get; } = [];
    public bool Unreachable { get; set; }

    public FakeDaemon On(string method, string path, Func<SeenRequest, (HttpStatusCode, string)> respond)
    {
        _routes[$"{method} {path}"] = respond;
        return this;
    }

    public FakeDaemon Get(string path, string json) => On("GET", path, _ => (HttpStatusCode.OK, json));
    public FakeDaemon Get<T>(string path, T value) => Get(path, Json.Serialize(value));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Unreachable) throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var seen = new SeenRequest(request.Method, request.RequestUri!, body,
            request.Headers.TryGetValues(AmcClient.TokenHeader, out var t) ? t.First() : null);
        Requests.Add(seen);
        var key = $"{request.Method.Method} {request.RequestUri!.AbsolutePath}";
        if (!_routes.TryGetValue(key, out var route))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"no route"}""", Encoding.UTF8, "application/json") };
        var (status, json) = route(seen);
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public SeenRequest Single(string method, string pathContains) =>
        Requests.Single(r => r.Method.Method == method && r.Uri.AbsolutePath.Contains(pathContains));
}

public sealed class CliHarness : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public string Dir { get; } = Directory.CreateTempSubdirectory("amc-cli-test-").FullName;
    public FakeDaemon Daemon { get; } = new();
    public StringWriter Out { get; } = new();
    public StringWriter Err { get; } = new();

    public CliContext Context(string input = "", bool interactive = false) => new()
    {
        Out = Out,
        Err = Err,
        In = new StringReader(input),
        Interactive = interactive,
        Color = false,
        Env = name => name switch { "PATH" => "", _ => null },
        Home = Dir,
        Cwd = Dir,
        Now = () => Now,
        HttpHandler = Daemon,
        TokenOverride = "test-token",
        SettingsOverride = new CliSettings(new Uri("http://127.0.0.1:47821/"), 47821, Path.Combine(Dir, "state"),
            Path.Combine(Dir, "state", "token"), Path.Combine(Dir, "config.json"), Path.Combine(Dir, ".claude")),
    };

    public async Task<int> Run(string input, bool interactive, params string[] argv) =>
        await Router.RunAsync(argv, Context(input, interactive), CancellationToken.None);

    public Task<int> Run(params string[] argv) => Run("", false, argv);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }

    public static Agent Agent(string id, string? title = null, AgentStatus status = AgentStatus.Running, string? parent = null,
        TimeSpan? lastActivityAgo = null, int? pid = null) => new()
    {
        Id = id,
        Provider = "claude",
        Kind = parent is null ? AgentKind.Main : AgentKind.Sub,
        ParentId = parent,
        Title = title,
        Status = status,
        Pid = pid,
        CreatedAt = Now - TimeSpan.FromHours(3),
        LastActivityAt = Now - (lastActivityAgo ?? TimeSpan.FromSeconds(5)),
        CompletedAt = AgentStateMachine.IsTerminal(status) ? Now - (lastActivityAgo ?? TimeSpan.FromSeconds(5)) : null,
    };
}
