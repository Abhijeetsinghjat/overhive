using System.Text.Json.Nodes;
using Amc.Core.Events;
using Amc.Core.State;
using Microsoft.Data.Sqlite;

namespace Amc.Daemon.Storage;

public sealed record EventQuery(
    long? After = null, long? Before = null, int Limit = 200,
    string? AgentId = null, bool IncludeDescendants = false, IReadOnlyCollection<string>? AgentIds = null,
    string? RepositoryId = null, string? WorktreeId = null, string? FilePath = null,
    IReadOnlyCollection<string>? Types = null, string? TypePrefix = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? Text = null,
    bool Descending = false);

/// <summary>
/// Append-only SQLite event log (WAL). Single writer (the ingest pipeline); readers open
/// their own connections. Dedupe is enforced by a UNIQUE index on dedupe_key.
/// </summary>
public sealed class EventStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly SqliteConnection _writer;

    public EventStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Cache = SqliteCacheMode.Private, Pooling = true }.ToString();
        _writer = Open();
        Migrate();
        if (!File.Exists(dbPath)) return;
        if (OperatingSystem.IsWindows()) Amc.Core.Security.OwnerOnly.Restrict(dbPath);
        else File.SetUnixFileMode(dbPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        // journal_size_limit: after a checkpoint resets the WAL, truncate it to 8 MB instead of keeping its high-water mark
        // (a large transaction can grow it well past the 1000-page autocheckpoint, which stays at the SQLite default).
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; " +
                "PRAGMA journal_size_limit=8388608;");
        return c;
    }

    private void Migrate()
    {
        // One-time switch to incremental auto-vacuum so space freed by retention is returned to disk.
        // Requires a full VACUUM once (runs before anything else uses the database).
        if (Scalar<long>(_writer, "PRAGMA auto_vacuum") != 2)
        {
            Exec(_writer, "PRAGMA auto_vacuum=INCREMENTAL");
            Exec(_writer, "VACUUM");
        }
        Exec(_writer, """
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY, -- no AUTOINCREMENT: deduped inserts must not burn sequence numbers
              id TEXT NOT NULL,
              ts TEXT NOT NULL,
              ts_ms INTEGER NOT NULL,
              type TEXT NOT NULL,
              source TEXT NOT NULL,
              evidence TEXT NOT NULL,
              agent_id TEXT, session_id TEXT, repository_id TEXT, worktree_id TEXT, file_path TEXT,
              dedupe_key TEXT,
              data TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_events_dedupe ON events(dedupe_key) WHERE dedupe_key IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_events_agent ON events(agent_id, seq);
            CREATE INDEX IF NOT EXISTS ix_events_worktree ON events(worktree_id, seq);
            CREATE INDEX IF NOT EXISTS ix_events_file ON events(file_path, seq);
            CREATE INDEX IF NOT EXISTS ix_events_type ON events(type, seq);
            CREATE INDEX IF NOT EXISTS ix_events_ts ON events(ts_ms);
            CREATE INDEX IF NOT EXISTS ix_events_session ON events(session_id, seq);
            -- Legacy plain-JSON snapshots (read once after an upgrade, emptied by the first compressed snapshot).
            CREATE TABLE IF NOT EXISTS snapshots(seq INTEGER PRIMARY KEY, created_at TEXT NOT NULL, state TEXT NOT NULL);
            -- Compressed snapshots (SnapshotCodec). A separate table, so an older daemon never reads this format.
            CREATE TABLE IF NOT EXISTS state_snapshots(seq INTEGER PRIMARY KEY, created_at TEXT NOT NULL, state BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS cursors(key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
            """);
        Exec(_writer, $"INSERT OR IGNORE INTO meta(key,value) VALUES('schema_version','{SchemaVersion}')");
    }

    /// <summary>Appends events in one transaction. Returns only the events actually stored (duplicates are skipped), with Seq set.</summary>
    public List<AgentEvent> Append(IReadOnlyList<AgentEvent> events)
    {
        var stored = new List<AgentEvent>(events.Count);
        using var tx = _writer.BeginTransaction();
        using var cmd = _writer.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO events(id,ts,ts_ms,type,source,evidence,agent_id,session_id,repository_id,worktree_id,file_path,dedupe_key,data)
            VALUES($id,$ts,$ts_ms,$type,$source,$evidence,$agent,$session,$repo,$wt,$file,$dedupe,$data)
            ON CONFLICT DO NOTHING
            RETURNING seq;
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[] { "$id", "$ts", "$ts_ms", "$type", "$source", "$evidence", "$agent", "$session", "$repo", "$wt", "$file", "$dedupe", "$data" })
            p[name] = cmd.Parameters.Add(name, SqliteType.Text);
        foreach (var e in events)
        {
            p["$id"].Value = e.Id;
            p["$ts"].Value = e.Ts.ToString("O");
            p["$ts_ms"].Value = e.Ts.ToUnixTimeMilliseconds();
            p["$type"].Value = e.Type;
            p["$source"].Value = e.Source;
            p["$evidence"].Value = e.Evidence.ToString().ToLowerInvariant();
            p["$agent"].Value = (object?)e.AgentId ?? DBNull.Value;
            p["$session"].Value = (object?)e.SessionId ?? DBNull.Value;
            p["$repo"].Value = (object?)e.RepositoryId ?? DBNull.Value;
            p["$wt"].Value = (object?)e.WorktreeId ?? DBNull.Value;
            p["$file"].Value = (object?)e.FilePath ?? DBNull.Value;
            p["$dedupe"].Value = (object?)e.DedupeKey ?? DBNull.Value;
            p["$data"].Value = e.Data.ToJsonString(Json.Options);
            var seq = cmd.ExecuteScalar();
            if (seq is long s) stored.Add(e with { Seq = s });
        }
        tx.Commit();
        Interlocked.Add(ref _appendedSinceCount, stored.Count);
        return stored;
    }

    public long LatestSeq()
    {
        using var c = Open();
        return Scalar<long?>(c, "SELECT MAX(seq) FROM events") ?? 0;
    }

    public long Count()
    {
        using var c = Open();
        return Scalar<long>(c, "SELECT COUNT(*) FROM events");
    }

    private static readonly TimeSpan CountRefresh = TimeSpan.FromMinutes(10);
    private readonly Lock _countLock = new();
    private long _countBase = -1, _countAtTicks, _appendedSinceCount;

    /// <summary>
    /// Event count for /api/health, which is polled: an exact COUNT(*) (a full index scan) at most every 10 minutes,
    /// plus the events appended since. Deletions by retention show up at the next refresh.
    /// </summary>
    public long CountApprox()
    {
        lock (_countLock)
        {
            if (_countBase < 0 || Environment.TickCount64 - _countAtTicks > (long)CountRefresh.TotalMilliseconds)
            {
                Interlocked.Exchange(ref _appendedSinceCount, 0);
                _countBase = Count();
                _countAtTicks = Environment.TickCount64;
            }
            return _countBase + Interlocked.Read(ref _appendedSinceCount);
        }
    }

    public List<AgentEvent> Query(EventQuery q)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        void Add(string clause, string name, object value) { where.Add(clause); cmd.Parameters.AddWithValue(name, value); }
        if (q.After is { } a) Add("seq > $after", "$after", a);
        if (q.Before is { } b) Add("seq < $before", "$before", b);
        var agents = new List<string>();
        if (q.AgentId is not null) agents.Add(q.AgentId);
        if (q.AgentIds is not null) agents.AddRange(q.AgentIds);
        if (agents.Count > 0)
        {
            var names = agents.Select((_, i) => "$ag" + i).ToList();
            where.Add($"agent_id IN ({string.Join(',', names)})");
            for (var i = 0; i < agents.Count; i++) cmd.Parameters.AddWithValue(names[i], agents[i]);
        }
        if (q.RepositoryId is not null) Add("repository_id = $repo", "$repo", q.RepositoryId);
        if (q.WorktreeId is not null) Add("worktree_id = $wt", "$wt", q.WorktreeId);
        if (q.FilePath is not null) Add("file_path = $file", "$file", q.FilePath);
        if (q.Types is { Count: > 0 })
        {
            var names = q.Types.Select((_, i) => "$t" + i).ToList();
            where.Add($"type IN ({string.Join(',', names)})");
            var i = 0;
            foreach (var t in q.Types) cmd.Parameters.AddWithValue(names[i++], t);
        }
        if (q.TypePrefix is not null) Add("type LIKE $tp", "$tp", q.TypePrefix.Replace("%", "") + "%");
        if (q.From is { } f) Add("ts_ms >= $from", "$from", f.ToUnixTimeMilliseconds());
        if (q.To is { } t2) Add("ts_ms <= $to", "$to", t2.ToUnixTimeMilliseconds());
        if (!string.IsNullOrWhiteSpace(q.Text)) Add("(data LIKE $text OR file_path LIKE $text)", "$text", "%" + q.Text.Replace("%", "") + "%");
        cmd.CommandText = "SELECT seq,id,ts,type,source,evidence,agent_id,session_id,repository_id,worktree_id,file_path,dedupe_key,data FROM events"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + (q.Descending ? " ORDER BY seq DESC" : " ORDER BY seq ASC")
            + " LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(q.Limit, 1, 5000));
        var list = new List<AgentEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>Streams all events after <paramref name="afterSeq"/> in order (used for replay on startup).</summary>
    public IEnumerable<AgentEvent> ReadAll(long afterSeq)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq,id,ts,type,source,evidence,agent_id,session_id,repository_id,worktree_id,file_path,dedupe_key,data FROM events WHERE seq > $after ORDER BY seq";
        cmd.Parameters.AddWithValue("$after", afterSeq);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return Read(r);
    }

    private static AgentEvent Read(SqliteDataReader r) => new()
    {
        Seq = r.GetInt64(0),
        Id = r.GetString(1),
        Ts = DateTimeOffset.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
        Type = r.GetString(3),
        Source = r.GetString(4),
        Evidence = Enum.Parse<Evidence>(r.GetString(5), ignoreCase: true),
        AgentId = r.IsDBNull(6) ? null : r.GetString(6),
        SessionId = r.IsDBNull(7) ? null : r.GetString(7),
        RepositoryId = r.IsDBNull(8) ? null : r.GetString(8),
        WorktreeId = r.IsDBNull(9) ? null : r.GetString(9),
        FilePath = r.IsDBNull(10) ? null : r.GetString(10),
        DedupeKey = r.IsDBNull(11) ? null : r.GetString(11),
        Data = JsonNode.Parse(r.GetString(12)) as JsonObject ?? new JsonObject(),
    };

    // ---- snapshots ---------------------------------------------------------------

    /// <summary>Snapshots kept: the newest, plus one to fall back on if the newest cannot be decoded.</summary>
    public const int SnapshotsKept = 2;

    public void SaveSnapshot(WorldState state)
    {
        state.ReducerVersion = Reducer.Version;
        using var encoded = SnapshotCodec.Encode(state);
        SaveSnapshot(state.Seq, encoded);
    }

    /// <summary>
    /// Stores an encoded snapshot (<see cref="SnapshotCodec"/>, encoded by the caller under the state read lock).
    /// The bytes are streamed into a zeroblob through <see cref="SqliteBlob"/>, so no contiguous copy is made.
    /// </summary>
    public void SaveSnapshot(long seq, ChunkedBuffer encoded)
    {
        using var c = Open(); // own connection: callable from the pipeline and from the retention service
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO state_snapshots(seq,created_at,state) VALUES($seq,$at,zeroblob($len))";
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$len", encoded.Length);
            cmd.ExecuteNonQuery();
        }
        using (var blob = new SqliteBlob(c, "state_snapshots", "state", seq))
            encoded.WriteTo(blob);
        using (var prune = c.CreateCommand())
        {
            prune.Transaction = tx;
            // Older snapshots, and the legacy JSON ones: an older daemon then finds none and replays the log.
            prune.CommandText = $"""
                DELETE FROM state_snapshots WHERE seq NOT IN (SELECT seq FROM state_snapshots ORDER BY seq DESC LIMIT {SnapshotsKept});
                DELETE FROM snapshots;
                """;
            prune.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// The newest snapshot that decodes, or null (replay from scratch). Falls back to the plain-JSON snapshots that
    /// earlier versions wrote, until the first compressed snapshot replaces them.
    /// </summary>
    public WorldState? LoadLatestSnapshot()
    {
        using var c = Open();
        var seqs = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT seq FROM state_snapshots ORDER BY seq DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read()) seqs.Add(r.GetInt64(0));
        }
        foreach (var seq in seqs)
        {
            try
            {
                using var blob = new SqliteBlob(c, "state_snapshots", "state", seq, readOnly: true);
                using var buffered = new BufferedStream(blob, ChunkedBuffer.ChunkSize);
                if (SnapshotCodec.Decode(buffered) is { } state) return state;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or EndOfStreamException) { }
        }
        try
        {
            var json = Scalar<string?>(c, "SELECT state FROM snapshots ORDER BY seq DESC LIMIT 1");
            return json is null ? null : Json.Deserialize<WorldState>(json);
        }
        catch (System.Text.Json.JsonException) { return null; } // incompatible snapshot: replay from scratch
    }

    /// <summary>(count, total bytes) of the stored snapshots, both formats; for diagnostics and tests.</summary>
    public (long Count, long Bytes) SnapshotStats()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM state_snapshots) + (SELECT COUNT(*) FROM snapshots),
                   COALESCE((SELECT SUM(length(state)) FROM state_snapshots), 0) + COALESCE((SELECT SUM(length(state)) FROM snapshots), 0)
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    // ---- cursors (collector checkpoints, e.g. transcript byte offsets) -----------

    public string? GetCursor(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM cursors WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public Dictionary<string, string> GetCursors(string prefix)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key,value FROM cursors WHERE key LIKE $p";
        cmd.Parameters.AddWithValue("$p", prefix.Replace("%", "") + "%");
        var d = new Dictionary<string, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public void SetCursor(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO cursors(key,value,updated_at) VALUES($k,$v,$at) ON CONFLICT(key) DO UPDATE SET value=$v, updated_at=$at";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public bool Ping()
    {
        try { using var c = Open(); return Scalar<long>(c, "SELECT 1") == 1; }
        catch (SqliteException) { return false; }
    }

    // ---- retention -------------------------------------------------------------------

    /// <summary>"Latest value" types: only the newest event per key matters for state; older ones are superseded.</summary>
    public static readonly IReadOnlyDictionary<string, string> CompactedTypes = new Dictionary<string, string>
    {
        [EventTypes.GitFileStatus] = "worktree_id, file_path",
        [EventTypes.WorktreeChanged] = "worktree_id",
    };

    /// <summary>High-volume detail types removed after the detail retention window.</summary>
    public static readonly IReadOnlyList<string> DetailTypes =
    [
        EventTypes.ProcessStarted, EventTypes.ProcessExited, EventTypes.ToolStarted, EventTypes.ToolCompleted, EventTypes.ToolFailed,
        EventTypes.CommandStarted, EventTypes.CommandCompleted, EventTypes.CommandFailed, EventTypes.FileRead, EventTypes.AgentOutput,
        EventTypes.AgentNotification, EventTypes.TestStarted, EventTypes.TestPassed, EventTypes.TestFailed,
    ];

    public sealed record PruneResult(long Compacted, long DetailDeleted, long HistoryDeleted, long FreedPages, TimeSpan Took);

    /// <summary>
    /// Deletes in small batches on its own connection, so the pipeline's writer is never blocked for long.
    /// The caller must have saved a snapshot first: normal restarts restore from it and never need pruned events.
    /// </summary>
    public PruneResult Prune(DateTimeOffset now, long maxSeq, int detailDays, int historyDays, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var detailCut = now.AddDays(-detailDays).ToUnixTimeMilliseconds();
        var historyCut = now.AddDays(-historyDays).ToUnixTimeMilliseconds();
        using var c = Open();
        long compacted = 0, detail = 0, history = 0;

        foreach (var (type, key) in CompactedTypes)
            compacted += DeleteBatched(c, $"""
                SELECT seq FROM events WHERE type = $type AND ts_ms < $cut AND seq <= $max
                  AND seq NOT IN (SELECT MAX(seq) FROM events WHERE type = $type GROUP BY {key})
                LIMIT 5000
                """, [("$type", type), ("$cut", detailCut), ("$max", maxSeq)], ct);

        var detailIn = string.Join(',', DetailTypes.Select((_, i) => "$d" + i));
        detail += DeleteBatched(c, $"SELECT seq FROM events WHERE type IN ({detailIn}) AND ts_ms < $cut AND seq <= $max LIMIT 5000",
            [.. DetailTypes.Select((t, i) => ("$d" + i, (object)t)), ("$cut", detailCut), ("$max", maxSeq)], ct);

        var keepIn = string.Join(',', CompactedTypes.Keys.Select((_, i) => "$k" + i));
        history += DeleteBatched(c, $"SELECT seq FROM events WHERE ts_ms < $cut AND seq <= $max AND type NOT IN ({keepIn}) LIMIT 5000",
            [.. CompactedTypes.Keys.Select((t, i) => ("$k" + i, (object)t)), ("$cut", historyCut), ("$max", maxSeq)], ct);

        var freeBefore = Scalar<long>(c, "PRAGMA freelist_count");
        Exec(c, "PRAGMA incremental_vacuum");
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)");
        return new PruneResult(compacted, detail, history, freeBefore, sw.Elapsed);
    }

    private static long DeleteBatched(SqliteConnection c, string selectSeqs, (string Name, object Value)[] ps, CancellationToken ct)
    {
        long total = 0;
        while (!ct.IsCancellationRequested)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM events WHERE seq IN ({selectSeqs})";
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            var n2 = cmd.ExecuteNonQuery();
            tx.Commit();
            total += n2;
            if (n2 < 5000) break;
            Thread.Sleep(5); // let the pipeline's writer in between batches
        }
        return total;
    }

    public long FileBytes(string dbPath) =>
        new[] { dbPath, dbPath + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? default! : (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public void Dispose() => _writer.Dispose();
}
