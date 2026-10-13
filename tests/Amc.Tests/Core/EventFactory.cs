using Amc.Core.Domain;
using Amc.Core.Events;

namespace Amc.Tests.Core;

/// <summary>Builds events the way collectors do, with monotonically increasing seq and time.</summary>
internal sealed class EventFactory
{
    private long _seq;
    private DateTimeOffset _now = new(2026, 10, 7, 11, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Now => _now;
    public void Advance(TimeSpan by) => _now += by;

    public AgentEvent Make<T>(string type, T payload, string? agent = null, string? wt = null, string? file = null,
        Evidence evidence = Evidence.Reported, string? repo = null) where T : class
    {
        _now += TimeSpan.FromSeconds(1);
        return new AgentEvent
        {
            Seq = ++_seq, Ts = _now, Type = type, Source = EventSources.Hook, Evidence = evidence,
            AgentId = agent, WorktreeId = wt, FilePath = file, RepositoryId = repo,
            Data = Json.ToObject(payload),
        };
    }

    public AgentEvent Main(string id, int pid = 100) =>
        Make(EventTypes.AgentCreated, new AgentCreatedData(AgentKind.Main, "claude", Pid: pid, ProcStart: "1"), id);

    public AgentEvent Sub(string id, string parent, string type = "general-purpose") =>
        Make(EventTypes.AgentSpawned, new AgentCreatedData(AgentKind.Sub, "claude", ParentId: parent, AgentType: type, Title: id), id);

    public AgentEvent Status(string id, AgentStatus s, string reason = "test") =>
        Make(EventTypes.AgentStatusChanged, new AgentStatusData(s, reason), id);

    public AgentEvent Repo(string repoId) =>
        Make(EventTypes.RepositoryDiscovered, new RepositoryData("repo", "/r/.git", "/r"), repo: repoId, evidence: Evidence.Observed);

    public AgentEvent Worktree(string wtId, string root, string repoId, int modified = 0, int conflicted = 0, string? op = null) =>
        Make(EventTypes.WorktreeChanged, new WorktreeData(root, repoId, "main", "abc", Modified: modified, Conflicted: conflicted, Operation: op),
            wt: wtId, repo: repoId, evidence: Evidence.Observed);

    public AgentEvent Edit(string agent, string wt, string path, string type = EventTypes.FileModified) =>
        Make(type, new FileChangeData(ToolUseId: "t" + _seq, Tool: "Edit"), agent, wt, path);

    public AgentEvent GitStatus(string wt, string path, string status, string? attributed = null) =>
        Make(EventTypes.GitFileStatus, new GitFileStatusData(status, 1, 0, AttributedAgentId: attributed), wt: wt, file: path,
            evidence: attributed is null ? Evidence.Observed : Evidence.Inferred);
}
