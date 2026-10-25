using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>
/// Qoder IDE SQLite 会话与 CLI 项目转录采集器。只读取身份、时间、模型与 token 元数据，
/// IDE 正文与 CLI 提示/回答文本既不查询也不保存。
/// </summary>
public sealed class QoderCollector : IClientCollector
{
    private const string Client = "qoder";
    private const long ParserVersion = 4;

    private readonly IReadOnlyList<string> _cliRoots;
    private readonly IReadOnlyList<string> _databases;

    public QoderCollector() : this(DefaultCliRoots(), DefaultDatabases())
    {
    }

    public QoderCollector(IReadOnlyList<string> cliRoots, IReadOnlyList<string> databases)
    {
        _cliRoots = cliRoots.ToList();
        _databases = databases.ToList();
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _cliRoots.Concat(_databases).ToList();

    /// <summary>CLI 项目根：QODER_HOME/projects、QODER_CN_HOME/projects，缺省回退到主目录。</summary>
    public static IReadOnlyList<string> DefaultCliRoots()
    {
        var roots = new List<string>();
        foreach (var (name, directory) in new[]
                 {
                     ("QODER_HOME", ".qoder"),
                     ("QODER_CN_HOME", ".qoder-cn"),
                 })
        {
            var root = CollectorSupport.EnvPath(name);
            if (root is null)
            {
                var home = CollectorSupport.HomeDir();
                if (home is null)
                {
                    continue;
                }
                root = Path.Combine(home, directory);
            }
            roots.Add(Path.Combine(root, "projects"));
        }
        return Dedup(roots);
    }

    /// <summary>两个发行版的 IDE 数据库，支持 QODER_DB / QODER_CN_DB 覆盖。</summary>
    public static IReadOnlyList<string> DefaultDatabases()
    {
        var bases = DataBases();
        var paths = new List<string>();
        var qoder = CollectorSupport.EnvPath("QODER_DB");
        if (qoder is not null)
        {
            paths.Add(qoder);
        }
        else
        {
            paths.AddRange(bases.Select(basePath => DatabaseUnder(basePath, "Qoder")));
        }
        var qoderCn = CollectorSupport.EnvPath("QODER_CN_DB");
        if (qoderCn is not null)
        {
            paths.Add(qoderCn);
        }
        else
        {
            paths.AddRange(bases.Select(basePath => DatabaseUnder(basePath, "QoderCN")));
        }
        return Dedup(paths);
    }

    private static List<string> DataBases()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return appData is null ? [] : [appData];
    }

    private static string DatabaseUnder(string basePath, string product) =>
        Path.Combine(basePath, product, "SharedClientCache", "cache", "db", "local.db");

    private static List<string> Dedup(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return paths.Where(seen.Add).ToList();
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        var (cli, databases) = Existing();
        if (cli.Count == 0 && databases.Count == 0)
        {
            report.RootMissing = true;
            return report;
        }

        var inputs = Inputs(cli, databases);
        report.FilesSeen = inputs.Count;
        var (sources, unchanged) = Sources(inputs, store, options.ForceRescan);
        // IDE 数据库可能只提交 WAL 而不改变主文件 mtime，因此只要存在数据库就必须解析。
        if (unchanged && databases.Count == 0)
        {
            report.FilesSkippedUnchanged = inputs.Count;
            return report;
        }

        var signatures = new Dictionary<string, SqliteSignature>(StringComparer.Ordinal);
        foreach (var database in databases)
        {
            signatures[database] = Signature(database);
        }

        var drafts = new List<Draft>();
        foreach (var database in databases)
        {
            Parsed parsed;
            try
            {
                using var snapshot = SqliteSnapshot.Open(database);
                parsed = ParseIde(snapshot.Connection, database);
            }
            catch (SyncException)
            {
                throw;
            }
            catch (Exception error) when (error is SqliteException or InvalidDataException)
            {
                throw SyncException.Database(error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw SyncException.FromSource(error);
            }
            report.FilesParsed++;
            report.UnparsedLines += parsed.Unparsed;
            UpdateUnparsed(sources, database, parsed.Unparsed);
            drafts.AddRange(parsed.Imports.Select(import => new Draft(import, true)));
        }

        foreach (var path in DiscoverCli(cli))
        {
            var fallback = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (fallback.Length == 0)
            {
                fallback = "unknown";
            }
            Parsed parsed;
            try
            {
                parsed = ParseCli(path, fallback);
            }
            catch (SyncException)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw SyncException.FromSource(error);
            }
            report.FilesParsed++;
            report.UnparsedLines += parsed.Unparsed;
            UpdateUnparsed(sources, path, parsed.Unparsed);
            drafts.AddRange(parsed.Imports.Select(import => new Draft(import, false)));
        }

        var best = new SortedDictionary<string, Draft>(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            var id = draft.Import.Session.ClientSessionId;
            if (!best.TryGetValue(id, out var existing))
            {
                best.Add(id, draft);
                continue;
            }
            // IDE 与 CLI 在迁移后可能共享同一会话 id；保留一条，不重复计数。
            report.IgnoredLines++;
            var requests = draft.Import.Messages.Count;
            var existingRequests = existing.Import.Messages.Count;
            if (requests > existingRequests ||
                (requests == existingRequests && draft.FromIde && !existing.FromIde))
            {
                best[id] = draft;
            }
        }
        var imports = best.Values.Select(draft => draft.Import).ToList();

        // 不发布与追加、WAL 提交或新文件竞争的扫描结果。
        if (!Inputs(cli, databases).SequenceEqual(inputs))
        {
            throw SyncException.ChangedDuringScan();
        }
        foreach (var (input, source) in inputs.Zip(sources))
        {
            var (size, mtime) = Fingerprint(input.Path);
            if (source.SizeBytes != size || source.MtimeMs != mtime)
            {
                throw SyncException.ChangedDuringScan();
            }
        }
        foreach (var database in databases)
        {
            if (signatures[database] != Signature(database))
            {
                throw SyncException.ChangedDuringScan();
            }
        }

        ImportSummary imported;
        try
        {
            imported = store.ReplaceClientSnapshot(Client, sources, imports);
        }
        catch (Exception error) when (error is SqliteException or ArgumentException)
        {
            throw SyncException.Store(error);
        }

        report.SessionsImported = imports.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private (List<string> Cli, List<string> Databases) Existing()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cli = new List<string>();
        foreach (var root in _cliRoots)
        {
            if (Exists(root) && seen.Add(root))
            {
                cli.Add(root);
            }
        }
        var databases = new List<string>();
        foreach (var database in _databases)
        {
            if (Exists(database) && seen.Add(database))
            {
                databases.Add(database);
            }
        }
        cli.Sort(StringComparer.Ordinal);
        databases.Sort(StringComparer.Ordinal);
        return (cli, databases);
    }

    private static List<Input> Inputs(IReadOnlyList<string> cli, IReadOnlyList<string> databases)
    {
        var inputs = databases
            .Select(path => new Input(path, SourceFileKind.Sqlite))
            .ToList();
        inputs.AddRange(DiscoverCli(cli).Select(path => new Input(path, SourceFileKind.Jsonl)));
        inputs.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return inputs;
    }

    private static List<string> DiscoverCli(IReadOnlyList<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            files.AddRange(CliFiles(root));
        }
        files.Sort(StringComparer.Ordinal);
        var deduped = new List<string>(files.Count);
        foreach (var file in files)
        {
            if (deduped.Count == 0 || !string.Equals(deduped[^1], file, StringComparison.Ordinal))
            {
                deduped.Add(file);
            }
        }
        return deduped;
    }

    /// <summary>每个项目目录下的直接 .jsonl；嵌套 transcript 目录属于日志管线，不扫描。</summary>
    private static List<string> CliFiles(string root)
    {
        var files = new List<string>();
        if (!Exists(root))
        {
            return files;
        }
        foreach (var entry in Enumerate(root))
        {
            var attributes = Attributes(entry);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) == 0 && IsJsonl(entry))
                {
                    files.Add(entry);
                }
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            foreach (var nested in Enumerate(entry))
            {
                var nestedAttributes = Attributes(nested);
                if ((nestedAttributes & FileAttributes.Directory) == 0 &&
                    (nestedAttributes & FileAttributes.ReparsePoint) == 0 &&
                    IsJsonl(nested))
                {
                    files.Add(nested);
                }
            }
        }
        return files;
    }

    private static bool IsJsonl(string path) =>
        string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.Ordinal);

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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
    }

    private static FileAttributes Attributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
    }

    private static string[] Enumerate(string directory)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.Directory(error);
        }
    }

    private static (long Size, long MtimeMs) Fingerprint(string path)
    {
        try
        {
            return CollectorSupport.Fingerprint(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
    }

    private static SqliteSignature Signature(string path)
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

    private static (List<SourceFile> Sources, bool Unchanged) Sources(
        IReadOnlyList<Input> inputs, UsageStore store, bool force)
    {
        var sources = new List<SourceFile>(inputs.Count);
        var unchanged = !force;
        foreach (var input in inputs)
        {
            var (size, mtime) = Fingerprint(input.Path);
            var previous = store.SourceFile(Client, input.Path);
            unchanged &= CollectorSupport.IsUnchanged(previous, size, mtime, ParserVersion);
            sources.Add(CollectorSupport.NewSourceFile(Client, input.Path, input.Kind,
                size, mtime, input.Kind == SourceFileKind.Jsonl ? size : 0, ParserVersion, 0));
        }
        unchanged &= store.SourcePaths(Client).SequenceEqual(
            sources.Select(source => source.Path), StringComparer.Ordinal);
        return (sources, unchanged);
    }

    private static void UpdateUnparsed(List<SourceFile> sources, string path, int unparsed)
    {
        var index = sources.FindIndex(source =>
            string.Equals(source.Path, path, StringComparison.Ordinal));
        if (index >= 0)
        {
            sources[index] = sources[index] with { UnparsedRecords = unparsed };
        }
    }

    // ------------------------------------------------------------------ CLI

    private static Parsed ParseCli(string path, string fallbackId)
    {
        var candidates = new Dictionary<string, CliCandidate>(StringComparer.Ordinal);
        var unparsed = 0;
        var index = 0;
        string? sessionId = null;
        string? project = null;
        var sawValid = false;

        try
        {
            CollectorSupport.ReadJsonLines(path, (line, _) =>
            {
                var current = index++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    return;
                }
                if (!TryReadCliEntry(trimmed, out var entry))
                {
                    unparsed++;
                    return;
                }
                sawValid = true;
                if (sessionId is null && !string.IsNullOrEmpty(entry.SessionId))
                {
                    sessionId = entry.SessionId;
                }
                if (project is null && !string.IsNullOrEmpty(entry.Cwd))
                {
                    project = entry.Cwd;
                }
                if (!string.Equals(entry.Kind, "assistant", StringComparison.Ordinal))
                {
                    return;
                }
                if (entry.Message is not { } message || !message.HasUsage)
                {
                    return;
                }
                // 未启用 EXPOSE_TOKEN_USAGE 的历史记录会被运行时清零；新记录优先使用实测值。
                RawTokenCounts raw;
                var estimated = false;
                if (message.Raw is { } counts && !IsPlaceholderUsage(counts))
                {
                    raw = CliCounts(counts);
                }
                else if (QoderCliEstimate.Compute(message.ContextUsageRatio, message.OutputChars)
                         is { } estimate)
                {
                    raw = estimate;
                    estimated = true;
                }
                else
                {
                    raw = new RawTokenCounts();
                }
                if (!CollectorSupport.ValidCounts(raw))
                {
                    unparsed++;
                    return;
                }
                var requestId = message.RequestId ?? entry.RequestId;
                var key = !string.IsNullOrEmpty(requestId)
                    ? "request:" + requestId
                    : !string.IsNullOrEmpty(message.Id)
                    ? "message:" + message.Id
                    : !string.IsNullOrEmpty(entry.Uuid)
                        ? "uuid:" + entry.Uuid
                        : "line:" + (current + 1).ToString(CultureInfo.InvariantCulture);
                var model = !string.IsNullOrEmpty(message.Model) ? message.Model : null;
                var sourceRef = "line=" + (current + 1).ToString(CultureInfo.InvariantCulture);
                var meta = new RequestMeta(key, model, null, entry.TimestampMs, entry.TimestampMs,
                    entry.IsSidechain ?? false, sourceRef);
                var record = QoderMessage(TokenSemanticsCatalog.QoderCli,
                    sessionId ?? fallbackId, meta, raw, current, estimated);
                // 同一请求可能多次输出或拆成多个消息块；已取得的实测值不能被后续占位覆盖。
                if (!candidates.TryGetValue(key, out var previous) ||
                    CliQuality(record) >= CliQuality(previous.Message))
                {
                    candidates[key] = new CliCandidate(current, record, raw);
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }

        if (!sawValid)
        {
            return new Parsed([], unparsed);
        }

        var id = sessionId ?? fallbackId;
        var ordered = candidates.Values.OrderBy(candidate => candidate.Seq).ToList();
        var messages = ordered.Select(candidate => candidate.Message).ToList();
        var counts = ordered.Select(candidate => candidate.Raw).ToList();

        long? start = null;
        long? end = null;
        foreach (var message in messages)
        {
            if (message.CreatedAtMs is { } created && (start is null || created < start))
            {
                start = created;
            }
            var completed = message.CompletedAtMs ?? message.CreatedAtMs;
            if (completed is { } value && (end is null || value > end))
            {
                end = value;
            }
        }

        // 开关前后的请求可以处在同一会话；历史估算不能混入该会话的实测总量。
        var mixed = messages.Any(message => message.SourceKind == SourceKind.Estimated) &&
                    messages.Any(message => message.SourceKind != SourceKind.Estimated);
        var sessionMessages = mixed
            ? messages.Select(message => message.SourceKind == SourceKind.Estimated
                ? message with { Usage = UsageNormalizer.Normalize(TokenSemanticsCatalog.QoderCli, default) }
                : message).ToList()
            : messages;
        var sessionCounts = mixed
            ? ordered.Select(candidate => candidate.Message.SourceKind == SourceKind.Estimated
                ? default : candidate.Raw).ToList()
            : counts;
        var session = RequestBuilder.SessionWithSemantics(Client, TokenSemanticsCatalog.QoderCli,
            new SessionMeta(id, null, project, start, end), sessionMessages, sessionCounts, unparsed > 0);
        if (messages.Count > 0 && messages.All(message => message.SourceKind == SourceKind.Estimated))
        {
            session = session with { SourceKind = SourceKind.Estimated };
        }
        return new Parsed([SessionImport.Create(session, messages, path)], unparsed);
    }

    private static int CliQuality(MessageRecord message) => message.Usage.TotalTokens is null
        ? 0 : message.SourceKind == SourceKind.Estimated ? 1 : 2;

    /// <summary>Qoder 原生输入直接来自 prompt_tokens，已包含读取及写入缓存。</summary>
    private static RawTokenCounts CliCounts(RawTokenCounts raw)
    {
        long? uncached = null;
        if (raw.Input is { } input && raw.CacheRead is { } read && raw.CacheWrite is { } write &&
            input >= read && input - read >= write)
        {
            uncached = input - read - write;
        }
        return raw with { Input = uncached };
    }

    private static bool TryReadCliEntry(string line, out CliEntry entry)
    {
        entry = default!;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (!TryOptionalString(root, out var kind, "type") ||
                !TryOptionalString(root, out var sessionId, "sessionId") ||
                !TryOptionalString(root, out var sdkSessionId, "session_id") ||
                !TryOptionalString(root, out var requestId, "request_id") ||
                !TryOptionalString(root, out var uuid, "uuid") ||
                !TryOptionalString(root, out var cwd, "cwd") ||
                !TryOptionalBoolean(root, out var isSidechain, "isSidechain") ||
                !TryOptionalTimestamp(root, out var timestamp, "timestamp") ||
                !TryOptionalMessage(root, out var message, "message"))
            {
                return false;
            }
            entry = new CliEntry(kind, sessionId ?? sdkSessionId, timestamp, uuid, isSidechain, cwd,
                requestId, message);
            return true;
        }
    }

    private static bool TryOptionalMessage(JsonElement element, out CliMessage? message, string name)
    {
        message = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }
        switch (property.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Object:
                if (!TryOptionalString(property, out var id, "id") ||
                    !TryOptionalString(property, out var model, "model") ||
                    !TryOptionalUsage(property, out var raw, "usage", out var hasUsage,
                        out var contextUsageRatio, out var requestId))
                {
                    return false;
                }
                message = new CliMessage(id, model, raw, hasUsage, contextUsageRatio,
                    raw is { } counts && !IsPlaceholderUsage(counts) ? 0 : OutputContentChars(property),
                    requestId);
                return true;
            default:
                return false;
        }
    }

    private static bool TryOptionalUsage(JsonElement element, out RawTokenCounts? raw, string name,
        out bool present, out double? contextUsageRatio, out string? requestId)
    {
        raw = null;
        present = false;
        contextUsageRatio = null;
        requestId = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }
        switch (property.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Object:
                present = true;
                if (!TryOptionalString(property, out requestId, "request_id") ||
                    !TryOptionalCount(property, out var input, "input_tokens", "inputTokens") ||
                    !TryOptionalCount(property, out var output, "output_tokens", "outputTokens") ||
                    !TryOptionalCount(property, out var cacheRead,
                        "cache_read_input_tokens", "cacheReadInputTokens") ||
                    !TryOptionalCount(property, out var cacheWrite,
                        "cache_creation_input_tokens", "cacheCreationInputTokens"))
                {
                    return false;
                }
                TryOptionalRatio(property, "context_usage_ratio", out contextUsageRatio);
                if (input is null && output is null && cacheRead is null && cacheWrite is null)
                {
                    return true;
                }
                raw = new RawTokenCounts(input, output, cacheRead, cacheWrite);
                return true;
            default:
                return false;
        }
    }

    private static void TryOptionalRatio(JsonElement element, string name, out double? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return;
        }
        if (property.TryGetDouble(out var ratio) && double.IsFinite(ratio) && ratio is > 0 and <= 1)
        {
            value = ratio;
        }
    }

    /// <summary>只统计数据长度用于 E5 输出估算，正文本身不离开解析过程。</summary>
    private static long OutputContentChars(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return 0;
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()?.Length ?? 0;
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        long total = 0;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (var field in new[] { "text", "thinking" })
            {
                if (block.TryGetProperty(field, out var text) && text.ValueKind == JsonValueKind.String)
                {
                    total += text.GetString()?.Length ?? 0;
                }
            }
            if (block.TryGetProperty("input", out var input) &&
                input.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                total += input.GetRawText().Length;
            }
        }
        return total;
    }

    private static bool TryOptionalTimestamp(JsonElement element, out long? millis, string name)
    {
        millis = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }
        switch (property.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Number:
                if (!property.TryGetInt64(out var number))
                {
                    return false;
                }
                millis = number >= 0 ? number : null;
                return true;
            case JsonValueKind.String:
                millis = ParseIso8601UtcMs(property.GetString());
                return true;
            default:
                return false;
        }
    }

    private static bool TryOptionalBoolean(JsonElement element, out bool? value, string name)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }
        switch (property.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ IDE

    private static Parsed ParseIde(SqliteConnection connection, string sourcePath)
    {
        var unparsed = 0;
        var sessions = new SortedDictionary<string, IdeSession>(StringComparer.Ordinal);
        if (TableExists(connection, "chat_session"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT session_id, project_uri, project_name, gmt_create, gmt_modified,
                       preferred_model_info
                FROM chat_session ORDER BY session_id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!TryRequiredString(reader, 0, out var id) ||
                    !TryOptionalString(reader, 1, out var uri) ||
                    !TryOptionalString(reader, 2, out var name) ||
                    !TryOptionalInt64(reader, 3, out var start) ||
                    !TryOptionalInt64(reader, 4, out var end) ||
                    !TryOptionalString(reader, 5, out var preferred))
                {
                    throw InvalidDatabase("invalid chat_session row");
                }
                if (id.Length == 0)
                {
                    unparsed++;
                    continue;
                }
                sessions[id] = new IdeSession(
                    !string.IsNullOrEmpty(uri) ? uri : name,
                    start,
                    end,
                    ModelFromJson(preferred));
            }
        }

        if (!TableExists(connection, "chat_message"))
        {
            return new Parsed([], unparsed);
        }

        var details = new SortedDictionary<string, List<IdeCandidate>>(StringComparer.Ordinal);
        var partialSessions = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, session_id, role, token_info, model_info, gmt_create, rowid
                FROM chat_message ORDER BY rowid
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!TryOptionalString(reader, 0, out var id) ||
                    !TryRequiredString(reader, 1, out var sessionId) ||
                    !TryOptionalString(reader, 2, out var role) ||
                    !TryOptionalString(reader, 3, out var tokenInfo) ||
                    !TryOptionalString(reader, 4, out var modelInfo) ||
                    !TryOptionalInt64(reader, 5, out var timeMs) ||
                    !TryInt64(reader, 6, out var rowid))
                {
                    throw InvalidDatabase("invalid chat_message row");
                }
                if (!string.Equals(role, "assistant", StringComparison.Ordinal))
                {
                    continue;
                }
                if (tokenInfo is null || tokenInfo.Trim().Length == 0)
                {
                    continue;
                }
                if (!TryTokenInfo(tokenInfo, out var raw) || !CollectorSupport.ValidCounts(raw))
                {
                    unparsed++;
                    partialSessions.Add(sessionId);
                    continue;
                }
                if (!sessions.TryGetValue(sessionId, out var meta))
                {
                    meta = new IdeSession(null, null, null, null);
                    sessions.Add(sessionId, meta);
                }
                var model = ModelFromJson(modelInfo) ?? meta.PreferredModel;
                var key = !string.IsNullOrEmpty(id)
                    ? "message:" + id
                    : "rowid:" + rowid.ToString(CultureInfo.InvariantCulture);
                var sourceRef = "chat_message_rowid=" + rowid.ToString(CultureInfo.InvariantCulture);
                var request = new RequestMeta(key, model, null, timeMs, timeMs, false, sourceRef);
                var message = QoderMessage(TokenSemanticsCatalog.QoderIde, sessionId, request, raw, rowid);
                if (!details.TryGetValue(sessionId, out var list))
                {
                    list = [];
                    details.Add(sessionId, list);
                }
                list.Add(new IdeCandidate(rowid, message, raw));
            }
        }

        var imports = new List<SessionImport>(sessions.Count);
        foreach (var (id, meta) in sessions)
        {
            List<IdeCandidate> ordered = details.TryGetValue(id, out var candidates)
                ? candidates.OrderBy(candidate => candidate.Seq).ToList()
                : [];
            var messages = ordered.Select(candidate => candidate.Message).ToList();
            var counts = ordered.Select(candidate => candidate.Raw).ToList();

            long? start = meta.StartMs;
            if (start is null)
            {
                foreach (var message in messages)
                {
                    if (message.CreatedAtMs is { } created && (start is null || created < start))
                    {
                        start = created;
                    }
                }
            }
            long? end = meta.EndMs;
            foreach (var message in messages)
            {
                var value = message.CompletedAtMs ?? message.CreatedAtMs;
                if (value is { } candidate && (end is null || candidate > end))
                {
                    end = candidate;
                }
            }

            var session = RequestBuilder.SessionWithSemantics(Client, TokenSemanticsCatalog.QoderIde,
                new SessionMeta(id, null, meta.Project, start, end), messages, counts,
                partialSessions.Contains(id));
            imports.Add(SessionImport.Create(session, messages, sourcePath));
        }
        return new Parsed(imports, unparsed);
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static bool TryTokenInfo(string tokenInfo, out RawTokenCounts raw)
    {
        raw = default;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(tokenInfo);
        }
        catch (JsonException)
        {
            return false;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (!TryOptionalCount(root, out var prompt, "prompt_tokens", "promptTokens") ||
                !TryOptionalCount(root, out var completion, "completion_tokens", "completionTokens") ||
                !TryOptionalCount(root, out var cached, "cached_tokens", "cachedTokens"))
            {
                return false;
            }
            if (prompt is null && completion is null && cached is null)
            {
                return false;
            }
            raw = new RawTokenCounts(prompt, completion, cached);
            return true;
        }
    }

    /// <summary>
    /// `model_info` / `preferred_model_info` 通常是 JSON 对象，也允许直接是模型名。
    /// JSON 解析失败且值不以 `{` 开头时按原值使用。
    /// </summary>
    private static string? ModelFromJson(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        string? model = null;
        var valid = true;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryOptionalString(root, out var key, "model_key") &&
                    TryOptionalString(root, out var name, "model"))
                {
                    model = key ?? name;
                }
                else
                {
                    valid = false;
                }
            }
        }
        catch (JsonException)
        {
            valid = false;
        }

        if (valid && !string.IsNullOrEmpty(model))
        {
            return model;
        }
        return trimmed.StartsWith('{') ? null : trimmed;
    }

    private static bool TryOptionalCount(JsonElement element, out long? value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }
            switch (property.ValueKind)
            {
                case JsonValueKind.Null:
                    value = null;
                    return true;
                case JsonValueKind.Number:
                    if (!property.TryGetUInt64(out var number) || number > long.MaxValue)
                    {
                        value = null;
                        return false;
                    }
                    value = (long)number;
                    return true;
                default:
                    value = null;
                    return false;
            }
        }
        value = null;
        return true;
    }

    private static bool TryOptionalString(JsonElement element, out string? value, string name)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }
        switch (property.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                value = property.GetString();
                return true;
            default:
                return false;
        }
    }

    private static MessageRecord QoderMessage(TokenSemantics semantics, string sessionId,
        RequestMeta meta, RawTokenCounts raw, long seq, bool estimated = false)
    {
        var message = RequestBuilder.MessageWithSemantics(Client, semantics, sessionId, meta, raw);
        return message with
        {
            Seq = seq,
            SourceKind = estimated ? SourceKind.Estimated : SourceKind.Measured,
        };
    }

    /// <summary>已提供的 token 字段全部为 0 时是客户端占位符，不是实测零用量。</summary>
    private static bool IsPlaceholderUsage(RawTokenCounts raw)
    {
        var any = false;
        foreach (var value in new[]
                 {
                     raw.Input, raw.Output, raw.CacheRead, raw.CacheWrite, raw.Reasoning, raw.ClientTotal,
                 })
        {
            if (value is not { } number)
            {
                continue;
            }
            any = true;
            if (number != 0)
            {
                return false;
            }
        }
        return any;
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

    private static bool TryOptionalString(SqliteDataReader reader, int index, out string? value)
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

    private static bool TryOptionalInt64(SqliteDataReader reader, int index, out long? value)
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

    private static bool TryInt64(SqliteDataReader reader, int index, out long value)
    {
        if (reader.GetValue(index) is long number)
        {
            value = number;
            return true;
        }
        value = 0;
        return false;
    }

    private static SyncException InvalidDatabase(string message) =>
        SyncException.Database(new InvalidDataException(message));

    /// <summary>解析 ISO 8601 UTC 时间至毫秒：只接受 UTC `Z` 形式，毫秒最多三位。</summary>
    private static long? ParseIso8601UtcMs(string? value)
    {
        if (value is null || value.Length < 20 || value[4] != '-' || value[7] != '-' ||
            value[10] != 'T' || value[13] != ':' || value[16] != ':' || value[^1] != 'Z')
        {
            return null;
        }
        if (!TryParseNumber(value, 0, 4, out var year) ||
            !TryParseNumber(value, 5, 2, out var month) ||
            !TryParseNumber(value, 8, 2, out var day) ||
            !TryParseNumber(value, 11, 2, out var hour) ||
            !TryParseNumber(value, 14, 2, out var minute) ||
            !TryParseNumber(value, 17, 2, out var second))
        {
            return null;
        }
        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 60)
        {
            return null;
        }
        long millis = 0;
        if (value.Length > 20)
        {
            if (value[19] != '.')
            {
                return null;
            }
            var fractionLength = value.Length - 21;
            if (fractionLength is < 1 or > 3)
            {
                return null;
            }
            var fraction = value.AsSpan(20, fractionLength);
            foreach (var character in fraction)
            {
                if (character is < '0' or > '9')
                {
                    return null;
                }
            }
            var digits = fraction.ToString().PadRight(3, '0');
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out millis))
            {
                return null;
            }
        }
        var days = DaysFromCivil(year, month, day);
        return ((days * 86_400 + hour * 3_600 + minute * 60 + second) * 1_000) + millis;
    }

    private static bool TryParseNumber(string value, int start, int length, out long number) =>
        long.TryParse(value.AsSpan(start, length), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out number);

    private static long DaysFromCivil(long year, long month, long day)
    {
        year -= month <= 2 ? 1 : 0;
        var era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - era * 400;
        var dayOfYear = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
        var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        return era * 146_097 + dayOfEra - 719_468;
    }

    private sealed record Input(string Path, SourceFileKind Kind);

    private sealed record Draft(SessionImport Import, bool FromIde);

    private sealed record Parsed(List<SessionImport> Imports, int Unparsed);

    private sealed record CliEntry(
        string? Kind,
        string? SessionId,
        long? TimestampMs,
        string? Uuid,
        bool? IsSidechain,
        string? Cwd,
        string? RequestId,
        CliMessage? Message);

    private sealed record CliMessage(string? Id, string? Model, RawTokenCounts? Raw, bool HasUsage,
        double? ContextUsageRatio, long OutputChars, string? RequestId);

    private sealed record CliCandidate(int Seq, MessageRecord Message, RawTokenCounts Raw);

    private sealed record IdeSession(
        string? Project,
        long? StartMs,
        long? EndMs,
        string? PreferredModel);

    private sealed record IdeCandidate(long Seq, MessageRecord Message, RawTokenCounts Raw);
}
