using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>MiMoCode SQLite 元数据采集器；排除导入副本，只统计原生请求。</summary>
public sealed class MiMoCodeCollector : IClientCollector
{
    private const string Client = "mimocode";
    private const int ParserVersion = 1;

    private const string MessagesQuery = """
        SELECT json_valid(data), session_id, json_object('id',id,'session_id',session_id,
            'role',json_extract(safe,'$.role'),'model',json_extract(safe,'$.modelID'),'provider',json_extract(safe,'$.providerID'),
            'agent',agent_id,'created',COALESCE(json_extract(safe,'$.time.created'),time_created),'completed',json_extract(safe,'$.time.completed'),
            'tokens',json_object('input',json_extract(safe,'$.tokens.input'),'output',json_extract(safe,'$.tokens.output'),
            'reasoning',json_extract(safe,'$.tokens.reasoning'),'cache_read',json_extract(safe,'$.tokens.cache.read'),
            'cache_write',json_extract(safe,'$.tokens.cache.write'),'total',json_extract(safe,'$.tokens.total')))
        FROM (SELECT id,session_id,agent_id,time_created,data,
                      CASE WHEN json_valid(data) THEN data ELSE '{}' END AS safe FROM message)
        ORDER BY id
        """;

    private readonly string? _database;

    public MiMoCodeCollector() : this(DefaultDatabase())
    {
    }

    public MiMoCodeCollector(string? database)
    {
        _database = database;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _database is null ? [] : [_database];

    public static string? DefaultDatabase()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (data is null)
        {
            data = CollectorSupport.Combine(CollectorSupport.HomeDir(), ".local", "share");
        }
        if (data is null) return null;
        var database = Environment.GetEnvironmentVariable("MIMOCODE_DB") ?? "mimocode.db";
        return Path.IsPathFullyQualified(database)
            ? database
            : Path.Combine(data, "mimocode", database);
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_database is null)
        {
            report.RootMissing = true;
            return report;
        }
        if (!File.Exists(_database))
        {
            if (Directory.Exists(_database))
            {
                throw SyncException.Database(new IOException("MiMoCode database path is a directory"));
            }
            report.RootMissing = true;
            return report;
        }
        report.FilesSeen = 1;

        SqliteSignature before;
        try
        {
            before = SqliteSnapshot.Signature(_database);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }

        Snapshot snapshot;
        using (var connection = SqliteSnapshot.Open(_database))
        {
            snapshot = Read(connection.Connection, _database);
        }

        SqliteSignature after;
        try
        {
            after = SqliteSnapshot.Signature(_database);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
        if (before != after) throw SyncException.ChangedDuringScan();

        var source = CollectorSupport.NewSourceFile(Client, _database, SourceFileKind.Sqlite,
            after.Main.Size, after.Main.MtimeMs, 0, ParserVersion, snapshot.Unparsed);

        ImportSummary imported;
        try
        {
            imported = store.ReplaceClientSnapshot(Client, [source], snapshot.Sessions);
        }
        catch (Exception error) when (error is SqliteException or ArgumentException)
        {
            throw SyncException.Store(error);
        }

        report.FilesParsed = 1;
        report.UnparsedLines = snapshot.Unparsed;
        report.SessionsImported = snapshot.Sessions.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private static Snapshot Read(SqliteConnection connection, string sourcePath)
    {
        try
        {
            var imported = ReadImports(connection);
            var sessions = new SortedDictionary<string, SessionMeta>(StringComparer.Ordinal);
            var unparsed = 0;

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT id, parent_id, directory, time_created, time_updated FROM session ORDER BY id";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var meta = new SessionMeta(
                        RequiredString(reader, 0),
                        OptionalString(reader, 1),
                        OptionalString(reader, 2),
                        OptionalInt64(reader, 3),
                        OptionalInt64(reader, 4));
                    if (meta.Id.Length == 0)
                    {
                        unparsed++;
                        continue;
                    }
                    sessions[meta.Id] = meta;
                }
            }

            var details = new SortedDictionary<string, List<Detail>>(StringComparer.Ordinal);
            var partial = new HashSet<string>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = MessagesQuery;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var valid = reader.GetInt64(0) != 0;
                    var sessionId = RequiredString(reader, 1);
                    var json = RequiredString(reader, 2);
                    var row = valid ? ParseRow(json) : null;
                    if (row is null)
                    {
                        unparsed++;
                        partial.Add(sessionId);
                        continue;
                    }
                    if (imported.Messages.Contains((sessionId, row.Id))) continue;
                    if (row.Role is not ("assistant" or "user" or "system"))
                    {
                        unparsed++;
                        partial.Add(sessionId);
                        continue;
                    }
                    if (row.Role != "assistant") continue;
                    var raw = row.Tokens;
                    if (row.Id.Length == 0 || !sessions.ContainsKey(sessionId) ||
                        !CollectorSupport.ValidCounts(raw))
                    {
                        unparsed++;
                        partial.Add(sessionId);
                        continue;
                    }
                    var model = row.Model is { } modelId
                        ? row.Provider is { } provider ? $"{provider}/{modelId}" : modelId
                        : null;
                    var message = RequestBuilder.Request(Client, row.SessionId, new RequestMeta(
                        row.Id,
                        model,
                        row.Agent,
                        row.Created,
                        row.Completed,
                        row.Agent is { } agent && agent != "main",
                        $"message_id={row.Id}"), raw);
                    if (!details.TryGetValue(sessionId, out var list))
                    {
                        list = [];
                        details[sessionId] = list;
                    }
                    list.Add(new Detail(message, raw));
                }
            }

            var result = new List<SessionImport>(sessions.Count);
            foreach (var (id, meta) in sessions)
            {
                if (imported.Sessions.Contains(id) && !details.ContainsKey(id)) continue;
                var rows = details.TryGetValue(id, out var existing) ? existing : [];
                details.Remove(id);
                var messages = new List<MessageRecord>(rows.Count);
                var counts = new List<RawTokenCounts>(rows.Count);
                foreach (var detail in rows)
                {
                    messages.Add(detail.Message);
                    counts.Add(detail.Counts);
                }
                result.Add(new SessionImport(
                    RequestBuilder.Session(Client, meta, messages, counts, partial.Contains(id)),
                    messages,
                    sourcePath,
                    new Dictionary<string, string>()));
            }

            return new Snapshot(result, unparsed);
        }
        catch (SyncException)
        {
            throw;
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException or InvalidCastException)
        {
            throw SyncException.Database(error);
        }
    }

    private static Imported ReadImports(SqliteConnection connection)
    {
        var imported = new Imported();
        foreach (var name in new[] { "claude_import", "external_import" })
        {
            if (!TableExists(connection, name)) continue;
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT session_id, message_ids FROM {name}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sessionId = RequiredString(reader, 0);
                var json = RequiredString(reader, 1);
                List<string>? ids;
                try
                {
                    ids = JsonSerializer.Deserialize<List<string>>(json);
                }
                catch (JsonException error)
                {
                    throw SyncException.Database(error);
                }
                if (ids is null)
                {
                    throw SyncException.Database(new InvalidDataException("invalid import provenance"));
                }
                imported.Sessions.Add(sessionId);
                foreach (var id in ids) imported.Messages.Add((sessionId, id));
            }
        }
        return imported;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static Row? ParseRow(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !RequiredString(root, "id", out var id) ||
                !RequiredString(root, "session_id", out var sessionId) ||
                !OptionalString(root, "role", out var role) ||
                !OptionalString(root, "model", out var model) ||
                !OptionalString(root, "provider", out var provider) ||
                !OptionalString(root, "agent", out var agent) ||
                !OptionalInt64(root, "created", out var created) ||
                !OptionalInt64(root, "completed", out var completed) ||
                !root.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object ||
                !OptionalCount(tokens, "input", out var input) ||
                !OptionalCount(tokens, "output", out var output) ||
                !OptionalCount(tokens, "reasoning", out var reasoning) ||
                !OptionalCount(tokens, "cache_read", out var cacheRead) ||
                !OptionalCount(tokens, "cache_write", out var cacheWrite) ||
                !OptionalCount(tokens, "total", out var total))
            {
                return null;
            }
            return new Row(id, sessionId, role, model, provider, agent, created, completed,
                new RawTokenCounts(input, output, cacheRead, cacheWrite, reasoning, total));
        }
    }

    private static bool RequiredString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool OptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return true;
    }

    private static bool OptionalInt64(JsonElement element, string name, out long? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number)) return false;
        value = number;
        return true;
    }

    private static bool OptionalCount(JsonElement element, string name, out long? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number) ||
            number < 0)
        {
            return false;
        }
        value = number;
        return true;
    }

    private static string RequiredString(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) throw new InvalidDataException("missing required text");
        return reader.GetValue(ordinal) is string value
            ? value
            : throw new InvalidDataException("unexpected text type");
    }

    private static string? OptionalString(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return reader.GetValue(ordinal) is string value
            ? value
            : throw new InvalidDataException("unexpected text type");
    }

    private static long? OptionalInt64(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return reader.GetValue(ordinal) is long value
            ? value
            : throw new InvalidDataException("unexpected integer type");
    }

    private sealed record Snapshot(List<SessionImport> Sessions, int Unparsed);

    private sealed record Detail(MessageRecord Message, RawTokenCounts Counts);

    private sealed record Row(
        string Id,
        string SessionId,
        string? Role,
        string? Model,
        string? Provider,
        string? Agent,
        long? Created,
        long? Completed,
        RawTokenCounts Tokens);

    private sealed class Imported
    {
        public HashSet<string> Sessions { get; } = new(StringComparer.Ordinal);
        public HashSet<(string SessionId, string MessageId)> Messages { get; } = new();
    }
}
