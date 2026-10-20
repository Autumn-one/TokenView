using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>
/// GitHub Copilot CLI（session-store.db）与 VS Code Agent Host（session.db）只读元数据采集器。
/// 两个来源指向同一逻辑会话时以 Host 的 turn 汇总为准，绝不重复累加。
/// </summary>
public sealed class CopilotCollector : IClientCollector
{
    private const string Client = "copilot";
    private const int ParserVersion = 2;
    private const string AgentHostSemanticsId = "copilot.agent_host.turn.v1";
    private const string AgentHostCacheRelation = "copilot.agent_host.cache_relation_unverified";
    private const string AgentHostTurnsMissing = "copilot.agent_host.turns_missing";
    private const string CopilotCachesExceedInput = "anomaly.copilot_caches_gt_input";
    private const string CopilotInputSplitMissing = "copilot.input_split_missing_caches";

    private readonly string? _database;
    private readonly string? _agentHost;

    public CopilotCollector() : this(DefaultDatabase(), DefaultAgentHost())
    {
    }

    public CopilotCollector(string? database) : this(database, null)
    {
    }

    public CopilotCollector(string? database, string? agentHost)
    {
        _database = database;
        _agentHost = agentHost;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths
    {
        get
        {
            var paths = new List<string>();
            if (_database is not null)
            {
                paths.Add(_database);
                if (StateRoot() is { } state) paths.Add(state);
            }
            if (_agentHost is not null) paths.Add(_agentHost);
            return paths;
        }
    }

    public static string? DefaultDatabase()
    {
        var configured = CollectorSupport.EnvPath("COPILOT_HOME");
        var root = configured ?? CollectorSupport.Combine(CollectorSupport.HomeDir(), ".copilot");
        return CollectorSupport.Combine(root, "session-store.db");
    }

    public static string? DefaultAgentHost()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return appData is null ? null : Path.Combine(appData, "Code", "agentSessionData");
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

        var databasePresent = Exists(database);
        var hostMissing = _agentHost is not null && !Exists(_agentHost);
        var stateRoot = StateRoot();
        var eventPaths = stateRoot is null ? [] : Children(stateRoot, "events.jsonl");
        var hostPaths = _agentHost is null ? [] : Children(_agentHost, "session.db");

        var observed = new Dictionary<string, SqliteSignature>(StringComparer.Ordinal);
        foreach (var path in eventPaths.Concat(hostPaths).Concat(databasePresent ? [database] : []))
        {
            observed[path] = Signature(path);
        }

        var sources = new List<SourceFile>();
        var sessions = new SortedDictionary<string, SessionImport>(StringComparer.Ordinal);
        var unparsed = 0;
        if (databasePresent)
        {
            Snapshot snapshot;
            try
            {
                using var connection = SqliteSnapshot.Open(database);
                snapshot = ReadSnapshot(connection.Connection, database);
            }
            catch (SyncException)
            {
                throw;
            }
            catch (SqliteException error)
            {
                throw SyncException.Database(error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw SyncException.FromSource(error);
            }
            catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
            {
                throw SyncException.Database(error);
            }

            sources.Add(NewSource(database, SourceFileKind.Sqlite, snapshot.Unparsed));
            unparsed += snapshot.Unparsed;
            foreach (var entry in snapshot.Sessions)
            {
                sessions[entry.Session.ClientSessionId] = entry;
            }
        }

        var sdk = new SortedDictionary<string, SdkSession>(StringComparer.Ordinal);
        foreach (var path in eventPaths)
        {
            var entry = ReadEvents(path);
            var id = entry.Import.Session.ClientSessionId;
            if (sdk.ContainsKey(id)) throw SyncException.ChangedDuringScan();
            sdk[id] = entry;
            sources.Add(NewSource(path, SourceFileKind.Jsonl, 0));
        }

        var turns = new List<Turn>();
        foreach (var path in hostPaths)
        {
            var found = ReadHost(path);
            if (found.Count > 0) sources.Add(NewSource(path, SourceFileKind.Sqlite, 0));
            turns.AddRange(found);
        }

        var host = GroupTurns(DedupTurns(turns));
        foreach (var (id, entry) in sdk)
        {
            if (host.Remove(id, out var linked))
            {
                var complete = linked.Count == entry.UserEvents.Count &&
                               linked.All(turn => entry.UserEvents.Contains(turn.EventId));
                var totals = linked.SelectMany(turn => turn.Totals).ToList();
                var session = AgentHostTotals(entry.Import.Session, totals, complete);
                var messages = entry.Import.Messages;
                var messagePaths = entry.Import.MessageSourcePaths;
                foreach (var turn in linked)
                {
                    if (!entry.UserEvents.Contains(turn.EventId))
                    {
                        throw SyncException.ChangedDuringScan();
                    }
                    var index = messages.FindIndex(message =>
                        string.Equals(message.DedupKey, $"api:{turn.ApiId}", StringComparison.Ordinal));
                    if (index < 0) continue;
                    var message = messages[index] with { Usage = AgentHostUsage(turn.LastCall) };
                    messages[index] = message;
                    messagePaths[message.DedupKey] = turn.SourcePath;
                }
                sessions[id] = new SessionImport(session, messages, entry.Import.SourcePath, messagePaths);
            }
            else if ((!databasePresent || hostMissing) && store.SessionExists(Client, id))
            {
                // 源暂时不可用不是"归零"修正：保留上次导入的会话及其原生账本。
                sessions.Remove(id);
            }
            else if (!sessions.TryGetValue(id, out var existing) || existing.Messages.Count == 0)
            {
                sessions[id] = entry.Import;
            }
        }

        // 没有 SDK 事件时，Host 无法提供可靠的调用标识与时间：保留历史而不是伪造请求。
        if (host.Count > 0) throw SyncException.ChangedDuringScan();

        if (sources.Count == 0)
        {
            report.RootMissing = true;
            return report;
        }

        if (Exists(database) != databasePresent ||
            !(stateRoot is null ? [] : Children(stateRoot, "events.jsonl"))
                .SequenceEqual(eventPaths, StringComparer.Ordinal) ||
            !(_agentHost is null ? [] : Children(_agentHost, "session.db"))
                .SequenceEqual(hostPaths, StringComparer.Ordinal))
        {
            throw SyncException.ChangedDuringScan();
        }
        foreach (var (path, stamp) in observed)
        {
            if (Signature(path) != stamp) throw SyncException.ChangedDuringScan();
        }

        ImportSummary imported;
        try
        {
            var ordered = sessions.Values.ToList();
            imported = !databasePresent || hostMissing
                ? store.MergeClientSnapshot(Client, sources, ordered)
                : store.ReplaceClientSnapshot(Client, sources, ordered);
        }
        catch (Exception error) when (error is SqliteException or ArgumentException or InvalidOperationException)
        {
            throw SyncException.Store(error);
        }

        report.FilesSeen = sources.Count;
        report.FilesParsed = sources.Count;
        report.UnparsedLines = unparsed;
        report.SessionsImported = sessions.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private string? StateRoot()
    {
        if (_database is null) return null;
        var parent = Path.GetDirectoryName(_database);
        return parent is null ? null : Path.Combine(parent, "session-state");
    }

    private static SourceFile NewSource(string path, SourceFileKind kind, long unparsed)
    {
        var (size, mtime) = Fingerprint(path);
        return CollectorSupport.NewSourceFile(Client, path, kind, size, mtime, 0, ParserVersion, unparsed);
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw SyncException.FromSource(error);
        }
    }

    /// <summary>只枚举 root 的直接子目录中的指定文件，结果按序数排序。</summary>
    private static List<string> Children(string root, string filename)
    {
        if (!Exists(root)) return [];
        if (File.Exists(root))
        {
            throw SyncException.Directory(new IOException("Copilot source root is not a directory"));
        }

        var files = new List<string>();
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.Directory(error);
        }

        foreach (var entry in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw SyncException.FromSource(error);
            }
            if (!attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var path = Path.Combine(entry, filename);
            if (!Exists(path)) continue;
            FileAttributes fileAttributes;
            try
            {
                fileAttributes = File.GetAttributes(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw SyncException.FromSource(error);
            }
            if (fileAttributes.HasFlag(FileAttributes.Directory) ||
                fileAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }
            files.Add(path);
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static Snapshot ReadSnapshot(SqliteConnection connection, string path)
    {
        var sessions = new SortedDictionary<string, SessionMeta>(StringComparer.Ordinal);
        var unparsed = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,cwd,created_at,updated_at FROM sessions ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var meta = new SessionMeta(
                    RequiredString(reader, 0),
                    null,
                    OptionalString(reader, 1),
                    Time(OptionalString(reader, 2)),
                    Time(OptionalString(reader, 3)));
                if (meta.Id.Length == 0)
                {
                    unparsed++;
                    continue;
                }
                sessions[meta.Id] = meta;
            }
        }

        var hasUsage = HasUsageTable(connection);
        var details = new SortedDictionary<string, List<(MessageRecord Message, RawTokenCounts Raw)>>(
            StringComparer.Ordinal);
        var partial = new HashSet<string>(StringComparer.Ordinal);
        if (hasUsage)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id,session_id,agent_id,parent_tool_call_id,model,
                       input_tokens,output_tokens,cache_read_tokens,cache_write_tokens,
                       reasoning_tokens,created_at
                FROM assistant_usage_events ORDER BY id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sessionId = RequiredString(reader, 1);
                long id;
                string? agent;
                string? parentTool;
                string? model;
                RawTokenCounts raw;
                long? createdMs;
                try
                {
                    id = reader.GetInt64(0);
                    agent = OptionalString(reader, 2);
                    parentTool = OptionalString(reader, 3);
                    model = OptionalString(reader, 4);
                    raw = new RawTokenCounts(
                        Count(reader, 5),
                        Count(reader, 6),
                        Count(reader, 7),
                        Count(reader, 8),
                        Count(reader, 9));
                    createdMs = Time(OptionalString(reader, 10));
                }
                catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
                {
                    unparsed++;
                    partial.Add(sessionId);
                    continue;
                }

                if (!sessions.ContainsKey(sessionId))
                {
                    unparsed++;
                    partial.Add(sessionId);
                    continue;
                }

                var (message, rawCounts) = CopilotRequest(sessionId, new RequestMeta(
                    $"usage:{id}",
                    model,
                    agent,
                    createdMs,
                    null,
                    agent is not null || parentTool is not null,
                    $"assistant_usage_events.id={id}"), raw);
                if (!details.TryGetValue(sessionId, out var list))
                {
                    list = [];
                    details[sessionId] = list;
                }
                list.Add((message, rawCounts));
            }
        }

        var imports = new List<SessionImport>(sessions.Count);
        foreach (var (id, meta) in sessions)
        {
            var found = details.Remove(id, out var list) ? list : [];
            var messages = new List<MessageRecord>(found.Count);
            var counts = new List<RawTokenCounts>(found.Count);
            foreach (var (message, raw) in found)
            {
                messages.Add(message);
                counts.Add(raw);
            }
            imports.Add(new SessionImport(
                CopilotSession(meta, messages, counts, !hasUsage || partial.Contains(id)),
                messages, path, new Dictionary<string, string>(StringComparer.Ordinal)));
        }
        return new Snapshot(imports, unparsed);
    }

    private static bool HasUsageTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='assistant_usage_events'";
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    private static (MessageRecord Message, RawTokenCounts Raw) CopilotRequest(
        string sessionId, RequestMeta meta, RawTokenCounts native)
    {
        ulong? caches = null;
        if (native.CacheRead is { } read && native.CacheWrite is { } write)
        {
            caches = (ulong)read + (ulong)write;
        }

        long? input = null;
        if (native.Input is { } nativeInput && caches is { } cacheSum && (ulong)nativeInput >= cacheSum)
        {
            input = nativeInput - (long)cacheSum;
        }

        long? total = null;
        if (native.Input is { } totalInput && native.Output is { } totalOutput)
        {
            var sum = (ulong)totalInput + (ulong)totalOutput;
            if (sum <= long.MaxValue) total = (long)sum;
        }

        var raw = new RawTokenCounts(input, native.Output, native.CacheRead, native.CacheWrite,
            native.Reasoning, total);
        var message = RequestBuilder.Request(Client, sessionId, meta, raw);
        var unknown = message.Usage.UnknownRelations.ToList();
        if (total is null &&
            !unknown.Contains(TokenSemanticsCatalog.UnknownTotalIncomplete, StringComparer.Ordinal))
        {
            unknown.Add(TokenSemanticsCatalog.UnknownTotalIncomplete);
        }
        if (native.Input is { } anomalyInput && caches is { } anomalyCaches &&
            anomalyCaches > (ulong)anomalyInput)
        {
            unknown.Add(CopilotCachesExceedInput);
        }
        if (native.Input is not null && caches is null)
        {
            unknown.Add(CopilotInputSplitMissing);
        }
        message = message with
        {
            Usage = message.Usage with
            {
                TotalTokens = total,
                TotalBasis = total is null ? UsageTotalBasis.Unavailable : UsageTotalBasis.DerivedFromBuckets,
                UnknownRelations = unknown,
            },
            SourceKind = total is null ? SourceKind.Measured : SourceKind.Derived,
        };
        return (message, raw);
    }

    private static SessionRecord CopilotSession(SessionMeta meta, List<MessageRecord> messages,
        List<RawTokenCounts> counts, bool partial)
    {
        var session = RequestBuilder.Session(Client, meta, messages, counts, partial);
        var unknown = session.Usage.UnknownRelations.ToList();
        foreach (var message in messages)
        {
            unknown.AddRange(message.Usage.UnknownRelations);
        }
        unknown.Sort(StringComparer.Ordinal);
        var deduped = new List<string>(unknown.Count);
        foreach (var relation in unknown)
        {
            if (deduped.Count == 0 || !string.Equals(deduped[^1], relation, StringComparison.Ordinal))
            {
                deduped.Add(relation);
            }
        }
        return session with { Usage = session.Usage with { UnknownRelations = deduped } };
    }

    private static NormalizedUsage AgentHostUsage(RawTokenCounts raw)
    {
        var unknown = new List<string> { "reasoning.missing", "cache_write.missing" };
        long? total = null;
        if (raw.CacheRead == 0 && raw.Input is { } input && raw.Output is { } output)
        {
            var sum = (ulong)input + (ulong)output;
            if (sum <= long.MaxValue) total = (long)sum;
        }
        if (raw.CacheRead != 0) unknown.Add(AgentHostCacheRelation);
        if (total is null) unknown.Add(TokenSemanticsCatalog.UnknownTotalIncomplete);

        return new NormalizedUsage(
            new TokenBuckets(
                raw.CacheRead == 0 ? raw.Input : null,
                raw.Output,
                null,
                raw.CacheRead,
                null),
            total,
            total is null ? UsageTotalBasis.Unavailable : UsageTotalBasis.DerivedFromBuckets,
            unknown,
            false);
    }

    private static MessageRecord AgentHostRequest(string sessionId, RequestMeta meta, RawTokenCounts raw)
    {
        var message = RequestBuilder.Request(Client, sessionId, meta, default);
        return message with
        {
            Usage = AgentHostUsage(raw),
            SemanticsId = AgentHostSemanticsId,
            SourceKind = SourceKind.Derived,
        };
    }

    private static SessionRecord AgentHostTotals(SessionRecord session, List<RawTokenCounts> turns,
        bool complete)
    {
        var usage = AgentHostUsage(RequestBuilder.SumCompleteCounts(turns));
        if (!complete)
        {
            var unknown = usage.UnknownRelations.ToList();
            unknown.Add(AgentHostTurnsMissing);
            usage = usage with
            {
                TotalTokens = null,
                TotalBasis = UsageTotalBasis.Unavailable,
                UnknownRelations = unknown,
            };
        }
        return session with
        {
            Usage = usage,
            SemanticsId = AgentHostSemanticsId,
            TotalBasis = SessionTotalBasis.ClientReported,
            Coverage = MessageCoverage.Partial,
            SourceKind = SourceKind.Derived,
        };
    }

    private static SdkSession ReadEvents(string path)
    {
        var before = Fingerprint(path);
        SessionMeta? meta = null;
        long? end = null;
        string? model = null;
        var requests = new SortedDictionary<string, RequestMeta>(StringComparer.Ordinal);
        var users = new SortedSet<string>(StringComparer.Ordinal);

        try
        {
            CollectorSupport.ReadJsonLines(path, (line, complete) =>
            {
                if (!complete) throw SyncException.ChangedDuringScan();
                if (IsAsciiWhitespace(line)) return;

                var parsed = ParseEvent(line);
                var time = ParseIso8601UtcMs(parsed.Timestamp);
                if (time is { } timestamp && (end is null || timestamp > end)) end = timestamp;

                switch (parsed.Kind)
                {
                    case "session.start":
                        var sessionId = parsed.Data.SessionId;
                        if (string.IsNullOrEmpty(sessionId)) throw InvalidSource();
                        if (meta is not null) throw InvalidSource();
                        model = parsed.Data.SelectedModel;
                        meta = new SessionMeta(sessionId, null, parsed.Data.Cwd,
                            ParseIso8601UtcMs(parsed.Data.StartTime) ?? time, null);
                        break;
                    case "user.message":
                        var userId = parsed.Id;
                        if (string.IsNullOrEmpty(userId)) throw InvalidSource();
                        users.Add(userId);
                        break;
                    case "session.model_change":
                        model = parsed.Data.NewModel ?? model;
                        break;
                    case "assistant.message":
                        var api = parsed.Data.ApiCallId;
                        if (!string.IsNullOrEmpty(api))
                        {
                            requests[api] = new RequestMeta(
                                $"api:{api}",
                                parsed.Data.Model ?? model,
                                null,
                                time,
                                null,
                                false,
                                $"assistant.message.apiCallId={api}");
                        }
                        break;
                }
            });
        }
        catch (SyncException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }

        if (Fingerprint(path) != before) throw SyncException.ChangedDuringScan();
        if (meta is null) throw InvalidSource();

        meta = meta with { EndMs = end };
        var messages = requests.Values
            .Select(request => AgentHostRequest(meta.Id, request, default))
            .ToList();
        var session = CopilotSession(meta, messages, [], true);
        session = session with { Model = model ?? session.Model, SemanticsId = AgentHostSemanticsId };
        return new SdkSession(
            new SessionImport(session, messages, path,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            users);
    }

    private static List<Turn> ReadHost(string path)
    {
        SqliteSnapshot snapshot;
        try
        {
            snapshot = SqliteSnapshot.Open(path);
        }
        catch (SyncException)
        {
            throw;
        }
        catch (SqliteException error)
        {
            throw SyncException.Database(error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }

        using (snapshot)
        {
            try
            {
                return ReadHostTurns(snapshot.Connection, path);
            }
            catch (SyncException)
            {
                throw;
            }
            catch (SqliteException error)
            {
                throw SyncException.Database(error);
            }
            catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
            {
                throw SyncException.Database(error);
            }
        }
    }

    private static List<Turn> ReadHostTurns(SqliteConnection connection, string path)
    {
        string? sdk = null;
        var primary = false;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT key,value FROM session_metadata
                WHERE key IN ('defaultChatProviderData','agentHost.chatProviderData')
                ORDER BY key DESC
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = RequiredString(reader, 0);
                var value = RequiredString(reader, 1);
                if (TryParseProvider(value, out var id) && !string.IsNullOrEmpty(id))
                {
                    sdk = id;
                    primary = string.Equals(key, "defaultChatProviderData", StringComparison.Ordinal);
                    break;
                }
            }
        }
        if (sdk is null) return [];

        var result = new List<Turn>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.event_id,u.usage FROM turn_usage u
                JOIN turns t ON t.id=u.turn_id ORDER BY t.id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var eventId = OptionalString(reader, 0);
                if (string.IsNullOrEmpty(eventId)) throw InvalidSource();
                var value = RequiredString(reader, 1);
                var usage = ParseHostUsage(value);
                var call = usage.Call ?? throw InvalidSource();
                if (call.SchemaVersion != 1 ||
                    !string.Equals(call.SdkSessionId, sdk, StringComparison.Ordinal) ||
                    call.ApiCallId.Length == 0)
                {
                    throw InvalidSource();
                }
                if (usage.Totals is null || usage.Totals.Count == 0) throw InvalidSource();

                result.Add(new Turn(
                    sdk,
                    eventId,
                    call.ApiCallId,
                    Counts(usage.Input, usage.Output, usage.CacheRead),
                    usage.Totals
                        .Select(total => Counts(total.Input, total.Output, total.Cached))
                        .ToList(),
                    path,
                    primary));
            }
        }
        return result;
    }

    private static RawTokenCounts Counts(ulong? input, ulong? output, ulong? cache)
    {
        if (new[] { input, output, cache }.Any(value => value > long.MaxValue))
        {
            throw InvalidSource();
        }
        return new RawTokenCounts(
            input is { } i ? (long)i : null,
            output is { } o ? (long)o : null,
            cache is { } c ? (long)c : null);
    }

    private static List<Turn> DedupTurns(List<Turn> turns)
    {
        var chosen = new SortedDictionary<(string Sdk, string Event), Turn>(TurnComparer.Instance);
        foreach (var turn in turns)
        {
            var key = (turn.SdkId, turn.EventId);
            if (chosen.TryGetValue(key, out var old))
            {
                if (old.Primary && !turn.Primary) continue;
                if (old.Primary == turn.Primary)
                {
                    if (!string.Equals(old.ApiId, turn.ApiId, StringComparison.Ordinal) ||
                        old.LastCall != turn.LastCall ||
                        !old.Totals.SequenceEqual(turn.Totals))
                    {
                        throw SyncException.ChangedDuringScan();
                    }
                    continue;
                }
            }
            chosen[key] = turn;
        }
        return chosen.Values.ToList();
    }

    private static SortedDictionary<string, List<Turn>> GroupTurns(List<Turn> turns)
    {
        var host = new SortedDictionary<string, List<Turn>>(StringComparer.Ordinal);
        foreach (var turn in turns)
        {
            if (!host.TryGetValue(turn.SdkId, out var list))
            {
                list = [];
                host[turn.SdkId] = list;
            }
            list.Add(turn);
        }
        return host;
    }

    private static EventInfo ParseEvent(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            throw InvalidSource();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryRequiredString(root, "type", out var kind) ||
                !TryOptionalString(root, "id", out var id) ||
                !TryOptionalString(root, "timestamp", out var timestamp))
            {
                throw InvalidSource();
            }

            string? sessionId = null;
            string? startTime = null;
            string? selectedModel = null;
            string? newModel = null;
            string? model = null;
            string? apiCallId = null;
            string? cwd = null;
            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Null || data.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidSource();
                }
                if (!TryOptionalString(data, "sessionId", out sessionId) ||
                    !TryOptionalString(data, "startTime", out startTime) ||
                    !TryOptionalString(data, "selectedModel", out selectedModel) ||
                    !TryOptionalString(data, "newModel", out newModel) ||
                    !TryOptionalString(data, "model", out model) ||
                    !TryOptionalString(data, "apiCallId", out apiCallId))
                {
                    throw InvalidSource();
                }
                if (data.TryGetProperty("context", out var context))
                {
                    if (context.ValueKind == JsonValueKind.Null)
                    {
                        // context 为 null 等同于缺失。
                    }
                    else if (context.ValueKind != JsonValueKind.Object ||
                             !TryOptionalString(context, "cwd", out cwd))
                    {
                        throw InvalidSource();
                    }
                }
            }

            return new EventInfo(kind, id, timestamp,
                new EventData(sessionId, startTime, selectedModel, newModel, model, apiCallId, cwd));
        }
    }

    private static HostUsage ParseHostUsage(string value)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            throw InvalidSource();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryOptionalU64(root, "inputTokens", out var input) ||
                !TryOptionalU64(root, "outputTokens", out var output) ||
                !TryOptionalU64(root, "cacheReadTokens", out var cacheRead))
            {
                throw InvalidSource();
            }

            List<HostTotal>? totals = null;
            HostCall? call = null;
            if (root.TryGetProperty("_meta", out var meta))
            {
                if (meta.ValueKind == JsonValueKind.Null || meta.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidSource();
                }
                if (meta.TryGetProperty("turnTokenTotals", out var totalsElement))
                {
                    if (totalsElement.ValueKind == JsonValueKind.Null)
                    {
                        // null 等同于缺失；随后由 required 检查判定。
                    }
                    else if (totalsElement.ValueKind != JsonValueKind.Array)
                    {
                        throw InvalidSource();
                    }
                    else
                    {
                        totals = [];
                        foreach (var item in totalsElement.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object ||
                                !TryOptionalU64(item, "inputTokens", out var totalInput) ||
                                !TryOptionalU64(item, "outputTokens", out var totalOutput) ||
                                !TryOptionalU64(item, "cachedTokens", out var cached))
                            {
                                throw InvalidSource();
                            }
                            totals.Add(new HostTotal(totalInput, totalOutput, cached));
                        }
                    }
                }
                if (meta.TryGetProperty("vscode.modelCall", out var callElement))
                {
                    if (callElement.ValueKind == JsonValueKind.Null)
                    {
                        // null 等同于缺失；随后由 required 检查判定。
                    }
                    else if (callElement.ValueKind != JsonValueKind.Object ||
                             !TryRequiredU64(callElement, "schemaVersion", out var schemaVersion) ||
                             !TryRequiredString(callElement, "sdkSessionId", out var sdkSessionId) ||
                             !TryRequiredString(callElement, "apiCallId", out var apiCallId))
                    {
                        throw InvalidSource();
                    }
                    else
                    {
                        call = new HostCall(schemaVersion, sdkSessionId, apiCallId);
                    }
                }
            }
            return new HostUsage(input, output, cacheRead, call, totals);
        }
    }

    private static bool TryParseProvider(string value, out string? sdkSessionId)
    {
        sdkSessionId = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            return false;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryRequiredString(root, "sdkSessionId", out var id))
            {
                return false;
            }
            sdkSessionId = id;
            return true;
        }
    }

    private static long? Count(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        var value = reader.GetInt64(index);
        if (value < 0) throw new InvalidCastException("negative token count");
        return value;
    }

    private static string RequiredString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? throw new InvalidCastException("unexpected NULL") : reader.GetString(index);

    private static string? OptionalString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static bool TryRequiredString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryOptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return true;
    }

    private static bool TryOptionalU64(JsonElement element, string name, out ulong? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetUInt64(out var number))
        {
            return false;
        }
        value = number;
        return true;
    }

    private static bool TryRequiredU64(JsonElement element, string name, out ulong value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetUInt64(out var number))
        {
            return false;
        }
        value = number;
        return true;
    }

    private static bool IsAsciiWhitespace(string line)
    {
        foreach (var current in line)
        {
            if (current is not (' ' or '\t' or '\r' or '\n' or '\v' or '\f')) return false;
        }
        return true;
    }

    private static long? Time(string? value)
    {
        if (value is null) return null;
        var parsed = ParseIso8601UtcMs(value);
        if (parsed is not null) return parsed;
        return value.Length == 19 && value[10] == ' '
            ? ParseIso8601UtcMs($"{value[..10]}T{value[11..]}Z")
            : null;
    }

    /// <summary>解析 ISO 8601 UTC 时间至毫秒：仅接受 UTC `Z` 形式，毫秒最多三位。</summary>
    private static long? ParseIso8601UtcMs(string? value)
    {
        if (value is null) return null;
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length < 20 || bytes[4] != (byte)'-' || bytes[7] != (byte)'-' ||
            bytes[10] != (byte)'T' || bytes[13] != (byte)':' || bytes[16] != (byte)':' ||
            bytes[^1] != (byte)'Z')
        {
            return null;
        }
        if (!TryParseAsciiInt(bytes, 0, 4, out var year) ||
            !TryParseAsciiInt(bytes, 5, 2, out var month) ||
            !TryParseAsciiInt(bytes, 8, 2, out var day) ||
            !TryParseAsciiInt(bytes, 11, 2, out var hour) ||
            !TryParseAsciiInt(bytes, 14, 2, out var minute) ||
            !TryParseAsciiInt(bytes, 17, 2, out var second))
        {
            return null;
        }
        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 60)
        {
            return null;
        }

        long millis = 0;
        if (bytes.Length > 20)
        {
            if (bytes[19] != (byte)'.') return null;
            var fractionLength = bytes.Length - 21;
            if (fractionLength is < 1 or > 3) return null;
            for (var index = 20; index < bytes.Length - 1; index++)
            {
                var current = bytes[index];
                if (current is < (byte)'0' or > (byte)'9') return null;
                millis = millis * 10 + (current - (byte)'0');
            }
            millis *= fractionLength switch
            {
                1 => 100,
                2 => 10,
                _ => 1,
            };
        }

        var days = DaysFromCivil(year, month, day);
        return (days * 86_400 + hour * 3_600 + minute * 60 + second) * 1_000 + millis;
    }

    private static bool TryParseAsciiInt(byte[] bytes, int start, int length, out long value)
    {
        value = 0;
        var index = start;
        var end = start + length;
        var negative = false;
        if (index < end && (bytes[index] == (byte)'+' || bytes[index] == (byte)'-'))
        {
            negative = bytes[index] == (byte)'-';
            index++;
        }
        if (index == end) return false;

        long number = 0;
        for (; index < end; index++)
        {
            var current = bytes[index];
            if (current is < (byte)'0' or > (byte)'9') return false;
            number = number * 10 + (current - (byte)'0');
        }
        value = negative ? -number : number;
        return true;
    }

    private static long DaysFromCivil(long year, long month, long day)
    {
        if (month <= 2) year -= 1;
        var era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - era * 400;
        var dayOfYear = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
        var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        return era * 146_097 + dayOfEra - 719_468;
    }

    private static SyncException InvalidSource() =>
        SyncException.FromSource(new InvalidDataException("unsupported or incomplete Copilot metadata"));

    private sealed record Snapshot(List<SessionImport> Sessions, int Unparsed);

    private sealed record SdkSession(SessionImport Import, SortedSet<string> UserEvents);

    private sealed record Turn(string SdkId, string EventId, string ApiId, RawTokenCounts LastCall,
        List<RawTokenCounts> Totals, string SourcePath, bool Primary);

    private sealed record EventInfo(string Kind, string? Id, string? Timestamp, EventData Data);

    private sealed record EventData(string? SessionId, string? StartTime, string? SelectedModel,
        string? NewModel, string? Model, string? ApiCallId, string? Cwd);

    private sealed record HostUsage(ulong? Input, ulong? Output, ulong? CacheRead, HostCall? Call,
        List<HostTotal>? Totals);

    private sealed record HostCall(ulong SchemaVersion, string SdkSessionId, string ApiCallId);

    private sealed record HostTotal(ulong? Input, ulong? Output, ulong? Cached);

    private sealed class TurnComparer : IComparer<(string Sdk, string Event)>
    {
        public static readonly TurnComparer Instance = new();

        public int Compare((string Sdk, string Event) x, (string Sdk, string Event) y)
        {
            var sdk = string.CompareOrdinal(x.Sdk, y.Sdk);
            return sdk != 0 ? sdk : string.CompareOrdinal(x.Event, y.Event);
        }
    }
}
