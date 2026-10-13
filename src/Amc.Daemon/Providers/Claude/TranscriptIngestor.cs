using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Daemon.Infrastructure;
using Amc.Daemon.Pipeline;
using Microsoft.Extensions.Options;

namespace Amc.Daemon.Providers.Claude;

/// <summary>
/// Tails Claude Code transcripts (~/.claude/projects/&lt;slug&gt;/&lt;session&gt;.jsonl and
/// &lt;session&gt;/subagents/agent-&lt;id&gt;.jsonl) incrementally. Byte offsets are persisted
/// per file so a restart never re-ingests; a session is always processed as a unit
/// (main transcript first, then sub-agents by spawn depth) so spawn parents resolve.
/// </summary>
public sealed partial class TranscriptIngestor
{
    public const string HealthName = "claude-transcripts";
    private const int ChunkSize = 1 << 20;
    private const int FingerprintBytes = 1024;
    private static readonly TimeSpan MetaGrace = TimeSpan.FromSeconds(30);

    private readonly ClaudeEventMapper _mapper;
    private readonly ClaudeContext _ctx;
    private readonly IEventSink _sink;
    private readonly ICursorStore _cursors;
    private readonly HealthRegistry _health;
    private readonly AmcOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TranscriptIngestor(ClaudeEventMapper mapper, IEventSink sink, ICursorStore cursors, HealthRegistry health,
        IOptions<AmcOptions> options, TimeProvider time)
    {
        _mapper = mapper; _ctx = mapper.Context; _sink = sink; _cursors = cursors; _health = health; _options = options.Value; _time = time;
        _health.Register(HealthName, TimeSpan.FromMinutes(2));
    }

    public string ProjectsDir => Path.Combine(_options.ClaudeHome, "projects");

    /// <summary>Optional observer (dry runs / diagnostics) — sees every event before it is published.</summary>
    public Action<AgentEvent>? Observer { get; set; }

    public async Task ScanAllAsync(CancellationToken ct)
    {
        if (!Directory.Exists(ProjectsDir)) { _health.Degraded(HealthName, "no projects directory: " + ProjectsDir); return; }
        var sessions = 0;
        foreach (var dir in SafeEnum(() => Directory.EnumerateDirectories(ProjectsDir)))
        foreach (var file in SafeEnum(() => Directory.EnumerateFiles(dir, "*.jsonl")))
        {
            ct.ThrowIfCancellationRequested();
            await ProcessSessionAsync(dir, Path.GetFileNameWithoutExtension(file), ct);
            sessions++;
        }
        _health.SetCounter(HealthName, "sessions", sessions);
        _health.Ok(HealthName, $"{sessions} session transcript(s) tracked");
    }

    /// <summary>Maps a changed file under projects/ to its session; null if it is not a transcript or sub-agent file.</summary>
    public (string ProjectDir, string SessionId)? SessionOf(string path)
    {
        var rel = Path.GetRelativePath(ProjectsDir, path);
        if (rel.StartsWith("..", StringComparison.Ordinal)) return null;
        var parts = rel.Split(Path.DirectorySeparatorChar);
        var projectDir = Path.Combine(ProjectsDir, parts[0]);
        if (parts.Length == 2 && parts[1].EndsWith(".jsonl", StringComparison.Ordinal)) return (projectDir, parts[1][..^".jsonl".Length]);
        if (parts.Length >= 4 && parts[2] == "subagents") return (projectDir, parts[1]);
        return null;
    }

    public async Task ProcessSessionAsync(string projectDir, string sessionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var main = Path.Combine(projectDir, sessionId + ".jsonl");
            var mainId = ClaudePaths.MainId(sessionId);
            if (File.Exists(main)) await ProcessFileAsync(main, sessionId, mainId, true, ct);

            var subDir = Path.Combine(projectDir, sessionId, "subagents");
            if (!Directory.Exists(subDir)) return;
            var subs = SafeEnum(() => Directory.EnumerateFiles(subDir, "*.jsonl"))
                .Select(f => (File: f, Meta: ReadMeta(Path.ChangeExtension(f, ".meta.json"))))
                .OrderBy(x => x.Meta?.SpawnDepth ?? 99).ThenBy(x => File.GetLastWriteTimeUtc(x.File))
                .ToList();
            foreach (var (file, meta) in subs)
            {
                ct.ThrowIfCancellationRequested();
                var childId = ClaudePaths.SubId(sessionId, Path.GetFileNameWithoutExtension(file));
                if (!WillRead(file)) continue;
                if (!_ctx.SpawnResolved.ContainsKey(childId))
                {
                    var pre = SpawnFor(file, sessionId, childId, meta, projectDir);
                    if (pre is null) continue; // meta not written yet: wait for it
                    await PublishAsync(pre, ct);
                }
                await ProcessFileAsync(file, sessionId, childId, false, ct);
            }
        }
        finally
        {
            try
            {
                if (_mapper.FlushChildEnds(sessionId) is { Count: > 0 } ends) await PublishAsync(ends, ct);
            }
            finally { _gate.Release(); }
        }
    }

    public sealed record SubagentMeta(string? AgentType, string? Description, string? ToolUseId, int? SpawnDepth);

    private static SubagentMeta? ReadMeta(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
            int? depth = o["spawnDepth"] is JsonValue v && v.TryGetValue<int>(out var d) ? d : null;
            return new SubagentMeta(ClaudeEventMapper.Str(o, "agentType"), ClaudeEventMapper.Str(o, "description"),
                ClaudeEventMapper.Str(o, "toolUseId"), depth);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The agent.spawned events for a sub-agent file, or null to retry later.</summary>
    private List<AgentEvent>? SpawnFor(string file, string sessionId, string childId, SubagentMeta? meta, string projectDir)
    {
        var o = new Origin(EventSources.Transcript, sessionId, childId, FirstTimestamp(file) ?? _time.GetUtcNow());
        var transcriptPath = file;
        if (meta?.ToolUseId is null)
        {
            if (_time.GetUtcNow() - File.GetLastWriteTimeUtc(file) < MetaGrace) return null;
            if (_ctx.IsKnown(childId)) return [];
            return _mapper.Spawned(o, childId, null, meta?.AgentType, meta?.Description, null, transcriptPath, parentResolved: false);
        }
        string? parent = _ctx.Spawns.TryGetValue(meta.ToolUseId, out var sp) ? sp.ParentAgentId : null;
        parent ??= FindToolUseOwner(projectDir, sessionId, meta.ToolUseId);
        if (parent is null && meta.SpawnDepth is null or <= 1) parent = ClaudePaths.MainId(sessionId);
        if (sp is not null) sp.ChildAgentId ??= childId;
        else _ctx.Spawns.TryAdd(meta.ToolUseId, new SpawnRequest(meta.ToolUseId, sessionId, parent ?? "", meta.AgentType, meta.Description, o.Ts) { ChildAgentId = childId });
        return _mapper.Spawned(o, childId, parent, meta.AgentType, meta.Description, meta.ToolUseId, transcriptPath, parent is not null);
    }

    /// <summary>Which transcript of the session contains the tool_use with this id (cold-cache fallback after a restart).</summary>
    private static string? FindToolUseOwner(string projectDir, string sessionId, string toolUseId)
    {
        var needle = $"\"id\":\"{toolUseId}\"";
        var candidates = new List<(string File, string AgentId)> { (Path.Combine(projectDir, sessionId + ".jsonl"), ClaudePaths.MainId(sessionId)) };
        var subDir = Path.Combine(projectDir, sessionId, "subagents");
        if (Directory.Exists(subDir))
            candidates.AddRange(SafeEnum(() => Directory.EnumerateFiles(subDir, "*.jsonl"))
                .Select(f => (f, ClaudePaths.SubId(sessionId, Path.GetFileNameWithoutExtension(f)))));
        foreach (var (file, agentId) in candidates)
        {
            try
            {
                if (!File.Exists(file)) continue;
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                string? line;
                while ((line = reader.ReadLine()) is not null)
                    if (line.Contains(needle, StringComparison.Ordinal) && line.Contains("\"tool_use\"", StringComparison.Ordinal)) return agentId;
            }
            catch (IOException) { }
        }
        return null;
    }

    private static DateTimeOffset? FirstTimestamp(string file)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            for (var i = 0; i < 5 && reader.ReadLine() is { } line; i++)
                if (TryParse(line) is { } r && Ts(r) is { } ts) return ts;
        }
        catch (IOException) { }
        return null;
    }

    // ---- per-file incremental reading --------------------------------------------------

    private sealed record Cursor(long Offset, int FpLen, string FpHash)
    {
        public override string ToString() => $"{Offset}:{FpLen}:{FpHash}";
        public Cursor With((int Len, string Hash) fp) => this with { FpLen = fp.Len, FpHash = fp.Hash };
        public static Cursor? Parse(string? s)
        {
            var p = s?.Split(':');
            return p is { Length: 3 } && long.TryParse(p[0], CultureInfo.InvariantCulture, out var off) && int.TryParse(p[1], CultureInfo.InvariantCulture, out var len)
                ? new Cursor(off, len, p[2]) : null;
        }
    }

    private static string CursorKey(string path) => "transcript:" + path;

    /// <summary>True if the file has a cursor or is new enough to be backfilled.</summary>
    private bool WillRead(string path) =>
        _cursors.Get(CursorKey(path)) is not null ||
        File.GetLastWriteTimeUtc(path) >= _time.GetUtcNow().UtcDateTime.AddHours(-_options.BackfillHours);

    private sealed class FileState(string path, string sessionId, string agentId, bool isMain)
    {
        public string Path { get; } = path;
        public string SessionId { get; } = sessionId;
        public string AgentId { get; } = agentId;
        public bool IsMain { get; } = isMain;
        public string? Cwd { get; set; }
    }

    public async Task ProcessFileAsync(string path, string sessionId, string agentId, bool isMain, CancellationToken ct)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) return; }
        catch (IOException) { return; }

        var key = CursorKey(path);
        var cursor = Cursor.Parse(_cursors.Get(key));
        long offset;
        if (cursor is null)
        {
            if (fi.LastWriteTimeUtc < _time.GetUtcNow().UtcDateTime.AddHours(-_options.BackfillHours))
            {
                // Outside the backfill window: start tailing at the end (aligned to a line boundary).
                var end = LastLineBoundary(path, fi.Length);
                _cursors.Set(key, new Cursor(end, 0, "").With(Fingerprint(path, end)).ToString());
                _health.Increment(HealthName, "files_skipped_old");
                return;
            }
            offset = 0;
            _health.Increment(HealthName, "files_backfilled");
        }
        else
        {
            offset = cursor.Offset;
            if (fi.Length < offset || (cursor.FpLen > 0 && Fingerprint(path, cursor.FpLen).Hash != cursor.FpHash))
            {
                offset = 0; // truncated or replaced (rotation): read again from the start
                _health.Increment(HealthName, "files_reset");
            }
        }
        if (fi.Length == offset) return;

        var state = new FileState(path, sessionId, agentId, isMain);
        long consumed = offset;
        var pendingTail = new MemoryStream();
        AgentEvent? held = null; // the newest event is published last, and waited for, before the checkpoint moves
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[ChunkSize];
            int read;
            while ((read = await fs.ReadAsync(buffer, ct)) > 0)
            {
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    ReadOnlyMemory<byte> line;
                    if (pendingTail.Length > 0)
                    {
                        pendingTail.Write(buffer, start, i - start);
                        line = pendingTail.ToArray();
                        pendingTail.SetLength(0);
                    }
                    else line = buffer.AsMemory(start, i - start);
                    consumed += line.Length + 1;
                    start = i + 1;
                    var events = await HandleLineAsync(line, state, ct);
                    if (events.Count == 0) continue;
                    if (held is not null) events.Insert(0, held);
                    held = events[^1];
                    events.RemoveAt(events.Count - 1);
                    await PublishAsync(events, ct);
                }
                if (start < read) pendingTail.Write(buffer, start, read - start); // partial trailing line: not consumed
            }
        }
        catch (IOException ex)
        {
            _health.Degraded(HealthName, $"read failed for {System.IO.Path.GetFileName(path)}: {ex.Message}");
        }

        if (held is not null)
        {
            Observer?.Invoke(held);
            await _sink.PublishAndWaitAsync(held, ct);
            _health.Increment(HealthName, "events");
        }
        _cursors.Set(key, new Cursor(consumed, 0, "").With(Fingerprint(path, consumed)).ToString());
    }

    private async Task PublishAsync(List<AgentEvent> events, CancellationToken ct)
    {
        foreach (var e in events)
        {
            Observer?.Invoke(e);
            await _sink.PublishAsync(e, ct);
        }
        _health.Increment(HealthName, "events", events.Count);
    }

    private static long LastLineBoundary(string path, long length)
    {
        if (length == 0) return 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var window = (int)Math.Min(length, 1 << 20);
            fs.Seek(length - window, SeekOrigin.Begin);
            var buf = new byte[window];
            fs.ReadExactly(buf);
            var idx = Array.LastIndexOf(buf, (byte)'\n');
            return idx < 0 ? length - window : length - window + idx + 1;
        }
        catch (IOException) { return length; }
    }

    private static (int Len, string Hash) Fingerprint(string path, long upTo)
    {
        var len = (int)Math.Min(upTo, FingerprintBytes);
        if (len <= 0) return (0, "");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[len];
            var n = fs.Read(buf, 0, len);
            return (n, Convert.ToHexStringLower(SHA1.HashData(buf.AsSpan(0, n)))[..16]);
        }
        catch (IOException) { return (0, ""); }
    }

    // ---- record mapping ----------------------------------------------------------------

    private static JsonObject? TryParse(ReadOnlySpan<byte> line)
    {
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static JsonObject? TryParse(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? Ts(JsonObject r) =>
        ClaudeEventMapper.Str(r, "timestamp") is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) ? ts : null;

    private async Task<List<AgentEvent>> HandleLineAsync(ReadOnlyMemory<byte> bytes, FileState f, CancellationToken ct)
    {
        _health.Increment(HealthName, "lines");
        if (bytes.Length == 0 || bytes.Span.Trim((byte)' ').Length == 0) return [];
        var r = TryParse(bytes.Span);
        if (r is null) { _health.Increment(HealthName, "bad_lines"); return []; }
        try { return await MapRecordAsync(r, f, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _health.Increment(HealthName, "map_errors");
            _health.Degraded(HealthName, "mapping error: " + ex.Message);
            return [];
        }
    }

    private async Task<List<AgentEvent>> MapRecordAsync(JsonObject r, FileState f, CancellationToken ct)
    {
        var type = ClaudeEventMapper.Str(r, "type");
        var list = new List<AgentEvent>();
        var cwd = ClaudeEventMapper.Str(r, "cwd");
        if (cwd is not null) f.Cwd = cwd;
        var o = new Origin(EventSources.Transcript, f.SessionId, f.AgentId, Ts(r) ?? _time.GetUtcNow(), f.Cwd);

        if (type is "user" or "assistant" or "ai-title")
        {
            if (f.IsMain) list.AddRange(_mapper.EnsureMain(o, f.Path, ClaudeEventMapper.Str(r, "version")));
            else if (!_ctx.IsKnown(f.AgentId)) list.AddRange(_mapper.Spawned(o, f.AgentId, null, null, null, null, f.Path, parentResolved: false));
        }

        switch (type)
        {
            case "ai-title" when ClaudeEventMapper.Str(r, "aiTitle") is { Length: > 0 } title && f.IsMain && _ctx.Changed(f.AgentId, "title", title):
                list.Add(ClaudeEventMapper.New(o, EventTypes.AgentUpdated, new AgentUpdatedData(Title: title), null));
                break;
            case "user":
                AddContextUpdate(r, o, list);
                await MapUserAsync(r, o, f, list, ct);
                break;
            case "assistant":
                AddContextUpdate(r, o, list);
                await MapAssistantAsync(r, o, list, ct);
                break;
        }
        return list;
    }

    private void AddContextUpdate(JsonObject r, Origin o, List<AgentEvent> list)
    {
        var cwd = ClaudeEventMapper.Str(r, "cwd");
        var branch = ClaudeEventMapper.Str(r, "gitBranch");
        var cwdChanged = _ctx.Changed(o.AgentId, "cwd", cwd);
        var branchChanged = _ctx.Changed(o.AgentId, "branch", branch);
        if (cwdChanged || branchChanged)
            list.Add(ClaudeEventMapper.New(o, EventTypes.AgentUpdated,
                new AgentUpdatedData(Cwd: cwdChanged ? cwd : null, GitBranch: branchChanged ? branch : null), null));
    }

    private async Task MapUserAsync(JsonObject r, Origin o, FileState f, List<AgentEvent> list, CancellationToken ct)
    {
        if (r["message"]?["content"] is not { } content) return;
        var isMeta = ClaudeEventMapper.Bool(r, "isMeta");
        var originKind = ClaudeEventMapper.Str(r["origin"], "kind");

        string? text = null;
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) text = s;
        else if (content is JsonArray blocks)
        {
            var results = blocks.OfType<JsonObject>().Where(b => ClaudeEventMapper.Str(b, "type") == "tool_result").ToList();
            foreach (var tr in results)
            {
                var id = ClaudeEventMapper.Str(tr, "tool_use_id");
                if (id is null) continue;
                var structured = results.Count == 1 ? r["toolUseResult"] : null;
                list.AddRange(await _mapper.ToolEndAsync(o, null, id, null, structured, ContentText(tr["content"]),
                    ClaudeEventMapper.Bool(tr, "is_error"), ct));
            }
            if (results.Count > 0) return;
            var texts = blocks.OfType<JsonObject>().Where(b => ClaudeEventMapper.Str(b, "type") == "text")
                .Select(b => ClaudeEventMapper.Str(b, "text")).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (texts.Count > 0) text = string.Join("\n", texts);
        }
        if (text is null) return;

        if (originKind == "task-notification" || text.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal))
        {
            foreach (Match m in TaskNotification().Matches(text))
            {
                var toolUseId = m.Groups["id"].Value;
                var spawn = _ctx.Spawns.ContainsKey(toolUseId) ||
                            (_ctx.ToolUses.TryGetValue(toolUseId, out var tu) && ClaudeEventMapper.SpawnTools.Contains(tu.Name));
                if (!spawn) continue;
                var status = m.Groups["status"].Value.ToLowerInvariant();
                var (st, reason) = status switch
                {
                    "completed" or "success" or "done" => (AgentStatus.Completed, "Background sub-agent completed"),
                    "killed" or "cancelled" or "canceled" or "stopped" => (AgentStatus.Cancelled, "Background sub-agent was stopped"),
                    _ => (AgentStatus.Failed, "Background sub-agent ended with status " + status),
                };
                _mapper.DeferChildEnd(o, toolUseId, null, st, reason);
            }
            return;
        }
        if (originKind == "peer")
        {
            if (ClaudeEventMapper.Bool(r["origin"], "handback") && ClaudeEventMapper.Str(r["origin"], "from") is { } from)
            {
                var child = ClaudePaths.SubId(f.SessionId, from);
                _mapper.DeferChildEnd(o, null, child, AgentStatus.Completed, "Handed back to parent");
            }
            return;
        }
        if (isMeta) return;
        var prompt = CleanPrompt(text);
        if (prompt is null) return;
        var key = f.IsMain
            ? _ctx.PromptKey(f.SessionId, ClaudeEventMapper.Str(r, "promptId"), prompt, EventSources.Transcript)
            : $"prompt:{f.AgentId}:{ClaudeEventMapper.Str(r, "uuid") ?? ClaudeContext.Hash(prompt)}";
        list.Add(_mapper.Prompt(o, prompt, key));
    }

    private async Task MapAssistantAsync(JsonObject r, Origin o, List<AgentEvent> list, CancellationToken ct)
    {
        if (r["message"] is not JsonObject msg) return;
        var model = ClaudeEventMapper.Str(msg, "model");
        if (model is { Length: > 0 } && !model.StartsWith('<') && _ctx.Changed(o.AgentId, "model", model))
            list.Add(ClaudeEventMapper.New(o, EventTypes.AgentUpdated, new AgentUpdatedData(Model: model), null));
        if (UsageEvent(msg, o) is { } usage) list.Add(usage);
        if (msg["content"] is not JsonArray blocks) return;
        var uuid = ClaudeEventMapper.Str(r, "uuid") ?? Ulid.NewId();
        var apiError = ClaudeEventMapper.Bool(r, "isApiErrorMessage");
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is not JsonObject b) continue;
            switch (ClaudeEventMapper.Str(b, "type"))
            {
                case "text" when !apiError && ClaudeEventMapper.Str(b, "text") is { } t && !string.IsNullOrWhiteSpace(t):
                    list.AddRange(_mapper.Output(o, t.Trim(), $"out:{uuid}:{i}"));
                    break;
                case "tool_use" when ClaudeEventMapper.Str(b, "id") is { } id && ClaudeEventMapper.Str(b, "name") is { } name:
                    list.AddRange(await _mapper.ToolStartAsync(o, name, id, b["input"] as JsonObject ?? new JsonObject(), ct));
                    break;
                // "thinking" (and anything else) is never ingested.
            }
        }
    }

    /// <summary>
    /// Token usage of one model response. Claude Code writes one record per content block with the same
    /// message id; identical repeats share a dedupe key, growing (streamed) values are new facts and the
    /// reducer adds only the increase.
    /// </summary>
    public static AgentEvent? UsageEvent(JsonObject msg, Origin o)
    {
        if (msg["usage"] is not JsonObject u || ClaudeEventMapper.Str(msg, "id") is not { Length: > 0 } id) return null;
        long L(string k) => u[k] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        var d = new UsageData(id, ClaudeEventMapper.Str(msg, "model") is { } m && !m.StartsWith('<') ? m : null,
            L("input_tokens"), L("output_tokens"), L("cache_creation_input_tokens"), L("cache_read_input_tokens"));
        if (d.Input + d.Output + d.CacheWrite + d.CacheRead == 0) return null;
        return ClaudeEventMapper.New(o, EventTypes.AgentUsage, d, $"usage:{id}:{d.Input}:{d.Output}:{d.CacheWrite}:{d.CacheRead}");
    }

    /// <summary>
    /// One-time import of token usage from transcripts that were ingested before usage tracking existed.
    /// Reads only usage (no other events, so nothing is duplicated) for agents already known.
    /// </summary>
    public async Task BackfillUsageAsync(CancellationToken ct)
    {
        const string Marker = "usage-backfill:v1";
        if (_cursors.Get(Marker) is not null || !Directory.Exists(ProjectsDir)) return;
        var published = 0;
        foreach (var dir in SafeEnum(() => Directory.EnumerateDirectories(ProjectsDir)))
        foreach (var main in SafeEnum(() => Directory.EnumerateFiles(dir, "*.jsonl")))
        {
            var sid = Path.GetFileNameWithoutExtension(main);
            var files = new List<(string File, string AgentId)> { (main, ClaudePaths.MainId(sid)) };
            var subDir = Path.Combine(dir, sid, "subagents");
            if (Directory.Exists(subDir))
                files.AddRange(SafeEnum(() => Directory.EnumerateFiles(subDir, "*.jsonl"))
                    .Select(f => (f, ClaudePaths.SubId(sid, Path.GetFileNameWithoutExtension(f)))));
            foreach (var (file, agentId) in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!_ctx.IsKnown(agentId)) continue;
                var batch = new List<AgentEvent>();
                foreach (var line in SafeEnum(() => File.ReadLines(file)))
                {
                    if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
                    JsonObject? r;
                    try { r = JsonNode.Parse(line) as JsonObject; } catch (System.Text.Json.JsonException) { continue; }
                    if (r is null || ClaudeEventMapper.Str(r, "type") != "assistant" || r["message"] is not JsonObject msg) continue;
                    var ts = DateTimeOffset.TryParse(ClaudeEventMapper.Str(r, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : _time.GetUtcNow();
                    if (UsageEvent(msg, new Origin(EventSources.Transcript, sid, agentId, ts)) is { } ev) batch.Add(ev);
                }
                if (batch.Count > 0) { await PublishAsync(batch, ct); published += batch.Count; }
            }
        }
        _cursors.Set(Marker, DateTimeOffset.UtcNow.ToString("O"));
        _health.SetCounter(HealthName, "usage_backfilled", published);
    }

    public static string? ContentText(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join("\n", a.OfType<JsonObject>().Where(b => ClaudeEventMapper.Str(b, "type") == "text")
            .Select(b => ClaudeEventMapper.Str(b, "text"))),
        _ => null,
    };

    /// <summary>User text as typed: slash commands rendered as "/cmd args", local command output and system wrappers dropped.</summary>
    public static string? CleanPrompt(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text.StartsWith("<command-name>", StringComparison.Ordinal) || text.StartsWith("<command-message>", StringComparison.Ordinal))
        {
            var name = CommandTag().Match(text) is { Success: true } n ? n.Groups[1].Value.Trim() : null;
            var args = CommandArgs().Match(text) is { Success: true } a ? a.Groups[1].Value.Trim() : "";
            return name is null ? null : (name + " " + args).Trim();
        }
        if (text.StartsWith("<bash-input>", StringComparison.Ordinal))
            return BashInput().Match(text) is { Success: true } b ? "!" + b.Groups[1].Value.Trim() : null;
        if (text.StartsWith("<local-command-", StringComparison.Ordinal) || text.StartsWith("<bash-std", StringComparison.Ordinal) ||
            text.StartsWith("<system-reminder>", StringComparison.Ordinal) || text.StartsWith("[Request interrupted", StringComparison.Ordinal))
            return null;
        return text;
    }

    [GeneratedRegex(@"<command-name>([^<]*)</command-name>")]
    private static partial Regex CommandTag();
    [GeneratedRegex(@"<command-args>([\s\S]*?)</command-args>")]
    private static partial Regex CommandArgs();
    [GeneratedRegex(@"<bash-input>([\s\S]*?)</bash-input>")]
    private static partial Regex BashInput();
    [GeneratedRegex(@"<tool-use-id>(?<id>[^<]+)</tool-use-id>[\s\S]*?<status>(?<status>[^<]+)</status>")]
    private static partial Regex TaskNotification();

    private static IEnumerable<string> SafeEnum(Func<IEnumerable<string>> f)
    {
        try { return f().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

