using System.IO;
using System.Text.Json;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>Claude Code 转录（含 subagents）；只读取元数据。</summary>
public sealed class ClaudeCodeCollector : IClientCollector
{
    private const int ParserVersion = 1;
    private const string Client = "claude_code";
    private static readonly HashSet<string> IgnoredTypes =
    [
        "system", "progress", "summary", "file-history-snapshot", "queue-operation",
        "last-prompt", "mode", "permission-mode", "atis-latch", "attachment",
        "cost-state", "ai-title", "custom-title", "file-history-delta",
    ];

    private readonly string? _root;

    public ClaudeCodeCollector() : this(DefaultRoot())
    {
    }

    public ClaudeCodeCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    public static string? DefaultRoot()
    {
        var configured = CollectorSupport.EnvPath("CLAUDE_CONFIG_DIR");
        var baseRoot = configured ?? CollectorSupport.Combine(CollectorSupport.HomeDir(), ".claude");
        return CollectorSupport.Combine(baseRoot, "projects");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_root is null || !Directory.Exists(_root))
        {
            report.RootMissing = true;
            return report;
        }

        var paths = CollectorSupport.JsonlFiles(_root, report);
        var sources = new List<SourceFile>();
        var unchanged = !options.ForceRescan;
        foreach (var path in paths)
        {
            var (size, mtime) = CollectorSupport.Fingerprint(path);
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

        var sessions = new Dictionary<string, Rollup>(StringComparer.Ordinal);
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var parsed = ParseFile(path);
            report.FilesParsed++;
            report.UnparsedLines += parsed.Unparsed;
            report.IgnoredLines += parsed.Ignored;
            sources[index] = sources[index] with { UnparsedRecords = parsed.Unparsed };
            foreach (var record in parsed.Records)
            {
                if (string.IsNullOrEmpty(record.SessionId)) continue;
                if (!sessions.TryGetValue(record.SessionId, out var rollup))
                {
                    rollup = new Rollup();
                    sessions[record.SessionId] = rollup;
                }
                rollup.Push(record, path);
            }
        }

        var currentPaths = CollectorSupport.JsonlFiles(_root, new SyncReport());
        if (!currentPaths.SequenceEqual(paths, StringComparer.Ordinal))
        {
            throw SyncException.ChangedDuringScan();
        }
        for (var index = 0; index < paths.Count; index++)
        {
            var (size, mtime) = CollectorSupport.Fingerprint(paths[index]);
            if (sources[index].SizeBytes != size || sources[index].MtimeMs != mtime)
            {
                throw SyncException.ChangedDuringScan();
            }
        }

        // 逐会话惰性产出：Finish 后立即从累积字典移除，避免再复制一整份 MessageRecord。
        var importedCount = 0;
        var imported = store.ReplaceClientSnapshot(Client, sources, Stream());

        report.SessionsImported = importedCount;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;

        IEnumerable<SessionImport> Stream()
        {
            foreach (var sessionId in sessions.Keys.ToList())
            {
                var rollup = sessions[sessionId];
                sessions.Remove(sessionId);
                var entry = rollup.Finish(sessionId);
                if (report.UnparsedLines > 0)
                {
                    entry = entry with
                    {
                        Session = entry.Session with { Coverage = MessageCoverage.Partial },
                    };
                }
                importedCount++;
                yield return entry;
            }
        }
    }

    private static ParsedFile ParseFile(string path)
    {
        var parsed = new ParsedFile();
        CollectorSupport.ReadJsonLines(path, (line, complete) =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException error)
            {
                if (!complete) throw SyncException.FromSource(error);
                parsed.Unparsed++;
                return;
            }
            using (document)
            {
                var element = document.RootElement;
                var kind = element.String("type");
                switch (kind)
                {
                    case "assistant":
                    case "user":
                    {
                        var sessionId = element.String("sessionId");
                        if (string.IsNullOrEmpty(sessionId))
                        {
                            parsed.Unparsed++;
                            return;
                        }
                        if (kind == "assistant")
                        {
                            var message = element.Element("message");
                            if (message is null)
                            {
                                parsed.Unparsed++;
                                return;
                            }
                            if (string.Equals(message.Value.String("model"), "<synthetic>", StringComparison.Ordinal))
                            {
                                return;
                            }
                            var id = message.Value.String("id");
                            var usage = ReadCounts(message.Value.Element("usage"));
                            if (string.IsNullOrEmpty(id) || !CollectorSupport.ValidCounts(usage))
                            {
                                parsed.Unparsed++;
                                return;
                            }
                            parsed.Records.Add(new Record(sessionId, kind, element.String("timestamp"),
                                element.String("cwd"), element.Bool("isSidechain"), id, message.Value.String("model"),
                                usage));
                        }
                        else
                        {
                            parsed.Records.Add(new Record(sessionId, kind, element.String("timestamp"),
                                element.String("cwd"), element.Bool("isSidechain"), null, null, default));
                        }
                        return;
                    }
                    default:
                        if (kind is not null && IgnoredTypes.Contains(kind)) return;
                        parsed.Ignored++;
                        return;
                }
            }
        });
        return parsed;
    }

    private static RawTokenCounts ReadCounts(JsonElement? usage)
    {
        if (usage is not { } value) return default;
        return new RawTokenCounts(
            value.Long("input_tokens"),
            value.Long("output_tokens"),
            value.Long("cache_read_input_tokens"),
            value.Long("cache_creation_input_tokens"));
    }

    private sealed record Record(string SessionId, string Kind, string? Timestamp, string? Cwd,
        bool IsSidechain, string? MessageId, string? Model, RawTokenCounts Usage);

    private sealed class ParsedFile
    {
        public List<Record> Records { get; } = new();
        public int Unparsed { get; set; }
        public int Ignored { get; set; }
    }

    private sealed class Rollup
    {
        private readonly Dictionary<string, (Record Record, string Path)> _records = new(StringComparer.Ordinal);
        private string? _cwd;
        private long? _start;
        private long? _end;
        private string? _sourcePath;

        public void Push(Record record, string path)
        {
            _sourcePath ??= path;
            if (UsageTime.ParseIso8601UtcMs(record.Timestamp) is { } time)
            {
                if (_start is null || time < _start) _start = time;
                if (_end is null || time > _end) _end = time;
            }
            if (record.Cwd is not null) _cwd = record.Cwd;
            if (record.Kind != "assistant" || string.IsNullOrEmpty(record.MessageId)) return;
            if (!_records.TryGetValue(record.MessageId, out var existing) ||
                (UsageTime.ParseIso8601UtcMs(record.Timestamp) ?? long.MinValue) >=
                (UsageTime.ParseIso8601UtcMs(existing.Record.Timestamp) ?? long.MinValue))
            {
                _records[record.MessageId] = (record, path);
            }
        }

        public SessionImport Finish(string sessionId)
        {
            var semantics = TokenSemanticsCatalog.ClaudeCode;
            var counts = new List<RawTokenCounts>();
            var messages = new List<MessageRecord>();
            var messagePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, pair) in _records)
            {
                counts.Add(pair.Record.Usage);
                messagePaths[id] = pair.Path;
                messages.Add(new MessageRecord
                {
                    Client = Client,
                    ClientSessionId = sessionId,
                    DedupKey = id,
                    CreatedAtMs = UsageTime.ParseIso8601UtcMs(pair.Record.Timestamp),
                    Model = pair.Record.Model,
                    Usage = UsageNormalizer.Normalize(semantics, pair.Record.Usage),
                    IsSidechain = pair.Record.IsSidechain,
                    SemanticsId = semantics.Id,
                });
            }
            messages.Sort((left, right) =>
            {
                var compare = (left.CreatedAtMs ?? long.MinValue).CompareTo(right.CreatedAtMs ?? long.MinValue);
                return compare != 0 ? compare : string.CompareOrdinal(left.DedupKey, right.DedupKey);
            });
            var model = messages.AsEnumerable().Reverse().FirstOrDefault(message => message.Model is not null)?.Model;
            var usage = UsageNormalizer.Normalize(semantics, RequestBuilder.SumCompleteCounts(counts));
            var session = new SessionRecord
            {
                Client = Client,
                ClientSessionId = sessionId,
                ProjectPath = _cwd,
                Model = model,
                StartedAtMs = _start,
                EndedAtMs = _end,
                SemanticsId = semantics.Id,
                TotalBasis = SessionTotalBasis.SumOfMessages,
                Coverage = messages.Count == 0 ? MessageCoverage.NoMessages : MessageCoverage.Complete,
                Usage = usage,
            };
            return new SessionImport(session, messages, _sourcePath ?? string.Empty, messagePaths);
        }
    }
}
