using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>
/// OpenCode SQLite 采集器。只读取 session 聚合列与 message.data 的用量字段，
/// 不读取正文、part、credential 等表；主库与 WAL 一律通过 <see cref="SqliteSnapshot"/> 快照读取。
/// </summary>
public sealed class OpenCodeCollector : IClientCollector
{
    private const string Client = "opencode";
    private const long ParserVersion = 1;

    /// <summary>进程内完整签名缓存（主库 + WAL）；重启后的首次同步仍全量扫描。</summary>
    private static readonly ConcurrentDictionary<string, SqliteSignature> Signatures =
        new(StringComparer.Ordinal);

    private readonly string? _database;

    public OpenCodeCollector() : this(DefaultDatabase())
    {
    }

    public OpenCodeCollector(string? database)
    {
        _database = database;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _database is null ? [] : [_database];

    public static string? DefaultDatabase()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (dataHome is not null)
        {
            return Path.Combine(dataHome, "opencode", "opencode.db");
        }
        var home = CollectorSupport.HomeDir();
        return CollectorSupport.Combine(home, ".local", "share", "opencode", "opencode.db");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_database is null || !File.Exists(_database))
        {
            report.RootMissing = true;
            return report;
        }

        report.FilesSeen = 1;
        var path = _database;
        var observed = Observe(path);
        if (!options.ForceRescan && IsUnchanged(store, path, observed))
        {
            report.FilesSkippedUnchanged = 1;
            CollectorSupport.CountReason(report, ScanReason.Unchanged);
            return report;
        }

        Snapshot snapshot;
        try
        {
            using var database = SqliteSnapshot.Open(path);
            snapshot = ReadSnapshot(database.Connection, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }

        // 快照在临时副本上解析完成后，再确认源库主文件与 WAL 均未变化。
        if (Observe(path) != observed)
        {
            throw SyncException.ChangedDuringScan();
        }

        var source = CollectorSupport.NewSourceFile(Client, path, SourceFileKind.Sqlite,
            observed.Main.Size, observed.Main.MtimeMs, 0, ParserVersion, snapshot.Unparsed);
        var imported = store.ReplaceClientSnapshot(Client, [source], snapshot.Sessions);
        report.FilesParsed = 1;
        report.UnparsedLines = snapshot.Unparsed;
        report.SessionsImported = snapshot.Sessions.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        Signatures[path] = observed;
        return report;
    }

    private static SqliteSignature Observe(string path)
    {
        try
        {
            return SqliteSnapshot.Signature(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
    }

    private static bool IsUnchanged(UsageStore store, string path, SqliteSignature observed)
    {
        if (!Signatures.TryGetValue(path, out var cached) || cached != observed) return false;
        var row = store.SourceFile(Client, path);
        if (row is null || row.ParserVersion != ParserVersion) return false;
        if (row.SizeBytes != observed.Main.Size || row.MtimeMs != observed.Main.MtimeMs) return false;
        var paths = store.SourcePaths(Client);
        return paths.Count == 1 && string.Equals(paths[0], path, StringComparison.Ordinal);
    }

    private static Snapshot ReadSnapshot(SqliteConnection connection, string sourcePath)
    {
        try
        {
            return SnapshotReader.Read(connection, sourcePath);
        }
        catch (SqliteException error)
        {
            throw SyncException.Database(error);
        }
        catch (InvalidCastException error)
        {
            throw SyncException.Database(error);
        }
        catch (InvalidOperationException error)
        {
            throw SyncException.Database(error);
        }
    }

    private sealed record Snapshot(List<SessionImport> Sessions, int Unparsed);

    private static class SnapshotReader
    {
        public static Snapshot Read(SqliteConnection connection, string sourcePath)
        {
            var unparsed = 0;
            var hasLegacy = TableExists(connection, "session");
            var hasCurrent = TableExists(connection, "session_v2");
            if (!hasLegacy && !hasCurrent)
            {
                throw SyncException.Database(new InvalidDataException("OpenCode database has no session table"));
            }

            var legacy = hasLegacy
                ? ReadSessions(connection, "session", ref unparsed)
                : new Dictionary<string, SessionRow>(StringComparer.Ordinal);
            var current = hasCurrent
                ? ReadSessions(connection, "session_v2", ref unparsed)
                : new Dictionary<string, SessionRow>(StringComparer.Ordinal);

            var messages = ReadMessages(connection, ref unparsed);
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            ids.UnionWith(legacy.Keys);
            ids.UnionWith(current.Keys);

            var imports = new List<SessionImport>(ids.Count);
            foreach (var id in ids)
            {
                legacy.TryGetValue(id, out var legacyRow);
                current.TryGetValue(id, out var currentRow);
                var row = Pick(legacyRow, currentRow);
                if (row is null) continue;

                var session = BuildSession(row);
                var details = messages.Remove(id, out var list) ? list.Items : [];
                if (session.Model is null && details.Count > 0)
                {
                    MessageRecord? latest = null;
                    foreach (var message in details)
                    {
                        if (latest is null ||
                            (message.CreatedAtMs ?? long.MinValue) >=
                            (latest.CreatedAtMs ?? long.MinValue))
                        {
                            latest = message;
                        }
                    }
                    session = session with { Model = latest?.Model };
                }
                session = session with { Coverage = CoverageOf(details, session.Usage.TotalTokens) };
                imports.Add(new SessionImport(session, details, sourcePath,
                    new Dictionary<string, string>(StringComparer.Ordinal)));
            }

            // 没有对应会话行的消息不导入，但按原始行数计入 unparsed。
            foreach (var list in messages.Values) unparsed += list.RawCount;
            return new Snapshot(imports, unparsed);
        }

        private static Dictionary<string, SessionRow> ReadSessions(SqliteConnection connection,
            string table, ref int unparsed)
        {
            var columns = Columns(connection, table);
            if (!columns.Contains("id"))
            {
                throw SyncException.Database(new InvalidDataException($"OpenCode {table} has no id column"));
            }

            string Column(string name) => columns.Contains(name) ? name : "NULL";
            var model = Column("model");
            var sql = $@"SELECT json_object('id', id, 'parent_id', {Column("parent_id")},
                'directory', {Column("directory")},
                'model', CASE WHEN json_valid({model}) THEN json_object('id', json_extract({model}, '$.id'),
                    'modelID', json_extract({model}, '$.modelID'),
                    'providerID', json_extract({model}, '$.providerID')) ELSE NULL END,
                'created', {Column("time_created")}, 'updated', {Column("time_updated")},
                'cost', {Column("cost")},
                'tokens', json_object('input', {Column("tokens_input")},
                    'output', {Column("tokens_output")}, 'reasoning', {Column("tokens_reasoning")},
                    'cache_read', {Column("tokens_cache_read")},
                    'cache_write', {Column("tokens_cache_write")}, 'total', {Column("tokens_total")}))
                FROM {table}";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, SessionRow>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    throw SyncException.Database(new InvalidDataException("OpenCode session projection was NULL"));
                }
                SessionRow row;
                try
                {
                    row = ParseSession(reader.GetString(0));
                }
                catch (Exception error) when (error is JsonException or RowFormatException)
                {
                    unparsed++;
                    continue;
                }
                if (row.Id.Length == 0 || !CollectorSupport.ValidCounts(row.Usage))
                {
                    unparsed++;
                    continue;
                }
                result[row.Id] = row;
            }
            return result;
        }

        private static Dictionary<string, MessageList> ReadMessages(SqliteConnection connection,
            ref int unparsed)
        {
            var result = new Dictionary<string, MessageList>(StringComparer.Ordinal);
            if (!TableExists(connection, "message")) return result;
            var columns = Columns(connection, "message");
            if (!columns.Contains("id") || !columns.Contains("session_id") || !columns.Contains("data"))
            {
                throw SyncException.Database(new InvalidDataException(
                    "OpenCode message table lacks required columns"));
            }

            const string sql = @"SELECT json_valid(data), json_object('id', id, 'session_id', session_id,
                'role', json_extract(safe, '$.role'),
                'model_id', COALESCE(json_extract(safe, '$.modelID'),
                    json_extract(safe, '$.model.modelID'), json_extract(safe, '$.model.id')),
                'provider_id', COALESCE(json_extract(safe, '$.providerID'),
                    json_extract(safe, '$.model.providerID')),
                'agent', COALESCE(json_extract(safe, '$.agent'), json_extract(safe, '$.mode')),
                'created', json_extract(safe, '$.time.created'),
                'completed', json_extract(safe, '$.time.completed'),
                'cost', json_extract(safe, '$.cost'),
                'tokens', json_object('input', json_extract(safe, '$.tokens.input'),
                    'output', json_extract(safe, '$.tokens.output'),
                    'reasoning', json_extract(safe, '$.tokens.reasoning'),
                    'cache_read', json_extract(safe, '$.tokens.cache.read'),
                    'cache_write', json_extract(safe, '$.tokens.cache.write'),
                    'total', json_extract(safe, '$.tokens.total')))
                FROM (SELECT id, session_id, data, CASE WHEN json_valid(data) THEN data ELSE '{}' END AS safe
                    FROM message)
                ORDER BY id";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                {
                    throw SyncException.Database(new InvalidDataException(
                        "OpenCode message JSON projection was NULL"));
                }
                var valid = reader.GetInt64(0);
                if (valid is not (0 or 1))
                {
                    throw SyncException.Database(new InvalidDataException(
                        "OpenCode message JSON validity was not boolean"));
                }
                if (valid == 0)
                {
                    unparsed++;
                    continue;
                }

                MessageCandidate candidate;
                try
                {
                    candidate = ParseMessage(reader.GetString(1));
                }
                catch (Exception error) when (error is JsonException or RowFormatException)
                {
                    unparsed++;
                    continue;
                }

                switch (candidate.Role)
                {
                    case "assistant":
                        break;
                    case "user":
                    case "system":
                        continue;
                    default:
                        unparsed++;
                        continue;
                }

                if (candidate.Id.Length == 0 || candidate.SessionId.Length == 0 ||
                    !CollectorSupport.ValidCounts(candidate.Usage))
                {
                    unparsed++;
                    continue;
                }

                var message = BuildMessage(candidate);
                if (!result.TryGetValue(candidate.SessionId, out var list))
                {
                    list = new MessageList();
                    result[candidate.SessionId] = list;
                }
                list.Add(candidate.Id, message);
            }
            return result;
        }

        private static SessionRow? Pick(SessionRow? legacy, SessionRow? current)
        {
            if (legacy is not null && current is not null)
            {
                return (legacy.UpdatedMs ?? long.MinValue) > (current.UpdatedMs ?? long.MinValue)
                    ? legacy
                    : current;
            }
            return legacy ?? current;
        }

        private static SessionRecord BuildSession(SessionRow row)
        {
            var semantics = TokenSemanticsCatalog.OpenCode;
            return new SessionRecord
            {
                Client = Client,
                ClientSessionId = row.Id,
                ParentClientSessionId = string.IsNullOrEmpty(row.ParentId) ? null : row.ParentId,
                ProjectPath = row.Directory,
                Model = row.Model,
                StartedAtMs = row.CreatedMs,
                EndedAtMs = row.UpdatedMs,
                SemanticsId = semantics.Id,
                TotalBasis = SessionTotalBasis.ClientReported,
                Coverage = MessageCoverage.Complete,
                Usage = UsageNormalizer.Normalize(semantics, row.Usage),
                SourceKind = SourceKind.Measured,
                CostSource = row.CostUsd is null ? CostSource.None : CostSource.ClientReported,
                ClientCostUsd = row.CostUsd,
            };
        }

        private static MessageRecord BuildMessage(MessageCandidate row)
        {
            var semantics = TokenSemanticsCatalog.OpenCode;
            var cost = FilterCost(row.CostUsd);
            return new MessageRecord
            {
                Client = Client,
                ClientSessionId = row.SessionId,
                DedupKey = row.Id,
                CreatedAtMs = row.CreatedMs,
                CompletedAtMs = row.CompletedMs,
                Model = row.ModelId is null
                    ? null
                    : row.ProviderId is { } provider ? $"{provider}/{row.ModelId}" : row.ModelId,
                Agent = row.Agent,
                Usage = UsageNormalizer.Normalize(semantics, row.Usage),
                IsSidechain = false,
                SemanticsId = semantics.Id,
                SourceKind = SourceKind.Measured,
                SourceRef = $"message_id={row.Id}",
                CostSource = cost is null ? CostSource.None : CostSource.ClientReported,
                ClientCostUsd = cost,
            };
        }

        private static MessageCoverage CoverageOf(List<MessageRecord> messages, long? sessionTotal)
        {
            if (messages.Count == 0) return MessageCoverage.NoMessages;
            long? total = 0;
            foreach (var message in messages)
            {
                if (message.Usage.TotalTokens is not { } value) return MessageCoverage.Partial;
                if (total is not { } current || current > long.MaxValue - value)
                {
                    return MessageCoverage.Partial;
                }
                total = current + value;
            }
            return total == sessionTotal ? MessageCoverage.Complete : MessageCoverage.Partial;
        }

        private static bool TableExists(SqliteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
            command.Parameters.AddWithValue("$name", table);
            return command.ExecuteScalar() is not null;
        }

        private static HashSet<string> Columns(SqliteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) columns.Add(reader.GetString(1));
            return columns;
        }

        private static SessionRow ParseSession(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new RowFormatException();
            return new SessionRow(
                RequiredString(root, "id"),
                OptionalString(root, "parent_id"),
                OptionalString(root, "directory"),
                ModelLabel(OptionalModel(root, "model")),
                OptionalInt64(root, "created"),
                OptionalInt64(root, "updated"),
                FilterCost(OptionalDouble(root, "cost")),
                ReadCounts(RequiredProperty(root, "tokens")));
        }

        private static MessageCandidate ParseMessage(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new RowFormatException();
            return new MessageCandidate(
                RequiredString(root, "id"),
                RequiredString(root, "session_id"),
                OptionalString(root, "role"),
                OptionalString(root, "model_id"),
                OptionalString(root, "provider_id"),
                OptionalString(root, "agent"),
                OptionalInt64(root, "created"),
                OptionalInt64(root, "completed"),
                OptionalDouble(root, "cost"),
                ReadCounts(RequiredProperty(root, "tokens")));
        }

        private static RawTokenCounts ReadCounts(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) throw new RowFormatException();
            return new RawTokenCounts(
                OptionalUnsigned(element, "input"),
                OptionalUnsigned(element, "output"),
                OptionalUnsigned(element, "cache_read"),
                OptionalUnsigned(element, "cache_write"),
                OptionalUnsigned(element, "reasoning"),
                OptionalUnsigned(element, "total"));
        }

        private static string? ModelLabel(ModelFields? model)
        {
            if (model is not { } fields) return null;
            var id = fields.Id ?? fields.ModelId;
            if (id is null) return null;
            return fields.ProviderId is { } provider ? $"{provider}/{id}" : id;
        }

        private static ModelFields? OptionalModel(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            if (value.ValueKind != JsonValueKind.Object) throw new RowFormatException();
            return new ModelFields(
                OptionalString(value, "id"),
                OptionalString(value, "modelID"),
                OptionalString(value, "providerID"));
        }

        private static JsonElement RequiredProperty(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) ? value : throw new RowFormatException();

        private static string RequiredString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                throw new RowFormatException();
            }
            return value.GetString()!;
        }

        private static string? OptionalString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            if (value.ValueKind != JsonValueKind.String) throw new RowFormatException();
            return value.GetString();
        }

        private static long? OptionalInt64(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            {
                throw new RowFormatException();
            }
            return number;
        }

        private static long? OptionalUnsigned(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) ||
                number < 0)
            {
                throw new RowFormatException();
            }
            return number;
        }

        private static double? OptionalDouble(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
            {
                throw new RowFormatException();
            }
            return number;
        }

        private static double? FilterCost(double? cost) =>
            cost is { } value && double.IsFinite(value) && value >= 0 ? value : null;

        private sealed record SessionRow(
            string Id,
            string? ParentId,
            string? Directory,
            string? Model,
            long? CreatedMs,
            long? UpdatedMs,
            double? CostUsd,
            RawTokenCounts Usage);

        private sealed record MessageCandidate(
            string Id,
            string SessionId,
            string? Role,
            string? ModelId,
            string? ProviderId,
            string? Agent,
            long? CreatedMs,
            long? CompletedMs,
            double? CostUsd,
            RawTokenCounts Usage);

        private sealed record ModelFields(string? Id, string? ModelId, string? ProviderId);

        private sealed class MessageList
        {
            private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
            private readonly List<MessageRecord> _items = new();

            public int RawCount { get; private set; }

            public List<MessageRecord> Items => _items;

            public void Add(string id, MessageRecord message)
            {
                RawCount++;
                if (_index.TryGetValue(id, out var index))
                {
                    _items[index] = message;
                    return;
                }
                _index[id] = _items.Count;
                _items.Add(message);
            }
        }

        private sealed class RowFormatException : Exception
        {
        }
    }
}
