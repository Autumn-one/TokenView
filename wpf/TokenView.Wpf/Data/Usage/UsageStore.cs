using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TokenView.Wpf.Data.Usage;

/// <summary>本地用量库：版本化迁移、绝对值快照写入与请求口径预聚合。</summary>
public sealed class UsageStore : IDisposable
{
    private const long HourMs = 3_600_000;

    private const string CacheWriteClients =
        "'claude_code', 'dsh', 'kimi_code', 'mimocode', 'pi_desktop', 'pi_agent', 'copilot', 'mcode', 'command_code', 'cline', 'qoder'";

    private readonly SqliteConnection _connection;

    private UsageStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static UsageStore Open(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        var store = new UsageStore(connection);
        store.Configure();
        Migrations.Apply(connection);
        return store;
    }

    public static UsageStore OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var store = new UsageStore(connection);
        store.Configure();
        Migrations.Apply(connection);
        return store;
    }

    private void Configure()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA cache_size = -8192;
            -- 只映射 32 MB：映射页计入工作集，对百 MB 级别的库完整映射 256 MB 没有收益。
            PRAGMA mmap_size = 33554432;
            -- 单事务重写整个客户端快照会产生较大的 WAL，限制其保留大小。
            PRAGMA journal_size_limit = 67108864;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>同步结束后回收 WAL；被并发读者占用时留给下次同步。</summary>
    public void Checkpoint()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 只读连接仍在使用时 checkpoint 会被拒绝，不影响已提交的数据。
        }
    }

    public int SchemaVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public SourceFileRow? SourceFile(string client, string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, client, path, kind, size_bytes, mtime_ms, content_offset, parser_version,
                   last_scan_ms, unparsed_records
            FROM source_files WHERE client = $client AND path = $path;
            """;
        command.Parameters.AddWithValue("$client", client);
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new SourceFileRow(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.GetInt64(6), reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.GetInt64(9));
    }

    public List<string> SourcePaths(string client)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path FROM source_files WHERE client = $client ORDER BY path;";
        command.Parameters.AddWithValue("$client", client);
        var paths = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    public bool SessionExists(string client, string clientSessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sessions WHERE client = $client AND client_session_id = $session LIMIT 1;";
        command.Parameters.AddWithValue("$client", client);
        command.Parameters.AddWithValue("$session", clientSessionId);
        return command.ExecuteScalar() is not null;
    }

    public ImportSummary ImportSession(SourceFile file, SessionRecord session, IReadOnlyList<MessageRecord> messages)
    {
        if (file.Client != session.Client)
            throw new ArgumentException("source file client does not match session client");

        using var transaction = _connection.BeginTransaction();
        var fileId = UpsertSourceFile(transaction, file);
        var outcome = UpsertSession(transaction, session);
        var summary = new ImportSummary { Session = outcome };
        if (outcome != UpsertOutcome.SkippedStale)
        {
            foreach (var message in messages)
            {
                if (message.ClientSessionId != session.ClientSessionId)
                    throw new ArgumentException("message session does not match session");
                switch (UpsertMessage(transaction, message, fileId))
                {
                    case UpsertOutcome.Inserted: summary.MessagesInserted++; break;
                    case UpsertOutcome.Updated: summary.MessagesUpdated++; break;
                    default: summary.MessagesSkippedStale++; break;
                }
            }
        }
        MarkRequestRollupDirty(transaction);
        transaction.Commit();
        return summary;
    }

    public ImportSummary ReplaceFileImport(SourceFile file, SessionRecord session, IReadOnlyList<MessageRecord> messages)
    {
        if (file.Client != session.Client)
            throw new ArgumentException("source file client does not match session client");

        using var transaction = _connection.BeginTransaction();
        var fileId = UpsertSourceFile(transaction, file);
        var outcome = UpsertSession(transaction, session);
        var pruned = Execute(transaction,
            "DELETE FROM messages WHERE source_file_id = $file", ("$file", fileId));
        var summary = new ImportSummary { Session = outcome, MessagesPruned = pruned };
        foreach (var message in messages)
        {
            if (message.ClientSessionId != session.ClientSessionId)
                throw new ArgumentException("message session does not match session");
            switch (UpsertMessage(transaction, message, fileId))
            {
                case UpsertOutcome.Inserted: summary.MessagesInserted++; break;
                case UpsertOutcome.Updated: summary.MessagesUpdated++; break;
                default: summary.MessagesSkippedStale++; break;
            }
        }
        MarkRequestRollupDirty(transaction);
        transaction.Commit();
        return summary;
    }

    /// <summary>完整快照替换：仅在扫描完整成功后调用。</summary>
    /// <remarks>
    /// 入参是惰性序列：采集器逐会话产出，写入在同一事务内进行。原子性由事务保证，
    /// 不需要把整个客户端快照同时留在内存里；任一会话非法或写入失败即整轮回滚。
    /// </remarks>
    public ImportSummary ReplaceClientSnapshot(string client, IReadOnlyList<SourceFile> files,
        IEnumerable<SessionImport> sessions) => WriteClientSnapshot(client, files, sessions, true);

    /// <summary>只替换成功会话，保留失败源的水位。</summary>
    public ImportSummary MergeClientSnapshot(string client, IReadOnlyList<SourceFile> files,
        IEnumerable<SessionImport> sessions) => WriteClientSnapshot(client, files, sessions, false);

    private ImportSummary WriteClientSnapshot(string client, IReadOnlyList<SourceFile> files,
        IEnumerable<SessionImport> sessions, bool pruneMissing)
    {
        // 采集器可能用 Path.Combine 拼路径（会把分隔符规范成 '\'），
        // 而目录枚举保留根路径原样的分隔符；校验一律用规范化后的完整路径。
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Client != client || !paths.Add(Path.GetFullPath(file.Path)))
                throw new ArgumentException("invalid snapshot source");
        }

        using var transaction = _connection.BeginTransaction();
        var pruned = 0;
        if (pruneMissing)
        {
            pruned = Convert.ToInt32(Scalar(transaction,
                "SELECT COUNT(*) FROM messages WHERE client = $client", ("$client", client)), CultureInfo.InvariantCulture);
            Execute(transaction, "DELETE FROM sessions WHERE client = $client", ("$client", client));
            Execute(transaction, "DELETE FROM source_files WHERE client = $client", ("$client", client));
        }

        var sources = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            sources[Path.GetFullPath(file.Path)] = UpsertSourceFile(transaction, file);
        }

        // 每条消息都要回填 source_file_id；按原始路径缓存，避免逐条规范化字符串。
        var sourceIds = new Dictionary<string, long?>(StringComparer.Ordinal);
        long? SourceId(string path)
        {
            if (sourceIds.TryGetValue(path, out var cached)) return cached;
            var id = sources.TryGetValue(Path.GetFullPath(path), out var found) ? found : (long?)null;
            sourceIds[path] = id;
            return id;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var summary = new ImportSummary { MessagesPruned = pruned };
        foreach (var entry in sessions)
        {
            if (entry.Session.Client != client)
                throw new ArgumentException("snapshot session client mismatch");
            if (!ids.Add(entry.Session.ClientSessionId))
                throw new ArgumentException("duplicate snapshot session");
            if (!paths.Contains(Path.GetFullPath(entry.SourcePath)))
                throw new ArgumentException("snapshot session source is not registered");
            if (entry.MessageSourcePaths.Values.Any(path => !paths.Contains(Path.GetFullPath(path))))
                throw new ArgumentException("snapshot message source is not registered");
            if (entry.Messages.Any(message => message.Client != client ||
                                              message.ClientSessionId != entry.Session.ClientSessionId))
                throw new ArgumentException("snapshot message belongs to another session");

            if (!pruneMissing)
            {
                pruned += Convert.ToInt32(Scalar(transaction,
                    "SELECT COUNT(*) FROM messages WHERE client = $client AND session_id = $session",
                    ("$client", client),
                    ("$session", UsageIds.SessionId(client, entry.Session.ClientSessionId))), CultureInfo.InvariantCulture);
                Execute(transaction,
                    "DELETE FROM sessions WHERE client = $client AND client_session_id = $session",
                    ("$client", client), ("$session", entry.Session.ClientSessionId));
            }

            summary = summary with { Session = UpsertSession(transaction, entry.Session) };
            foreach (var message in entry.Messages)
            {
                var sourcePath = entry.MessageSourcePaths.TryGetValue(message.DedupKey, out var mapped)
                    ? mapped
                    : entry.SourcePath;
                UpsertMessage(transaction, message, SourceId(sourcePath));
                summary.MessagesInserted++;
            }
        }

        // 采集器可能把解析移进会话流（惰性产出），并在产出过程中更新 UnparsedRecords。
        // 因此会话流结束后按最新值再写一次水印，保证 source_files 与上报计数一致；
        // 消息的 source_file_id 仍用首次写入拿到的 id（路径唯一，id 不变）。
        foreach (var file in files)
        {
            UpsertSourceFile(transaction, file);
        }

        summary.MessagesPruned = pruned;
        MarkRequestRollupDirty(transaction);
        transaction.Commit();
        return summary;
    }

    public void ClearStatistics()
    {
        using var transaction = _connection.BeginTransaction();
        Execute(transaction, "DELETE FROM messages; DELETE FROM sessions; DELETE FROM source_files;");
        Execute(transaction, "DELETE FROM request_hourly");
        Execute(transaction, "DELETE FROM request_hourly_sessions");
        Execute(transaction, "UPDATE usage_rollup_state SET request_dirty = 0 WHERE id = 1");
        transaction.Commit();
    }

    public bool RequestRollupIsFresh()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT request_dirty FROM usage_rollup_state WHERE id = 1";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
    }

    public void RebuildRequestRollup()
    {
        using var transaction = _connection.BeginTransaction();
        Execute(transaction, "DELETE FROM request_hourly");
        Execute(transaction, "DELETE FROM request_hourly_sessions");
        using (var insert = Command(transaction, $"""
            -- estimated 请求保留请求数，但 token 列写 0 并计入 requests_without_usage，
            -- 避免任何估算数字进入实测合计。
            INSERT INTO request_hourly (
                 hour_ms, client, model, project_path, source_kind,
                 requests, requests_with_usage, requests_without_usage,
                 measured_requests, derived_requests,
                 input_tokens, output_tokens, reasoning_tokens, cache_read_tokens,
                 cache_write_tokens, total_tokens, cache_write_counted,
                 cache_paired_requests, cache_paired_input, cache_paired_read,
                 input_known, output_known, reasoning_known, cache_read_known, cache_write_known)
             SELECT (m.created_at_ms / {HourMs}) * {HourMs} AS hour_ms,
                    s.client, COALESCE(m.model, s.model), s.project_path, m.source_kind,
                    COUNT(*),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      WHEN m.total_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' OR m.total_tokens IS NULL
                                      THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(m.source_kind = 'measured'), 0),
                    COALESCE(SUM(m.source_kind = 'derived'), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      ELSE m.input_tokens END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      ELSE m.output_tokens END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      ELSE m.reasoning_tokens END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      ELSE m.cache_read_tokens END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      ELSE m.cache_write_tokens END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      ELSE m.total_tokens END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      WHEN s.client IN ({CacheWriteClients})
                                      THEN m.cache_write_tokens ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      WHEN m.input_tokens IS NOT NULL AND m.cache_read_tokens IS NOT NULL
                                      THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      WHEN m.input_tokens IS NOT NULL AND m.cache_read_tokens IS NOT NULL
                                      THEN m.input_tokens END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      WHEN m.input_tokens IS NOT NULL AND m.cache_read_tokens IS NOT NULL
                                      THEN m.cache_read_tokens END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      WHEN m.input_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      WHEN m.output_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      WHEN m.reasoning_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      WHEN m.cache_read_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN m.source_kind = 'estimated' THEN 0
                                      WHEN m.cache_write_tokens IS NOT NULL THEN 1 ELSE 0 END), 0)
             FROM messages m JOIN sessions s ON s.id = m.session_id
             WHERE m.created_at_ms IS NOT NULL
             GROUP BY hour_ms, s.client, COALESCE(m.model, s.model), s.project_path, m.source_kind
            """))
        {
            insert.ExecuteNonQuery();
        }
        using (var insert = Command(transaction, $"""
            INSERT INTO request_hourly_sessions (session_id, hour_ms, client, model, project_path)
             SELECT DISTINCT m.session_id, (m.created_at_ms / {HourMs}) * {HourMs},
                    s.client, COALESCE(m.model, s.model), s.project_path
             FROM messages m JOIN sessions s ON s.id = m.session_id
             WHERE m.created_at_ms IS NOT NULL
            """))
        {
            insert.ExecuteNonQuery();
        }
        Execute(transaction, "UPDATE usage_rollup_state SET request_dirty = 0 WHERE id = 1");
        transaction.Commit();
    }

    private long UpsertSourceFile(SqliteTransaction transaction, SourceFile file)
    {
        if (string.IsNullOrEmpty(file.Path))
            throw new ArgumentException("source file path is empty");
        Execute(transaction, """
            INSERT INTO source_files
                 (client, path, kind, size_bytes, mtime_ms, content_offset, parser_version, last_scan_ms, unparsed_records)
             VALUES ($client, $path, $kind, $size, $mtime, $offset, $version, $scan, $unparsed)
             ON CONFLICT (client, path) DO UPDATE SET
                 kind = excluded.kind,
                 size_bytes = excluded.size_bytes,
                 mtime_ms = excluded.mtime_ms,
                 content_offset = excluded.content_offset,
                 parser_version = excluded.parser_version,
                 last_scan_ms = excluded.last_scan_ms,
                 unparsed_records = excluded.unparsed_records
            """,
            ("$client", file.Client), ("$path", file.Path),
            ("$kind", UsageIds.SourceFileKindValue(file.Kind)),
            ("$size", file.SizeBytes), ("$mtime", file.MtimeMs),
            ("$offset", file.ContentOffset), ("$version", file.ParserVersion),
            ("$scan", file.LastScanMs), ("$unparsed", file.UnparsedRecords));
        return Convert.ToInt64(Scalar(transaction,
            "SELECT id FROM source_files WHERE client = $client AND path = $path",
            ("$client", file.Client), ("$path", file.Path)), CultureInfo.InvariantCulture);
    }

    private UpsertOutcome UpsertSession(SqliteTransaction transaction, SessionRecord session)
    {
        if (string.IsNullOrEmpty(session.ClientSessionId))
            throw new ArgumentException("session id is empty");

        long? existingTotal = null;
        string? existingBasis = null;
        long existingUpdatedAt = long.MinValue;
        using (var command = Command(transaction, """
            SELECT total_tokens, total_basis, updated_at_ms FROM sessions
            WHERE client = $client AND client_session_id = $session LIMIT 1
            """))
        {
            command.Parameters.AddWithValue("$client", session.Client);
            command.Parameters.AddWithValue("$session", session.ClientSessionId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                existingTotal = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                existingBasis = reader.IsDBNull(1) ? null : reader.GetString(1);
                existingUpdatedAt = reader.GetInt64(2);
            }
        }

        var isNew = existingBasis is null && existingTotal is null;
        var now = UsageTime.NowMs();
        var incomingUpdatedAt = session.EndedAtMs ?? now;
        if (!ShouldReplaceSession(existingTotal, existingBasis, existingUpdatedAt,
                session.Usage.TotalTokens, incomingUpdatedAt))
        {
            return UpsertOutcome.SkippedStale;
        }

        var buckets = session.Usage.Buckets;
        Execute(transaction, """
            INSERT INTO sessions
                 (id, client, client_session_id, parent_client_session_id, project_path, model,
                  started_at_ms, ended_at_ms, semantics_id, total_basis, message_coverage, source_kind,
                  input_tokens, output_tokens, reasoning_tokens, cache_read_tokens, cache_write_tokens,
                  total_tokens, client_total_tokens, client_total_mismatch, unknown_relations,
                  cost_source, client_cost_usd, imported_at_ms, updated_at_ms)
             VALUES ($id, $client, $session, $parent, $project, $model, $started, $ended, $semantics,
                     $basis, $coverage, $source_kind, $input, $output, $reasoning, $cache_read,
                     $cache_write, $total, $total, $mismatch, $unknown, $cost_source, $cost,
                     $imported, $updated)
             ON CONFLICT (client, client_session_id) DO UPDATE SET
                 parent_client_session_id = excluded.parent_client_session_id,
                 project_path = excluded.project_path,
                 model = excluded.model,
                 started_at_ms = excluded.started_at_ms,
                 ended_at_ms = excluded.ended_at_ms,
                 semantics_id = excluded.semantics_id,
                 total_basis = excluded.total_basis,
                 message_coverage = excluded.message_coverage,
                 source_kind = excluded.source_kind,
                 input_tokens = excluded.input_tokens,
                 output_tokens = excluded.output_tokens,
                 reasoning_tokens = excluded.reasoning_tokens,
                 cache_read_tokens = excluded.cache_read_tokens,
                 cache_write_tokens = excluded.cache_write_tokens,
                 total_tokens = excluded.total_tokens,
                 client_total_tokens = excluded.client_total_tokens,
                 client_total_mismatch = excluded.client_total_mismatch,
                 unknown_relations = excluded.unknown_relations,
                 cost_source = excluded.cost_source,
                 client_cost_usd = excluded.client_cost_usd,
                 updated_at_ms = excluded.updated_at_ms
            """,
            ("$id", UsageIds.SessionId(session.Client, session.ClientSessionId)),
            ("$client", session.Client), ("$session", session.ClientSessionId),
            ("$parent", session.ParentClientSessionId), ("$project", session.ProjectPath),
            ("$model", session.Model), ("$started", session.StartedAtMs), ("$ended", session.EndedAtMs),
            ("$semantics", session.SemanticsId),
            ("$basis", UsageIds.TotalBasisValue(session.TotalBasis)),
            ("$coverage", UsageIds.CoverageValue(session.Coverage)),
            ("$source_kind", UsageIds.SourceKindValue(session.SourceKind)),
            ("$input", buckets.Input), ("$output", buckets.Output), ("$reasoning", buckets.Reasoning),
            ("$cache_read", buckets.CacheRead), ("$cache_write", buckets.CacheWrite),
            ("$total", session.Usage.TotalTokens),
            ("$mismatch", session.Usage.ClientTotalMismatch ? 1 : 0),
            ("$unknown", UsageIds.EncodeUnknownRelations(session.Usage.UnknownRelations)),
            ("$cost_source", UsageIds.CostSourceValue(session.CostSource)),
            ("$cost", session.ClientCostUsd),
            ("$imported", now), ("$updated", incomingUpdatedAt));

        return isNew || existingBasis is null ? UpsertOutcome.Inserted : UpsertOutcome.Updated;
    }

    private static bool ShouldReplaceSession(long? existingTotal, string? existingBasis,
        long existingUpdatedAt, long? incomingTotal, long incomingUpdatedAt)
    {
        if (existingBasis is null) return true;
        if (incomingTotal is null && existingTotal is not null) return false;
        if (existingBasis == "client_cumulative" &&
            existingTotal is { } existing && incomingTotal is { } incoming && incoming < existing)
        {
            return false;
        }
        return incomingUpdatedAt >= existingUpdatedAt;
    }

    private UpsertOutcome UpsertMessage(SqliteTransaction transaction, MessageRecord message, long? sourceFileId)
    {
        if (string.IsNullOrEmpty(message.DedupKey))
            throw new ArgumentException("message dedup key is empty");

        var sessionKey = UsageIds.SessionId(message.Client, message.ClientSessionId);
        long? existingCreated = null;
        var exists = false;
        using (var command = Command(transaction, """
            SELECT created_at_ms FROM messages
            WHERE client = $client AND session_id = $session AND dedup_key = $key LIMIT 1
            """))
        {
            command.Parameters.AddWithValue("$client", message.Client);
            command.Parameters.AddWithValue("$session", sessionKey);
            command.Parameters.AddWithValue("$key", message.DedupKey);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                exists = true;
                existingCreated = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            }
        }
        if (exists && existingCreated is { } previous && message.CreatedAtMs is { } incoming && incoming < previous)
        {
            return UpsertOutcome.SkippedStale;
        }

        var now = UsageTime.NowMs();
        var buckets = message.Usage.Buckets;
        Execute(transaction, """
            INSERT INTO messages
                 (id, session_id, client, dedup_key, seq, created_at_ms, completed_at_ms, model, agent,
                  semantics_id, source_kind, input_tokens, output_tokens, reasoning_tokens,
                  cache_read_tokens, cache_write_tokens, total_tokens, client_total_tokens,
                  client_total_mismatch, unknown_relations, is_sidechain, source_file_id, source_ref,
                  cost_source, client_cost_usd, imported_at_ms, updated_at_ms)
             VALUES ($id, $session, $client, $key, $seq, $created, $completed, $model, $agent,
                     $semantics, $source_kind, $input, $output, $reasoning, $cache_read,
                     $cache_write, $total, $total, $mismatch, $unknown, $sidechain, $file,
                     $ref, $cost_source, $cost, $imported, $updated)
             ON CONFLICT (client, session_id, dedup_key) DO UPDATE SET
                 seq = excluded.seq,
                 created_at_ms = excluded.created_at_ms,
                 completed_at_ms = excluded.completed_at_ms,
                 model = excluded.model,
                 agent = excluded.agent,
                 semantics_id = excluded.semantics_id,
                 source_kind = excluded.source_kind,
                 input_tokens = excluded.input_tokens,
                 output_tokens = excluded.output_tokens,
                 reasoning_tokens = excluded.reasoning_tokens,
                 cache_read_tokens = excluded.cache_read_tokens,
                 cache_write_tokens = excluded.cache_write_tokens,
                 total_tokens = excluded.total_tokens,
                 client_total_tokens = excluded.client_total_tokens,
                 client_total_mismatch = excluded.client_total_mismatch,
                 unknown_relations = excluded.unknown_relations,
                 is_sidechain = excluded.is_sidechain,
                 source_file_id = excluded.source_file_id,
                 source_ref = excluded.source_ref,
                 cost_source = excluded.cost_source,
                 client_cost_usd = excluded.client_cost_usd,
                 updated_at_ms = excluded.updated_at_ms
            """,
            ("$id", UsageIds.MessageId(message.Client, message.ClientSessionId, message.DedupKey)),
            ("$session", sessionKey), ("$client", message.Client), ("$key", message.DedupKey),
            ("$seq", message.Seq), ("$created", message.CreatedAtMs),
            ("$completed", message.CompletedAtMs), ("$model", message.Model), ("$agent", message.Agent),
            ("$semantics", message.SemanticsId),
            ("$source_kind", UsageIds.SourceKindValue(message.SourceKind)),
            ("$input", buckets.Input), ("$output", buckets.Output), ("$reasoning", buckets.Reasoning),
            ("$cache_read", buckets.CacheRead), ("$cache_write", buckets.CacheWrite),
            ("$total", message.Usage.TotalTokens),
            ("$mismatch", message.Usage.ClientTotalMismatch ? 1 : 0),
            ("$unknown", UsageIds.EncodeUnknownRelations(message.Usage.UnknownRelations)),
            ("$sidechain", message.IsSidechain ? 1 : 0), ("$file", sourceFileId),
            ("$ref", message.SourceRef),
            ("$cost_source", UsageIds.CostSourceValue(message.CostSource)),
            ("$cost", message.ClientCostUsd),
            ("$imported", now), ("$updated", message.CreatedAtMs ?? now));

        return exists ? UpsertOutcome.Updated : UpsertOutcome.Inserted;
    }

    private static void MarkRequestRollupDirty(SqliteTransaction transaction)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE usage_rollup_state SET request_dirty = 1 WHERE id = 1";
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(SqliteTransaction? transaction, string sql)
    {
        var command = transaction?.Connection!.CreateCommand() ?? _connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null) command.Transaction = transaction;
        return command;
    }

    private int Execute(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(transaction, sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command.ExecuteNonQuery();
    }

    private object? Scalar(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(transaction, sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command.ExecuteScalar();
    }

    public void Dispose() => _connection.Dispose();

    private static class Migrations
    {
        private const string InitialSchema = """
            CREATE TABLE source_files (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                client           TEXT    NOT NULL,
                path             TEXT    NOT NULL,
                kind             TEXT    NOT NULL CHECK (kind IN ('jsonl', 'sqlite')),
                size_bytes       INTEGER,
                mtime_ms         INTEGER,
                content_offset   INTEGER NOT NULL DEFAULT 0,
                parser_version   INTEGER NOT NULL,
                last_scan_ms     INTEGER,
                unparsed_records INTEGER NOT NULL DEFAULT 0,
                UNIQUE (client, path)
            );

            CREATE TABLE sessions (
                id                       TEXT PRIMARY KEY,
                client                   TEXT    NOT NULL,
                client_session_id        TEXT    NOT NULL,
                parent_client_session_id TEXT,
                project_path             TEXT,
                model                    TEXT,
                started_at_ms            INTEGER,
                ended_at_ms              INTEGER,
                semantics_id             TEXT    NOT NULL,
                total_basis              TEXT    NOT NULL CHECK (total_basis IN ('client_cumulative', 'client_reported', 'sum_of_messages')),
                message_coverage         TEXT    NOT NULL CHECK (message_coverage IN ('complete', 'partial', 'none')),
                source_kind              TEXT    NOT NULL CHECK (source_kind IN ('measured', 'derived', 'estimated')),
                input_tokens             INTEGER,
                output_tokens            INTEGER,
                reasoning_tokens         INTEGER,
                cache_read_tokens        INTEGER,
                cache_write_tokens       INTEGER,
                total_tokens             INTEGER,
                client_total_tokens      INTEGER,
                client_total_mismatch    INTEGER NOT NULL DEFAULT 0 CHECK (client_total_mismatch IN (0, 1)),
                unknown_relations        TEXT,
                cost_source              TEXT    NOT NULL CHECK (cost_source IN ('client_reported', 'none')),
                client_cost_usd          REAL,
                imported_at_ms           INTEGER NOT NULL,
                updated_at_ms            INTEGER NOT NULL,
                UNIQUE (client, client_session_id)
            );

            CREATE INDEX idx_sessions_client_started ON sessions (client, started_at_ms);
            CREATE INDEX idx_sessions_project_started ON sessions (project_path, started_at_ms);

            CREATE TABLE messages (
                id                    TEXT PRIMARY KEY,
                session_id            TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
                client                TEXT    NOT NULL,
                dedup_key             TEXT    NOT NULL,
                seq                   INTEGER,
                created_at_ms         INTEGER,
                completed_at_ms       INTEGER,
                model                 TEXT,
                agent                 TEXT,
                semantics_id          TEXT    NOT NULL,
                source_kind           TEXT    NOT NULL CHECK (source_kind IN ('measured', 'derived', 'estimated')),
                input_tokens          INTEGER,
                output_tokens         INTEGER,
                reasoning_tokens      INTEGER,
                cache_read_tokens     INTEGER,
                cache_write_tokens    INTEGER,
                total_tokens          INTEGER,
                client_total_tokens   INTEGER,
                client_total_mismatch INTEGER NOT NULL DEFAULT 0 CHECK (client_total_mismatch IN (0, 1)),
                unknown_relations     TEXT,
                is_sidechain          INTEGER NOT NULL DEFAULT 0 CHECK (is_sidechain IN (0, 1)),
                source_file_id        INTEGER REFERENCES source_files (id) ON DELETE SET NULL,
                source_ref            TEXT,
                cost_source           TEXT    NOT NULL CHECK (cost_source IN ('client_reported', 'none')),
                client_cost_usd       REAL,
                imported_at_ms        INTEGER NOT NULL,
                updated_at_ms         INTEGER NOT NULL,
                UNIQUE (client, session_id, dedup_key)
            );

            CREATE INDEX idx_messages_session_created ON messages (session_id, created_at_ms);
            CREATE INDEX idx_messages_client_created ON messages (client, created_at_ms);
            CREATE INDEX idx_messages_model ON messages (model);
            """;

        private const string RollupSchema = """
            CREATE TABLE request_hourly (
                hour_ms              INTEGER NOT NULL,
                client               TEXT    NOT NULL,
                model                TEXT,
                project_path         TEXT,
                source_kind          TEXT    NOT NULL,
                requests             INTEGER NOT NULL,
                requests_with_usage  INTEGER NOT NULL,
                requests_without_usage INTEGER NOT NULL,
                measured_requests    INTEGER NOT NULL,
                derived_requests     INTEGER NOT NULL,
                input_tokens         INTEGER NOT NULL,
                output_tokens        INTEGER NOT NULL,
                reasoning_tokens     INTEGER NOT NULL,
                cache_read_tokens    INTEGER NOT NULL,
                cache_write_tokens   INTEGER NOT NULL,
                total_tokens         INTEGER NOT NULL,
                cache_write_counted  INTEGER NOT NULL,
                cache_paired_requests INTEGER NOT NULL,
                cache_paired_input   INTEGER NOT NULL,
                cache_paired_read    INTEGER NOT NULL,
                input_known          INTEGER NOT NULL,
                output_known        INTEGER NOT NULL,
                reasoning_known      INTEGER NOT NULL,
                cache_read_known     INTEGER NOT NULL,
                cache_write_known    INTEGER NOT NULL
            );

            CREATE INDEX idx_request_hourly_time ON request_hourly (hour_ms);

            CREATE TABLE request_hourly_sessions (
                session_id   TEXT    NOT NULL,
                hour_ms      INTEGER NOT NULL,
                client       TEXT    NOT NULL,
                model        TEXT,
                project_path TEXT
            );

            CREATE INDEX idx_request_hourly_sessions_time ON request_hourly_sessions (hour_ms);

            CREATE TABLE usage_rollup_state (
                id            INTEGER PRIMARY KEY CHECK (id = 1),
                request_dirty INTEGER NOT NULL DEFAULT 1
            );

            INSERT INTO usage_rollup_state (id, request_dirty) VALUES (1, 1);
            """;

        public static void Apply(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS schema_migrations (
                        version       INTEGER PRIMARY KEY,
                        name          TEXT    NOT NULL,
                        applied_at_ms INTEGER NOT NULL
                    );
                    """;
                command.ExecuteNonQuery();
            }
            var current = CurrentVersion(connection);
            foreach (var (version, name, sql) in new (int Version, string Name, string Sql)[]
                     {
                         (1, "initial_usage_schema", InitialSchema),
                         (2, "request_rollup", RollupSchema),
                         (3, "refresh_request_rollup_once", "UPDATE usage_rollup_state SET request_dirty = 1 WHERE id = 1;"),
                     })
            {
                if (version <= current) continue;
                using var transaction = connection.BeginTransaction();
                using (var migration = connection.CreateCommand())
                {
                    migration.Transaction = transaction;
                    migration.CommandText = sql;
                    migration.ExecuteNonQuery();
                }
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO schema_migrations (version, name, applied_at_ms) VALUES ($version, $name, $at)";
                insert.Parameters.AddWithValue("$version", version);
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$at", UsageTime.NowMs());
                insert.ExecuteNonQuery();
                transaction.Commit();
            }
            using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA user_version = {CurrentVersion(connection)};";
            pragma.ExecuteNonQuery();
        }

        private static int CurrentVersion(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }
}
