using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>
/// Devin Desktop &amp; CLI SQLite 用量采集器；只投影统计所需的元数据列，正文与扩展列不读取。
/// </summary>
public sealed class DevinCollector : IClientCollector
{
    private const int ParserVersion = 1;
    private const string Client = "devin";
    private const string ReasoningMissing = TokenSemanticsCatalog.UnknownReasoningMissing;
    private const string TotalIncomplete = TokenSemanticsCatalog.UnknownTotalIncomplete;

    /// <summary>进程生命周期内的源签名缓存，重启即失效。</summary>
    private static readonly object SignatureLock = new();
    private static readonly Dictionary<string, SqliteSignature> SignatureCache = new(StringComparer.Ordinal);

    private readonly string? _database;

    public DevinCollector() : this(DefaultDatabase())
    {
    }

    public DevinCollector(string? database)
    {
        _database = database;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _database is null ? [] : [_database];

    /// <summary>按 DEVIN_DATA_DIR → APPDATA → XDG_DATA_HOME → home 的顺序发现数据库。</summary>
    public static string? DefaultDatabase()
    {
        var appdata = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("APPDATA")
            : null;
        return ResolveDatabase(
            Environment.GetEnvironmentVariable("DEVIN_DATA_DIR"),
            appdata,
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            CollectorSupport.HomeDir());
    }

    private static string? ResolveDatabase(string? overrideDir, string? appdata,
        string? xdgDataHome, string? home)
    {
        if (overrideDir is not null)
        {
            return HasDbExtension(overrideDir)
                ? overrideDir
                : Path.Combine(overrideDir, "cli", "sessions.db");
        }
        if (appdata is not null) return Path.Combine(appdata, "Devin", "cli", "sessions.db");
        if (xdgDataHome is not null) return Path.Combine(xdgDataHome, "devin", "cli", "sessions.db");
        return home is null
            ? null
            : Path.Combine(home, ".local", "share", "devin", "cli", "sessions.db");
    }

    /// <summary>判断扩展名是否为 "db"：开头单独的 '.' 不算扩展名。</summary>
    private static bool HasDbExtension(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 && name.Length - dot == 3 &&
               name[dot + 1] == 'd' && name[dot + 2] == 'b';
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
        SqliteSignature signature;
        try
        {
            if (!PathExists(database))
            {
                report.RootMissing = true;
                return report;
            }

            // 复制前先比对完整源签名（主库 + WAL）：未变化则跳过整个 2+ GiB 复制与重解析。
            var current = SqliteSnapshot.Signature(database);
            if (!options.ForceRescan && CachedSignature(database) == current)
            {
                report.FilesSeen = 1;
                report.FilesSkippedUnchanged = 1;
                return report;
            }

            report.FilesSeen = 1;
            signature = SqliteSnapshot.Signature(database);
            using var connection = SqliteSnapshot.Open(database);
            snapshot = Read(connection.Connection, database);
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
                                      InvalidCastException or InvalidOperationException or
                                      ArgumentException or FormatException or OverflowException)
        {
            report.FilesSeen = 1;
            report.FilesFailed = 1;
            return report;
        }

        var source = CollectorSupport.NewSourceFile(Client, database, SourceFileKind.Sqlite,
            signature.Main.Size, signature.Main.MtimeMs, 0, ParserVersion, snapshot.Unparsed);
        ImportSummary imported;
        try
        {
            imported = store.ReplaceClientSnapshot(Client, [source], snapshot.Sessions);
        }
        catch (Exception error) when (error is SqliteException or ArgumentException)
        {
            throw SyncException.Store(error);
        }

        // 仅在成功入库后记录签名；失败或扫描中变化都不记录，下次同步重试。
        RememberSignature(database, signature);

        report.FilesSeen = 1;
        report.FilesParsed = 1;
        report.SessionsImported = snapshot.Sessions.Count;
        report.UnparsedLines = snapshot.Unparsed;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private static bool PathExists(string path)
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

    private static SqliteSignature? CachedSignature(string database)
    {
        lock (SignatureLock)
        {
            return SignatureCache.TryGetValue(database, out var signature) ? signature : null;
        }
    }

    private static void RememberSignature(string database, SqliteSignature signature)
    {
        lock (SignatureLock)
        {
            SignatureCache[database] = signature;
        }
    }

    private static Snapshot Read(SqliteConnection connection, string path)
    {
        var sessionRows = new List<SessionRow>();
        using (var command = connection.CreateCommand())
        {
            // sessions.hidden 不作为删除标记：hidden 会话同样包含真实用量。
            command.CommandText =
                """
                SELECT id, working_directory, model, created_at, last_activity_at
                FROM sessions
                ORDER BY id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sessionRows.Add(new SessionRow(
                    RequiredString(reader, 0),
                    OptionalString(reader, 1),
                    OptionalString(reader, 2),
                    OptionalInt64(reader, 3),
                    OptionalInt64(reader, 4)));
            }
        }

        using var messageCommand = connection.CreateCommand();
        messageCommand.CommandText =
            """
            SELECT
                row_id,
                node_id,
                created_at,
                CASE WHEN json_valid(chat_message) THEN json_extract(chat_message, '$.metadata.request_id') END,
                CASE WHEN json_valid(chat_message) THEN json_extract(chat_message, '$.metadata.generation_model') END,
                CASE WHEN json_valid(chat_message) THEN json_extract(chat_message, '$.metadata.created_at') END,
                CASE WHEN json_valid(chat_message) THEN json_extract(chat_message, '$.metadata.metrics.input_tokens') END,
                CASE WHEN json_valid(chat_message) THEN json_extract(chat_message, '$.metadata.metrics.output_tokens') END,
                CASE WHEN json_valid(chat_message) THEN json_extract(chat_message, '$.metadata.metrics.cache_read_tokens') END,
                CASE WHEN json_valid(chat_message) THEN json_extract(chat_message, '$.metadata.metrics.cache_creation_tokens') END
            FROM message_nodes
            WHERE session_id = $session
              AND json_valid(chat_message)
              AND json_extract(chat_message, '$.metadata.generation_model') IS NOT NULL
            ORDER BY row_id
            """;
        var sessionParameter = messageCommand.Parameters.Add("$session", SqliteType.Text);

        var sessions = new List<SessionImport>(sessionRows.Count);
        var totalUnparsed = 0;
        foreach (var session in sessionRows)
        {
            sessionParameter.Value = session.Id;
            var rawRows = new List<RawRow>();
            var sessionUnparsed = 0;
            var partial = false;

            using (var reader = messageCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    var rowId = RequiredInt64(reader, 0);
                    var nodeId = OptionalInt64(reader, 1);
                    var nodeCreatedAtSec = OptionalInt64(reader, 2);
                    var requestId = OptionalString(reader, 3);
                    var generationModel = OptionalString(reader, 4);
                    var metaCreatedAt = OptionalString(reader, 5);
                    var inputTokens = OptionalInt64(reader, 6);
                    var outputTokens = OptionalInt64(reader, 7);
                    var cacheReadTokens = OptionalInt64(reader, 8);
                    var cacheCreationTokens = OptionalInt64(reader, 9);

                    // generation_model 存在但指标非法（如负数）：计为 unparsed 并标记会话 partial；
                    // JSON 显式 null 不是非法行（缺失 ≠ 非法），按下方口径导入。
                    var isValid = requestId is { Length: > 0 }
                                  && inputTokens is null or >= 0
                                  && outputTokens is null or >= 0
                                  && cacheReadTokens is null or >= 0
                                  && cacheCreationTokens is null or >= 0;
                    if (!isValid)
                    {
                        sessionUnparsed++;
                        partial = true;
                        continue;
                    }

                    // 缺失的不对称处理（见 docs/data-model.md §18 / data-metrics.md 口径 2.13）：
                    // cache_read / cache_creation 的显式 null 按 0 计；input / output 的 null 保持未知。
                    var createdAtMs = ParseRfc3339UtcMs(metaCreatedAt)
                                      ?? (nodeCreatedAtSec is { } seconds
                                          ? SaturatingMul(seconds, 1000)
                                          : null);
                    rawRows.Add(new RawRow(
                        rowId,
                        nodeId,
                        requestId!,
                        generationModel,
                        createdAtMs,
                        inputTokens,
                        outputTokens,
                        cacheReadTokens.GetValueOrDefault(),
                        cacheCreationTokens.GetValueOrDefault()));
                }
            }

            var dedup = DedupSessionRows(rawRows);
            if (dedup.Anomalies > 0)
            {
                sessionUnparsed += dedup.Anomalies;
                partial = true;
            }

            var messages = new List<MessageRecord>(dedup.Rows.Count);
            foreach (var row in dedup.Rows) messages.Add(BuildMessage(session.Id, row));

            var record = BuildSession(session, messages, partial);
            sessions.Add(SessionImport.Create(record, messages, path));
            totalUnparsed += sessionUnparsed;
        }

        return new Snapshot(sessions, totalUnparsed);
    }

    /// <summary>
    /// 按 request_id 合并同一会话内的消息行：保留 (created_at_ms, row_id) 最大的一条；
    /// 组内 Token 四元组冲突时只计异常，绝不静默选择任何一条。
    /// </summary>
    private static DedupResult DedupSessionRows(IReadOnlyList<RawRow> rows)
    {
        var groups = new Dictionary<string, List<RawRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!groups.TryGetValue(row.RequestId, out var group))
            {
                group = [];
                groups.Add(row.RequestId, group);
            }
            group.Add(row);
        }

        var result = new List<RawRow>();
        var anomalies = 0;
        foreach (var group in groups.Values)
        {
            var expected = group[0].TokenTuple();
            var conflict = false;
            foreach (var row in group)
            {
                if (row.TokenTuple() != expected)
                {
                    conflict = true;
                    break;
                }
            }
            if (conflict)
            {
                anomalies++;
                continue;
            }

            RawRow? best = null;
            foreach (var row in group)
            {
                if (best is null || CompareRows(row, best) >= 0) best = row;
            }
            if (best is not null) result.Add(best);
        }

        result.Sort(CompareRows);
        return new DedupResult(result, anomalies);
    }

    private static int CompareRows(RawRow left, RawRow right)
    {
        var byTime = (left.CreatedAtMs ?? 0).CompareTo(right.CreatedAtMs ?? 0);
        return byTime != 0 ? byTime : left.RowId.CompareTo(right.RowId);
    }

    private static MessageRecord BuildMessage(string sessionId, RawRow row)
    {
        var semantics = TokenSemanticsCatalog.Devin;
        var raw = new RawTokenCounts(row.Input, row.Output, row.CacheRead, row.CacheWrite);
        var reference = row.NodeId is { } nodeId
            ? $"row_id={Format(row.RowId)},node_id={Format(nodeId)}"
            : $"row_id={Format(row.RowId)}";
        var message = RequestBuilder.MessageWithSemantics(Client, semantics, sessionId,
            new RequestMeta(row.RequestId, row.Model, null, row.CreatedAtMs, null, false, reference), raw);

        // Devin 不上报 reasoning，四桶之外始终标注 reasoning.missing。
        var unknown = message.Usage.UnknownRelations.ToList();
        if (!unknown.Contains(ReasoningMissing, StringComparer.Ordinal)) unknown.Add(ReasoningMissing);
        unknown.Sort(StringComparer.Ordinal);
        unknown = unknown.Distinct(StringComparer.Ordinal).ToList();
        return message with
        {
            Usage = message.Usage with { UnknownRelations = unknown },
        };
    }

    private static SessionRecord BuildSession(SessionRow meta, List<MessageRecord> messages, bool partial)
    {
        var semantics = TokenSemanticsCatalog.Devin;
        var projectPath = string.IsNullOrWhiteSpace(meta.WorkingDirectory) ? null : meta.WorkingDirectory;
        var model = string.IsNullOrWhiteSpace(meta.Model) ? null : meta.Model;

        var startedAtMs = meta.CreatedAtSec is { } created
            ? SaturatingMul(created, 1000)
            : (long?)null;
        var endedAtMs = meta.LastActivityAtSec is { } activity
            ? SaturatingMul(activity, 1000)
            : (long?)null;
        foreach (var message in messages)
        {
            if ((message.CompletedAtMs ?? message.CreatedAtMs) is not { } value) continue;
            if (endedAtMs is null || value > endedAtMs) endedAtMs = value;
        }

        NormalizedUsage usage;
        if (messages.Count == 0)
        {
            usage = new NormalizedUsage(
                new TokenBuckets(),
                null,
                UsageTotalBasis.Unavailable,
                [TotalIncomplete],
                false);
        }
        else
        {
            long? input = 0;
            long? output = 0;
            long? cacheRead = 0;
            long? cacheWrite = 0;
            long? total = 0;
            var unknown = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                AddBucket(ref input, message.Usage.Buckets.Input);
                AddBucket(ref output, message.Usage.Buckets.Output);
                AddBucket(ref cacheRead, message.Usage.Buckets.CacheRead);
                AddBucket(ref cacheWrite, message.Usage.Buckets.CacheWrite);
                total = total is { } current && message.Usage.TotalTokens is { } value
                    ? CheckedAdd(current, value)
                    : null;
                foreach (var relation in message.Usage.UnknownRelations) unknown.Add(relation);
            }

            if (partial)
            {
                total = null;
                unknown.Add(TotalIncomplete);
            }

            UsageTotalBasis basis;
            if (total is not null)
            {
                basis = UsageTotalBasis.DerivedFromBuckets;
            }
            else
            {
                unknown.Add(TotalIncomplete);
                basis = UsageTotalBasis.Unavailable;
            }

            var relations = unknown.ToList();
            relations.Sort(StringComparer.Ordinal);
            usage = new NormalizedUsage(
                new TokenBuckets(input, output, null, cacheRead, cacheWrite),
                total,
                basis,
                relations,
                false);
        }

        return new SessionRecord
        {
            Client = Client,
            ClientSessionId = meta.Id,
            ParentClientSessionId = null,
            ProjectPath = projectPath,
            Model = model,
            StartedAtMs = startedAtMs,
            EndedAtMs = endedAtMs,
            SemanticsId = semantics.Id,
            TotalBasis = SessionTotalBasis.SumOfMessages,
            Coverage = partial
                ? MessageCoverage.Partial
                : messages.Count == 0
                    ? MessageCoverage.NoMessages
                    : MessageCoverage.Complete,
            Usage = usage,
            SourceKind = SourceKind.Measured,
            CostSource = CostSource.None,
            ClientCostUsd = null,
        };
    }

    private static void AddBucket(ref long? slot, long? value)
    {
        slot = slot is { } current && value is { } number ? CheckedAdd(current, number) : null;
    }

    private static long? CheckedAdd(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>解析 RFC3339 纳秒/微秒/毫秒 UTC 字符串至毫秒，仅接受以 <c>Z</c> 结尾的 UTC 形式。</summary>
    private static long? ParseRfc3339UtcMs(string? value)
    {
        if (value is null) return null;
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length < 20 || bytes[4] != (byte)'-' || bytes[7] != (byte)'-' ||
            bytes[10] != (byte)'T' || bytes[13] != (byte)':' || bytes[16] != (byte)':' ||
            bytes[^1] != (byte)'Z')
        {
            return null;
        }

        if (!TryDigits(bytes, 0, 4, out var year) ||
            !TryDigits(bytes, 5, 2, out var month) ||
            !TryDigits(bytes, 8, 2, out var day) ||
            !TryDigits(bytes, 11, 2, out var hour) ||
            !TryDigits(bytes, 14, 2, out var minute) ||
            !TryDigits(bytes, 17, 2, out var second))
        {
            return null;
        }

        if (month is < 1 or > 12 || day is < 1 or > 31 ||
            hour > 23 || minute > 59 || second > 60)
        {
            return null;
        }

        long millis = 0;
        if (bytes.Length > 20)
        {
            if (bytes[19] != (byte)'.') return null;
            var fractionLength = bytes.Length - 21;
            if (fractionLength <= 0) return null;
            for (var index = 20; index < bytes.Length - 1; index++)
            {
                if (bytes[index] is < (byte)'0' or > (byte)'9') return null;
            }
            for (var index = 0; index < 3; index++)
            {
                millis *= 10;
                if (index < fractionLength) millis += bytes[20 + index] - (byte)'0';
            }
        }

        var days = DaysFromCivil(year, month, day);
        var seconds = days * 86_400 + hour * 3_600 + minute * 60 + second;
        return seconds * 1_000 + millis;
    }

    private static bool TryDigits(byte[] bytes, int offset, int length, out long value)
    {
        long result = 0;
        for (var index = 0; index < length; index++)
        {
            var digit = bytes[offset + index];
            if (digit is < (byte)'0' or > (byte)'9')
            {
                value = 0;
                return false;
            }
            result = result * 10 + (digit - (byte)'0');
        }
        value = result;
        return true;
    }

    private static long DaysFromCivil(long year, long month, long day)
    {
        var adjustedYear = month <= 2 ? year - 1 : year;
        var era = (adjustedYear >= 0 ? adjustedYear : adjustedYear - 399) / 400;
        var yearOfEra = adjustedYear - era * 400;
        var dayOfYear = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
        var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        return era * 146_097 + dayOfEra - 719_468;
    }

    private static long SaturatingMul(long value, long factor)
    {
        if (value > long.MaxValue / factor) return long.MaxValue;
        if (value < long.MinValue / factor) return long.MinValue;
        return value * factor;
    }

    private static string RequiredString(SqliteDataReader reader, int index)
    {
        if (reader.GetValue(index) is string value) return value;
        throw new InvalidDataException($"unexpected SQLite type in column {index}");
    }

    private static string? OptionalString(SqliteDataReader reader, int index) =>
        reader.GetValue(index) switch
        {
            DBNull => null,
            string value => value,
            _ => throw new InvalidDataException($"unexpected SQLite type in column {index}"),
        };

    private static long RequiredInt64(SqliteDataReader reader, int index)
    {
        if (reader.GetValue(index) is long value) return value;
        throw new InvalidDataException($"unexpected SQLite type in column {index}");
    }

    private static long? OptionalInt64(SqliteDataReader reader, int index) =>
        reader.GetValue(index) switch
        {
            DBNull => null,
            long value => value,
            _ => throw new InvalidDataException($"unexpected SQLite type in column {index}"),
        };

    private sealed record SessionRow(
        string Id,
        string? WorkingDirectory,
        string? Model,
        long? CreatedAtSec,
        long? LastActivityAtSec);

    private sealed record RawRow(
        long RowId,
        long? NodeId,
        string RequestId,
        string? Model,
        long? CreatedAtMs,
        long? Input,
        long? Output,
        long CacheRead,
        long CacheWrite)
    {
        public (long? Input, long? Output, long CacheRead, long CacheWrite) TokenTuple() =>
            (Input, Output, CacheRead, CacheWrite);
    }

    private sealed record DedupResult(List<RawRow> Rows, int Anomalies);

    private sealed record Snapshot(List<SessionImport> Sessions, int Unparsed);
}
