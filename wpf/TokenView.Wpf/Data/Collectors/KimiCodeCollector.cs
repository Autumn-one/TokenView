using System.IO;
using System.Text.Json;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>Kimi Code 会话元数据与 agent wire 用量账本；只读取元数据。</summary>
public sealed class KimiCodeCollector : IClientCollector
{
    private const int ParserVersion = 1;
    private const string Client = "kimi_code";

    private static readonly HashSet<string> IgnoredTypes = new(StringComparer.Ordinal)
    {
        "runtime.set_binding", "mcp.tools_discovered", "profile.bind", "config.update",
        "permission.set_mode", "turn.prompt", "context.append_message", "agent.message.appended",
        "agent.turn.started", "plugin.session_start", "context.append_loop_event",
        "llm.tools_snapshot", "llm.request", "token_counting.measured", "agent.turn.ended",
        "turn.ended", "token_counting.turn_recorded", "prompt.completed", "tools.set_active_tools",
    };

    private readonly string? _root;

    public KimiCodeCollector() : this(DefaultRoot())
    {
    }

    public KimiCodeCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    public static string? DefaultRoot()
    {
        var home = CollectorSupport.EnvPath("KIMI_CODE_HOME") ??
                   CollectorSupport.Combine(CollectorSupport.HomeDir(), ".kimi-code");
        return CollectorSupport.Combine(home, "sessions");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_root is null || !Directory.Exists(_root))
        {
            report.RootMissing = true;
            return report;
        }

        var paths = CollectorSupport.DiscoverFiles(_root, report, path => SessionDir(path) is not null);
        var sources = new List<SourceFile>();
        var unchanged = !options.ForceRescan;
        foreach (var path in paths)
        {
            var (size, mtime) = Fingerprint(path);
            var old = store.SourceFile(Client, path);
            unchanged &= CollectorSupport.IsUnchanged(old, size, mtime, ParserVersion);
            sources.Add(CollectorSupport.NewSourceFile(Client, path, SourceFileKind.Jsonl,
                size, mtime, size, ParserVersion, 0));
        }
        unchanged &= store.SourcePaths(Client).SequenceEqual(sources.Select(source => source.Path),
            StringComparer.Ordinal);
        if (unchanged)
        {
            report.FilesSkippedUnchanged = paths.Count;
            return report;
        }

        var sourceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < sources.Count; index++)
        {
            sourceIndex[sources[index].Path] = index;
        }

        var groups = new SortedDictionary<string, SessionFiles>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var directory = SessionDir(path);
            if (directory is null) continue;
            if (!groups.TryGetValue(directory, out var group))
            {
                group = new SessionFiles(Path.Combine(directory, "state.json"), new List<string>());
                groups[directory] = group;
            }
            if (string.Equals(Path.GetFileName(path), "wire.jsonl", StringComparison.Ordinal))
            {
                group.Wires.Add(path);
            }
        }

        var importedCount = 0;
        ImportSummary imported;
        try
        {
            imported = store.ReplaceClientSnapshot(Client, sources, Stream());
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        {
            throw SyncException.Store(error);
        }
        report.SessionsImported = importedCount;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;

        // 逐会话解析并产出：活集只保留当前会话的消息，写完即可回收。
        IEnumerable<SessionImport> Stream()
        {
            foreach (var group in groups.Values)
            {
                var meta = ReadMetadata(group.StatePath);
                var messages = new List<MessageRecord>();
                var counts = new List<RawTokenCounts>();
                var messageSourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
                var partial = false;
                report.FilesParsed++;
                foreach (var wire in group.Wires)
                {
                    var parsed = ReadWire(wire, meta.Id);
                    report.FilesParsed++;
                    report.UnparsedLines += parsed.Unparsed;
                    report.IgnoredLines += parsed.Ignored;
                    partial |= parsed.Partial || parsed.Unparsed > 0;
                    if (sourceIndex.TryGetValue(wire, out var index))
                    {
                        // 解析后复核指纹：追加写入中的会话只提交已读到的完整行，
                        // 水位保留扫描开始值，下次同步因指纹不同而重解析。
                        var (size, mtime) = Fingerprint(wire);
                        if (sources[index].SizeBytes != size || sources[index].MtimeMs != mtime)
                        {
                            partial = true;
                        }
                        sources[index] = sources[index] with { UnparsedRecords = parsed.Unparsed };
                    }
                    foreach (var message in parsed.Messages)
                    {
                        messageSourcePaths[message.DedupKey] = wire;
                    }
                    messages.AddRange(parsed.Messages);
                    counts.AddRange(parsed.Counts);
                }
                importedCount++;
                yield return new SessionImport(BuildSession(meta, messages, counts, partial), messages,
                    group.StatePath, messageSourcePaths);
            }
        }
    }

    private static string? SessionDir(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "state.json", StringComparison.Ordinal))
        {
            var parent = Path.GetDirectoryName(path);
            return parent is not null && IsSessionDir(parent) ? parent : null;
        }
        if (string.Equals(name, "wire.jsonl", StringComparison.Ordinal))
        {
            var agentDir = Path.GetDirectoryName(path);
            var agentsDir = agentDir is null ? null : Path.GetDirectoryName(agentDir);
            if (agentsDir is null ||
                !string.Equals(Path.GetFileName(agentsDir), "agents", StringComparison.Ordinal))
            {
                return null;
            }
            var sessionDir = Path.GetDirectoryName(agentsDir);
            return sessionDir is not null && IsSessionDir(sessionDir) ? sessionDir : null;
        }
        return null;
    }

    private static bool IsSessionDir(string path) =>
        Path.GetFileName(path).StartsWith("session_", StringComparison.Ordinal);

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

    private static KimiMetadata ReadMetadata(string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (JsonException error)
        {
            throw InvalidSource(error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidSource();

            string? id;
            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null)
            {
                if (idElement.ValueKind != JsonValueKind.String) throw InvalidSource();
                id = idElement.GetString();
            }
            else
            {
                id = null;
            }

            if (root.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind != JsonValueKind.Null)
            {
                if (versionElement.ValueKind != JsonValueKind.Number ||
                    !versionElement.TryGetUInt64(out var version) || version != 2)
                {
                    throw InvalidSource();
                }
            }

            if (!TryReadOptionalString(root, "cwd", out var cwd) ||
                !TryReadOptionalString(root, "workDir", out var workDir) ||
                !TryReadOptionalTimestamp(root, "createdAt", out var start) ||
                !TryReadOptionalTimestamp(root, "updatedAt", out var end))
            {
                throw InvalidSource();
            }

            id ??= Path.GetFileName(Path.GetDirectoryName(path));
            if (string.IsNullOrEmpty(id) || start is null) throw InvalidSource();

            return new KimiMetadata(id, cwd ?? workDir, start, end);
        }
    }

    private static ParsedWire ReadWire(string path, string sessionId)
    {
        var agent = Path.GetFileName(Path.GetDirectoryName(path));
        if (string.IsNullOrEmpty(agent))
        {
            throw InvalidSource(new InvalidDataException("missing Kimi Code agent folder"));
        }

        var parsed = new ParsedWire();
        var hasHeader = false;
        long ordinal = 0;
        try
        {
            CollectorSupport.ReadJsonLines(path, (line, complete) =>
            {
                ordinal = checked(ordinal + 1);
                if (string.IsNullOrWhiteSpace(line)) return;

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // 写入中的末行无法解析：只统计完整行，覆盖标 partial。
                    parsed.Unparsed++;
                    if (!complete) parsed.Partial = true;
                    return;
                }

                using (document)
                {
                    var element = document.RootElement;
                    var kind = element.ValueKind == JsonValueKind.Object ? element.String("type") : null;
                    if (kind is null)
                    {
                        parsed.Unparsed++;
                        return;
                    }

                    if (!hasHeader)
                    {
                        if (string.Equals(kind, "metadata", StringComparison.Ordinal) &&
                            TryReadOptionalString(element, "protocol_version", out var protocol) &&
                            protocol is "1.4" or "1.5")
                        {
                            hasHeader = true;
                            return;
                        }
                        if (!complete)
                        {
                            // 新会话刚建立，头部行还没写完整，下次同步重解析。
                            parsed.Unparsed++;
                            parsed.Partial = true;
                            return;
                        }
                        throw InvalidSource();
                    }

                    switch (kind)
                    {
                        case "usage.record":
                        {
                            if (!TryReadUsageRecord(element, out var record))
                            {
                                parsed.Unparsed++;
                                return;
                            }
                            if (record.UsageScope is not null &&
                                record.UsageScope is not ("turn" or "session"))
                            {
                                parsed.Unparsed++;
                                return;
                            }
                            if (!CollectorSupport.ValidCounts(record.Counts))
                            {
                                parsed.Unparsed++;
                                return;
                            }
                            parsed.Messages.Add(BuildMessage(sessionId, agent, ordinal, record.Time,
                                record.Model, record.Counts));
                            parsed.Counts.Add(record.Counts);
                            return;
                        }
                        default:
                            if (IgnoredTypes.Contains(kind)) return;
                            parsed.Ignored++;
                            return;
                    }
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }

        if (!hasHeader)
        {
            // 空 wire 或只有未写完的头部：新会话刚建立，按不完整处理。
            parsed.Partial = true;
        }
        return parsed;
    }

    private static bool TryReadUsageRecord(JsonElement element, out UsageRecord record)
    {
        record = default;
        if (!TryReadOptionalString(element, "model", out var model) ||
            !TryReadOptionalUsage(element, "usage", out var counts) ||
            !TryReadOptionalString(element, "usageScope", out var scope) ||
            !TryReadOptionalInt64(element, "time", out var time))
        {
            return false;
        }
        record = new UsageRecord(model, scope, time is >= 0 ? time : null, counts);
        return true;
    }

    private static bool TryReadOptionalUsage(JsonElement parent, string name, out RawTokenCounts counts)
    {
        counts = default;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (element.ValueKind != JsonValueKind.Object) return false;

        if (!TryReadOptionalUInt64(element, "inputOther", out var input) ||
            !TryReadOptionalUInt64(element, "output", out var output) ||
            !TryReadOptionalUInt64(element, "inputCacheRead", out var cacheRead) ||
            !TryReadOptionalUInt64(element, "inputCacheCreation", out var cacheWrite))
        {
            return false;
        }

        ulong total = 0;
        foreach (var value in new[] { input, output, cacheRead, cacheWrite })
        {
            if (value is not { } number) continue;
            if (total > ulong.MaxValue - number) return false;
            total += number;
        }
        if (total > long.MaxValue) return false;

        counts = new RawTokenCounts((long?)input, (long?)output, (long?)cacheRead, (long?)cacheWrite);
        return true;
    }

    private static bool TryReadOptionalString(JsonElement parent, string name, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return true;
    }

    private static bool TryReadOptionalInt64(JsonElement parent, string name, out long? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var number))
        {
            return false;
        }
        value = number;
        return true;
    }

    private static bool TryReadOptionalUInt64(JsonElement parent, string name, out ulong? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetUInt64(out var number))
        {
            return false;
        }
        value = number;
        return true;
    }

    private static bool TryReadOptionalTimestamp(JsonElement parent, string name, out long? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var millis)) return false;
                value = millis >= 0 ? millis : null;
                return true;
            case JsonValueKind.String:
                value = UsageTime.ParseIso8601UtcMs(element.GetString());
                return true;
            default:
                return false;
        }
    }

    private static MessageRecord BuildMessage(string sessionId, string agent, long line, long? time,
        string? model, RawTokenCounts raw)
    {
        var semantics = TokenSemanticsCatalog.KimiCode;
        return new MessageRecord
        {
            Client = Client,
            ClientSessionId = sessionId,
            DedupKey = $"agent:{agent}:line:{line}",
            Seq = line,
            CreatedAtMs = time,
            CompletedAtMs = time,
            Model = model,
            Agent = agent,
            Usage = UsageNormalizer.Normalize(semantics, raw),
            IsSidechain = !string.Equals(agent, "main", StringComparison.Ordinal),
            SemanticsId = semantics.Id,
            SourceKind = SourceKind.Measured,
            SourceRef = $"line={line}",
            CostSource = CostSource.None,
            ClientCostUsd = null,
        };
    }

    private static SessionRecord BuildSession(KimiMetadata meta, List<MessageRecord> messages,
        List<RawTokenCounts> counts, bool partial)
    {
        var semantics = TokenSemanticsCatalog.KimiCode;
        MessageRecord? modelMessage = null;
        foreach (var message in messages)
        {
            if (message.Model is null) continue;
            if (modelMessage is null ||
                (message.CreatedAtMs ?? long.MinValue) >= (modelMessage.CreatedAtMs ?? long.MinValue))
            {
                modelMessage = message;
            }
        }

        long? ended = meta.EndMs;
        foreach (var message in messages)
        {
            if (message.CreatedAtMs is { } value && (ended is null || value > ended)) ended = value;
        }

        var coverage = partial
            ? MessageCoverage.Partial
            : messages.Count == 0
                ? MessageCoverage.NoMessages
                : MessageCoverage.Complete;

        return new SessionRecord
        {
            Client = Client,
            ClientSessionId = meta.Id,
            ParentClientSessionId = null,
            ProjectPath = meta.Project,
            Model = modelMessage?.Model,
            StartedAtMs = meta.StartMs,
            EndedAtMs = ended,
            SemanticsId = semantics.Id,
            TotalBasis = SessionTotalBasis.SumOfMessages,
            Coverage = coverage,
            Usage = UsageNormalizer.Normalize(semantics, RequestBuilder.SumCompleteCounts(counts)),
            SourceKind = SourceKind.Measured,
            CostSource = CostSource.None,
            ClientCostUsd = null,
        };
    }

    private static SyncException InvalidSource(Exception? inner = null, string message =
        "unsupported or incomplete Kimi Code session") =>
        SyncException.FromSource(inner ?? new InvalidDataException(message));

    private sealed record KimiMetadata(string Id, string? Project, long? StartMs, long? EndMs);

    private readonly record struct UsageRecord(string? Model, string? UsageScope, long? Time,
        RawTokenCounts Counts);

    private sealed record SessionFiles(string StatePath, List<string> Wires);

    private sealed class ParsedWire
    {
        public List<MessageRecord> Messages { get; } = new();
        public List<RawTokenCounts> Counts { get; } = new();
        public bool Partial { get; set; }
        public int Unparsed { get; set; }
        public int Ignored { get; set; }
    }
}
