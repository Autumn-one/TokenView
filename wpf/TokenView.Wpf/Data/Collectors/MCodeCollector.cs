using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>MCode 本地 SQLite 请求账本采集器；只读取统计所需的元数据列。</summary>
public sealed class MCodeCollector : IClientCollector
{
    private const int ParserVersion = 1;
    private const string Client = "mcode";
    private const string ReasoningRelation = "mcode.reasoning_relation_unverified";

    private static readonly string[] SessionWhitelist =
    [
        "session_id",
        "parent_session_id",
        "workspace_dir",
        "created_at_ms",
        "updated_at_ms",
    ];

    private static readonly string[] UsageWhitelist =
    [
        "id",
        "session_id",
        "agent_name",
        "model",
        "ts",
        "input_tokens",
        "output_tokens",
        "reasoning_tokens",
        "cache_read_tokens",
        "cache_write_tokens",
    ];

    private readonly string? _database;

    public MCodeCollector() : this(DefaultDatabase())
    {
    }

    public MCodeCollector(string? database)
    {
        _database = database;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _database is null ? [] : [_database];

    /// <summary>先取 MINIMAX_DATA_DIR，再取 MAVIS_DATA_DIR，最后回退到 ~/.minimax。</summary>
    public static string? DefaultDatabase()
    {
        var root = TrimmedEnvironment("MINIMAX_DATA_DIR")
                   ?? TrimmedEnvironment("MAVIS_DATA_DIR");
        if (root is null)
        {
            var home = CollectorSupport.HomeDir();
            if (home is null) return null;
            root = Path.Combine(home, ".minimax");
        }
        return Path.Combine(root, "v2", "sqlite", "runtime-state.sqlite");
    }

    private static string? TrimmedEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        var database = _database;
        if (database is null)
        {
            report.RootMissing = true;
            return report;
        }

        Snapshot snapshot;
        SqliteSignature observed;
        try
        {
            if (!Exists(database))
            {
                report.RootMissing = true;
                return report;
            }
            observed = SqliteSnapshot.Signature(database);
            using var connection = SqliteSnapshot.Open(database);
            snapshot = Read(connection.Connection, database);
            if (SqliteSnapshot.Signature(database) != observed)
            {
                throw SyncException.ChangedDuringScan();
            }
        }
        catch (SyncException error) when (error.Retryable)
        {
            throw;
        }
        catch (SyncException)
        {
            report.FilesSeen = 1;
            report.FilesFailed = 1;
            return report;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      InvalidDataException or SqliteException or
                                      InvalidOperationException or ArgumentException or
                                      FormatException or OverflowException)
        {
            report.FilesSeen = 1;
            report.FilesFailed = 1;
            return report;
        }

        var source = CollectorSupport.NewSourceFile(Client, database, SourceFileKind.Sqlite,
            observed.Main.Size, observed.Main.MtimeMs, 0, ParserVersion, snapshot.Unparsed);
        ImportSummary imported;
        try
        {
            imported = store.ReplaceClientSnapshot(Client, [source], snapshot.Sessions);
        }
        catch (Exception error) when (error is SqliteException or ArgumentException)
        {
            throw SyncException.Store(error);
        }

        report.FilesSeen = 1;
        report.FilesParsed = 1;
        report.SessionsImported = snapshot.Sessions.Count;
        report.UnparsedLines = snapshot.Unparsed;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static Snapshot Read(SqliteConnection connection, string path)
    {
        var sessionColumns = Columns(connection, "local_runtime_sessions");
        if (!sessionColumns.Contains("session_id"))
        {
            throw new InvalidDataException("local_runtime_sessions.session_id is missing");
        }

        var sessions = new SortedDictionary<string, SessionMeta>(StringComparer.Ordinal);
        var unparsed = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT {Project(sessionColumns, SessionWhitelist)} FROM local_runtime_sessions ORDER BY session_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!TryRequiredString(reader, 0, out var id) ||
                    !TryString(reader, 1, out var parent) ||
                    !TryString(reader, 2, out var project) ||
                    !TryTimestamp(reader, 3, out var startMs) ||
                    !TryTimestamp(reader, 4, out var endMs))
                {
                    unparsed++;
                    continue;
                }
                if (id.Length == 0)
                {
                    unparsed++;
                    continue;
                }
                if (sessions.ContainsKey(id))
                {
                    throw new InvalidDataException("duplicate local_runtime_sessions.session_id");
                }
                sessions.Add(id, new SessionMeta(id, parent, project, startMs, endMs));
            }
        }

        var hasUsage = HasUsage(connection);
        var details = new Dictionary<string, List<(MessageRecord Message, RawTokenCounts Raw)>>(
            StringComparer.Ordinal);
        var partial = new HashSet<string>(StringComparer.Ordinal);
        if (hasUsage)
        {
            var usageColumns = Columns(connection, "local_runtime_token_usage");
            foreach (var required in new[] { "id", "session_id" })
            {
                if (!usageColumns.Contains(required))
                {
                    throw new InvalidDataException($"local_runtime_token_usage.{required} is missing");
                }
            }

            var ids = new HashSet<long>();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {Project(usageColumns, UsageWhitelist)} FROM local_runtime_token_usage ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // 无可用会话引用时中止，避免用未归属的用量覆盖既有完整快照。
                if (!TryRequiredString(reader, 1, out var sessionId))
                {
                    throw new InvalidDataException("invalid local_runtime_token_usage.session_id");
                }
                if (!TryInt64(reader, 0, out var id) ||
                    !TryString(reader, 2, out var agent) ||
                    !TryString(reader, 3, out var model) ||
                    !TryTimestamp(reader, 4, out var createdMs) ||
                    !TryCount(reader, 5, out var input) ||
                    !TryCount(reader, 6, out var output) ||
                    !TryCount(reader, 7, out var reasoning) ||
                    !TryCount(reader, 8, out var cacheRead) ||
                    !TryCount(reader, 9, out var cacheWrite))
                {
                    unparsed++;
                    partial.Add(sessionId);
                    continue;
                }

                var raw = new RawTokenCounts(input, output, cacheRead, cacheWrite, reasoning);
                if (id is not { } usageId || usageId < 0 ||
                    !CollectorSupport.ValidCounts(raw) || !sessions.ContainsKey(sessionId))
                {
                    unparsed++;
                    partial.Add(sessionId);
                    continue;
                }
                if (!ids.Add(usageId))
                {
                    throw new InvalidDataException("duplicate local_runtime_token_usage.id");
                }

                var key = usageId.ToString(CultureInfo.InvariantCulture);
                var meta = new RequestMeta(
                    $"usage:{key}",
                    model is not null && model.Trim().Length > 0 ? model : null,
                    agent,
                    createdMs,
                    null,
                    false,
                    $"local_runtime_token_usage.id={key}");
                var message = Request(sessionId, meta, raw);
                if (!details.TryGetValue(sessionId, out var entries))
                {
                    entries = [];
                    details.Add(sessionId, entries);
                }
                entries.Add((message, raw));
            }
        }

        var imports = new List<SessionImport>(sessions.Count);
        foreach (var (id, meta) in sessions)
        {
            var messages = new List<MessageRecord>();
            var counts = new List<RawTokenCounts>();
            if (details.TryGetValue(id, out var entries))
            {
                foreach (var (message, raw) in entries)
                {
                    messages.Add(message);
                    counts.Add(raw);
                }
            }
            var session = Session(meta, messages, counts, !hasUsage || partial.Contains(id));
            imports.Add(SessionImport.Create(session, messages, path));
        }
        return new Snapshot(imports, unparsed);
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static string Project(HashSet<string> columns, string[] whitelist) =>
        string.Join(",", whitelist.Select(column => columns.Contains(column) ? column : $"NULL AS {column}"));

    private static bool HasUsage(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='local_runtime_token_usage'";
        return command.ExecuteScalar() is not null;
    }

    private static bool TryRequiredString(SqliteDataReader reader, int index, out string value)
    {
        if (reader.GetValue(index) is string text)
        {
            value = text;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryString(SqliteDataReader reader, int index, out string? value)
    {
        switch (reader.GetValue(index))
        {
            case DBNull:
                value = null;
                return true;
            case string text:
                value = text;
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static bool TryInt64(SqliteDataReader reader, int index, out long? value)
    {
        switch (reader.GetValue(index))
        {
            case DBNull:
                value = null;
                return true;
            case long number:
                value = number;
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static bool TryTimestamp(SqliteDataReader reader, int index, out long? value)
    {
        if (!TryInt64(reader, index, out value)) return false;
        if (value is < 0) return false;
        return true;
    }

    private static bool TryCount(SqliteDataReader reader, int index, out long? value)
    {
        if (!TryInt64(reader, index, out value)) return false;
        if (value is < 0) return false;
        return true;
    }

    private static MessageRecord Request(string sessionId, RequestMeta meta, RawTokenCounts raw)
    {
        var message = RequestBuilder.Request(Client, sessionId, meta, raw);
        var usage = message.Usage;
        var unknown = usage.UnknownRelations.ToList();
        // 已安装客户端的正样本 reasoning 均为 0；非零或缺失时无法确认 output 分桶。
        if (raw.Reasoning != 0L)
        {
            usage = usage with
            {
                Buckets = usage.Buckets with { Output = null },
                TotalTokens = null,
                TotalBasis = UsageTotalBasis.Unavailable,
            };
            unknown.Add(ReasoningRelation);
            unknown.Add(TokenSemanticsCatalog.UnknownTotalIncomplete);
        }
        unknown.Sort(StringComparer.Ordinal);
        usage = usage with { UnknownRelations = unknown.Distinct(StringComparer.Ordinal).ToList() };
        return message with
        {
            Usage = usage,
            SourceKind = usage.TotalTokens is not null ? SourceKind.Derived : message.SourceKind,
        };
    }

    private static SessionRecord Session(SessionMeta meta, List<MessageRecord> messages,
        List<RawTokenCounts> counts, bool partial)
    {
        var session = RequestBuilder.Session(Client, meta, messages, counts, partial);
        var usage = session.Usage;
        if (messages.Any(message => message.Usage.UnknownRelations
                .Any(relation => string.Equals(relation, ReasoningRelation, StringComparison.Ordinal))))
        {
            usage = usage with { Buckets = usage.Buckets with { Output = null } };
        }
        var unknown = usage.UnknownRelations.ToList();
        // 无效账本行使整个会话总量未知，不能把剩余已知请求当作全部用量。
        if (partial)
        {
            usage = usage with
            {
                TotalTokens = null,
                TotalBasis = UsageTotalBasis.Unavailable,
            };
            unknown.Add(TokenSemanticsCatalog.UnknownTotalIncomplete);
        }
        foreach (var message in messages)
        {
            unknown.AddRange(message.Usage.UnknownRelations);
        }
        unknown.Sort(StringComparer.Ordinal);
        usage = usage with { UnknownRelations = unknown.Distinct(StringComparer.Ordinal).ToList() };
        return session with
        {
            Usage = usage,
            SourceKind = usage.TotalTokens is not null ? SourceKind.Derived : session.SourceKind,
        };
    }

    private sealed record Snapshot(List<SessionImport> Sessions, int Unparsed);
}
