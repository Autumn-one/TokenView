using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>DSH / deepseek-hermes 会话元数据采集器；只读取统计所需字段。</summary>
public sealed class DshCollector : IClientCollector
{
    private const int ParserVersion = 2;
    private const string Client = "dsh";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> IgnoredKinds = new(StringComparer.Ordinal)
    {
        "text-chunks", "reasoning-chunks", "tool-call-chunks", "user/message", "tool/call",
        "tool/result", "turn/start", "turn/end", "step/start", "step/end", "request/context",
        "permission/preset", "sandbox/mode", "approval/policy", "agent/inbox/spliced",
        "session/title", "session/title-llm-request", "web/deepseek-search-llm-request",
        "llm/retry", "todo/write", "command/run", "command/done", "approval/asked",
        "approval/decided", "compaction/start", "compaction/summary", "compaction/end",
        "compaction/prune", "system/message", "model/selection", "workspace/changes",
        "session-log-deepseek/delivery-accepted", "agent-preset/selected",
    };

    private readonly string? _root;

    public DshCollector() : this(DefaultRoot())
    {
    }

    public DshCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    public static string? DefaultRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_SESSIONS_DIR");
        if (configured is not null) return configured;
        return CollectorSupport.Combine(CollectorSupport.HomeDir(), ".dsh", "sessions");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_root is null || !Directory.Exists(_root))
        {
            if (_root is not null && File.Exists(_root))
                throw SyncException.Directory(new IOException("DSH sessions root is not a directory"));
            report.RootMissing = true;
            return report;
        }

        var discovered = CollectorSupport.DiscoverFiles(_root, report, path => Generation(path) is not null);
        var paths = Select(discovered);
        var sources = new List<SourceFile>(paths.Count);
        var unchanged = !options.ForceRescan;
        foreach (var path in paths)
        {
            var (size, mtime) = Fingerprint(path);
            var old = store.SourceFile(Client, path);
            unchanged &= CollectorSupport.IsUnchanged(old, size, mtime, ParserVersion);
            sources.Add(CollectorSupport.NewSourceFile(Client, path, SourceFileKind.Jsonl,
                size, mtime, size, ParserVersion, 0));
        }

        var sourcePaths = sources.Select(source => source.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        unchanged &= store.SourcePaths(Client).SequenceEqual(sourcePaths, StringComparer.Ordinal);
        if (unchanged)
        {
            report.FilesSkippedUnchanged = paths.Count;
            return report;
        }

        var sessions = new SortedDictionary<string, SessionImport>(StringComparer.Ordinal);
        var successful = new List<SourceFile>();
        for (var index = 0; index < paths.Count; index++)
        {
            Parsed parsed;
            try
            {
                parsed = ParseFile(paths[index]);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          DshFormatException or JsonException or DecoderFallbackException or
                                          OverflowException or ArgumentException or IndexOutOfRangeException or
                                          InvalidOperationException)
            {
                report.FilesFailed++;
                continue;
            }

            report.FilesParsed++;
            report.UnparsedLines += parsed.Unparsed;
            sources[index] = sources[index] with { UnparsedRecords = parsed.Unparsed };
            successful.Add(sources[index]);

            var id = parsed.Import.Session.ClientSessionId;
            if (!sessions.TryGetValue(id, out var existing) ||
                AtLeastAsLate(parsed.Import.Session.EndedAtMs, existing.Session.EndedAtMs))
            {
                sessions[id] = parsed.Import;
            }
        }

        var currentPaths = Select(CollectorSupport.DiscoverFiles(_root, new SyncReport(),
            path => Generation(path) is not null));
        if (!currentPaths.SequenceEqual(paths, StringComparer.Ordinal))
        {
            throw SyncException.ChangedDuringScan();
        }
        for (var index = 0; index < paths.Count; index++)
        {
            var (size, mtime) = Fingerprint(paths[index]);
            if (sources[index].SizeBytes != size || sources[index].MtimeMs != mtime)
            {
                throw SyncException.ChangedDuringScan();
            }
        }

        ImportSummary imported;
        try
        {
            var imports = sessions.Values.ToList();
            imported = report.FilesFailed == 0
                ? store.ReplaceClientSnapshot(Client, successful, imports)
                : store.MergeClientSnapshot(Client, successful, imports);
        }
        catch (Exception error) when (error is SqliteException or ArgumentException)
        {
            throw SyncException.Store(error);
        }

        report.SessionsImported = sessions.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private static bool AtLeastAsLate(long? candidate, long? existing) =>
        candidate is null ? existing is null : existing is null || candidate.Value >= existing.Value;

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

    private static List<string> Select(IEnumerable<string> paths)
    {
        var selected = new Dictionary<string, (ulong Version, string Path)>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var version = Generation(path);
            if (version is null) continue;
            var parent = Path.GetDirectoryName(path);
            if (parent is null) continue;
            if (!selected.TryGetValue(parent, out var old) ||
                version.Value > old.Version ||
                (version.Value == old.Version && string.CompareOrdinal(path, old.Path) < 0))
            {
                selected[parent] = (version.Value, path);
            }
        }
        return selected.Values.Select(value => value.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static ulong? Generation(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".zstd", StringComparison.Ordinal))
            name = name[..^5];
        if (string.Equals(name, "session.jsonl", StringComparison.Ordinal)) return 0;
        if (!name.StartsWith("session.v", StringComparison.Ordinal) ||
            !name.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            return null;
        }
        var version = name["session.v".Length..^".jsonl".Length];
        if (version.Length == 0 || version[0] == '0') return null;
        foreach (var character in version)
        {
            if (character is < '0' or > '9') return null;
        }
        return ulong.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static Parsed ParseFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var state = new ParseState(path);
        var consumer = new DshLineConsumer(state.OnLine);
        if (string.Equals(Path.GetExtension(path), ".zstd", StringComparison.Ordinal))
            DshZstd.Decompress(bytes, consumer.Append);
        else
            consumer.Append(bytes, 0, bytes.Length);
        consumer.Flush();
        return state.Finish();
    }

    private sealed class ParseState
    {
        private readonly string _path;
        private readonly List<DshEvent> _events = new();
        private Header? _header;
        private long? _seedCut;
        private int _unparsed;

        public ParseState(string path)
        {
            _path = path;
        }

        public void OnLine(string line, bool complete)
        {
            if (_header is null)
            {
                _header = ParseHeader(line);
                return;
            }
            if (string.IsNullOrWhiteSpace(line)) return;
            ParseEvent(line, complete);
        }

        public Parsed Finish()
        {
            if (_header is not { } header) throw Invalid();
            long cut;
            if (header.Version < 2)
            {
                cut = header.SeedLength ?? 0;
            }
            else
            {
                if (header.IsSeeded == true)
                {
                    if (_seedCut is not { } seed) throw Invalid();
                    cut = seed;
                }
                else if (header.IsSeeded == false)
                {
                    if (_seedCut is not null) throw Invalid();
                    cut = 0;
                }
                else
                {
                    throw Invalid();
                }
            }
            return Fold(header, _events, cut, _path, _unparsed);
        }

        private Header ParseHeader(string line)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException error)
            {
                throw Invalid(error);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw Invalid();
                if (!TryRequiredString(root, "type", out var kind) ||
                    !TryRequiredUInt64(root, "version", out var version) ||
                    !TryRequiredString(root, "id", out var id) ||
                    !TryOptionalInt64(root, "createdAt", out var createdAt) ||
                    !TryOptionalString(root, "cwd", out var cwd) ||
                    !TryOptionalString(root, "parentSession", out var parent) ||
                    !TryOptionalInt64(root, "seedLength", out var seedLength) ||
                    !TryOptionalBool(root, "isSeeded", out var isSeeded) ||
                    !TryOptionalString(root, "origin", out var origin))
                {
                    throw Invalid();
                }
                if (!string.Equals(kind, "session", StringComparison.Ordinal) ||
                    version > 4 ||
                    string.IsNullOrEmpty(id) ||
                    Generation(_path) != version ||
                    (version >= 2 && isSeeded is null) ||
                    seedLength is < 0)
                {
                    throw Invalid();
                }
                return new Header(id, version, createdAt, cwd, parent, seedLength, isSeeded, origin);
            }
        }

        private void ParseEvent(string line, bool complete)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException error)
            {
                if (!complete) throw Invalid(error);
                _unparsed++;
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !TryRequiredString(root, "type", out var kind))
                {
                    if (!complete) throw Invalid();
                    _unparsed++;
                    return;
                }

                switch (kind)
                {
                    case "session/end-seed":
                        if (_header!.Version >= 2) ParseEndSeed(root);
                        return;
                    case "request/header":
                        if (!TryParseRequest(root, out var model, out var modelSeq, out var modelTime))
                        {
                            _unparsed++;
                            return;
                        }
                        _events.Add(new ModelEvent(modelSeq, modelTime, model));
                        return;
                    case "assistant/message":
                    case "assistant/attempt":
                    case "assistant/chunk":
                    case "llm/retry-started":
                        ParseAssistant(root, kind);
                        return;
                }

                if (IgnoredKinds.Contains(kind)) return;
                _unparsed++;
            }
        }

        private void ParseEndSeed(JsonElement root)
        {
            if (!TryOptionalInt64(root, "seq", out var seq) ||
                !TryOptionalInt64(root, "time", out _) ||
                !TryRequiredObject(root, "data", out var data) ||
                !TryOptionalBool(data, "inherited", out var inherited))
            {
                throw Invalid();
            }
            if (inherited != true) return;
            if (seq is not { } value || value < 0) throw Invalid();
            _seedCut = value;
        }

        private void ParseAssistant(JsonElement root, string kind)
        {
            if (!TryParseAssistant(root, out var parsed))
            {
                _unparsed++;
                return;
            }
            if (parsed.Seq is not { } seq || seq < 0 ||
                parsed.Turn is not { } turn || turn < 0 ||
                parsed.Step is not { } step || step < 0)
            {
                _unparsed++;
                return;
            }

            if (string.Equals(kind, "llm/retry-started", StringComparison.Ordinal))
            {
                _events.Add(new RetryEvent(seq, parsed.Time, turn, step));
                return;
            }

            RawTokenCounts? usage;
            if (string.Equals(kind, "assistant/chunk", StringComparison.Ordinal))
            {
                if (parsed.Chunk is not { Kind: "usage" } chunk) return;
                usage = chunk.Usage;
            }
            else
            {
                usage = parsed.HasUsage ? parsed.Usage : parsed.StreamUsage;
            }

            if (usage is { } counts && !CollectorSupport.ValidCounts(counts))
            {
                _unparsed++;
                return;
            }
            _events.Add(new SampleEvent(seq, parsed.Time, turn, step, usage,
                string.Equals(kind, "assistant/message", StringComparison.Ordinal)));
        }
    }

    private static Parsed Fold(Header header, List<DshEvent> events, long cut, string path, int unparsed)
    {
        string? model = null;
        long? end = header.CreatedAt;
        var messages = new SortedDictionary<(long Turn, long Step, ulong Attempt), MessageRecord>();
        var usages = new Dictionary<(long Turn, long Step, ulong Attempt), RawTokenCounts>();
        var attempts = new Dictionary<(long Turn, long Step), ulong>();
        var retries = new HashSet<long>();
        (long Turn, long Step)? lastSample = null;

        foreach (var item in events)
        {
            if (item is ModelEvent modelEvent)
            {
                model = modelEvent.Model;
                continue;
            }
            if (item.Seq < cut) continue;

            switch (item)
            {
                case RetryEvent retry:
                    if (retries.Add(retry.Seq) && lastSample == (retry.Turn, retry.Step))
                    {
                        var retryKey = (retry.Turn, retry.Step);
                        attempts[retryKey] = attempts.GetValueOrDefault(retryKey) + 1;
                        lastSample = null;
                    }
                    break;
                case SampleEvent sample:
                    var turnStep = (sample.Turn, sample.Step);
                    var attempt = attempts.GetValueOrDefault(turnStep);
                    var key = (sample.Turn, sample.Step, attempt);
                    var hasSample = sample.Usage is not null;
                    var usage = sample.Usage ??
                                (usages.TryGetValue(key, out var previous) ? previous : default);
                    end = Max(end, sample.Time);
                    var message = BuildMessage(header, sample.Seq, sample.Turn, sample.Step, attempt,
                        sample.Time, model, usage, sample.Complete);
                    messages[key] = message;
                    usages[key] = usage;
                    if (hasSample) lastSample = turnStep;
                    break;
            }
        }

        var messageList = messages.Values.ToList();
        var totals = RequestBuilder.SumCompleteCounts(usages.Values.ToList());
        var session = new SessionRecord
        {
            Client = Client,
            ClientSessionId = header.Id,
            ParentClientSessionId = header.ParentSession,
            ProjectPath = header.Cwd,
            Model = model,
            StartedAtMs = header.CreatedAt,
            EndedAtMs = end,
            SemanticsId = TokenSemanticsCatalog.Dsh.Id,
            TotalBasis = SessionTotalBasis.SumOfMessages,
            Coverage = unparsed > 0
                ? MessageCoverage.Partial
                : messageList.Count == 0
                    ? MessageCoverage.NoMessages
                    : MessageCoverage.Complete,
            Usage = DshUsage(totals),
            SourceKind = SourceKind.Measured,
            CostSource = CostSource.None,
            ClientCostUsd = null,
        };
        return new Parsed(SessionImport.Create(session, messageList, path), unparsed);
    }

    private static MessageRecord BuildMessage(Header header, long seq, long turn, long step,
        ulong attempt, long? time, string? model, RawTokenCounts usage, bool complete)
    {
        var dedup = attempt == 0
            ? string.Create(CultureInfo.InvariantCulture, $"step:{turn}:{step}")
            : string.Create(CultureInfo.InvariantCulture, $"step:{turn}:{step}:attempt:{attempt}");
        return new MessageRecord
        {
            Client = Client,
            ClientSessionId = header.Id,
            DedupKey = dedup,
            Seq = seq,
            CreatedAtMs = time,
            CompletedAtMs = complete ? time : null,
            Model = model,
            Agent = null,
            Usage = DshUsage(usage),
            IsSidechain = string.Equals(header.Origin, "subagent", StringComparison.Ordinal),
            SemanticsId = TokenSemanticsCatalog.Dsh.Id,
            SourceKind = SourceKind.Measured,
            SourceRef = string.Create(CultureInfo.InvariantCulture, $"seq={seq}"),
            CostSource = CostSource.None,
            ClientCostUsd = null,
        };
    }

    private static NormalizedUsage DshUsage(RawTokenCounts raw)
    {
        long? clientTotal = null;
        if (raw.Input is { } input && raw.Output is { } output &&
            raw.CacheRead is { } cacheRead && raw.CacheWrite is { } cacheWrite)
        {
            long total = 0;
            var valid = true;
            foreach (var value in new[] { input, output, cacheRead, cacheWrite })
            {
                if (total > long.MaxValue - value)
                {
                    valid = false;
                    break;
                }
                total += value;
            }
            if (valid) clientTotal = total;
        }

        var usage = UsageNormalizer.Normalize(TokenSemanticsCatalog.Dsh,
            raw with { ClientTotal = clientTotal });
        if (usage.TotalTokens is not null)
            usage = usage with { TotalBasis = UsageTotalBasis.DerivedFromBuckets };
        return usage;
    }

    private static long? Max(long? left, long? right) =>
        left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    private static bool TryParseRequest(JsonElement root, out string? model, out long seq, out long? time)
    {
        model = null;
        seq = 0;
        time = null;
        if (!TryOptionalInt64(root, "seq", out var eventSeq) ||
            !TryOptionalInt64(root, "time", out var eventTime) ||
            !TryRequiredObject(root, "data", out var data) ||
            !TryRequiredObject(data, "header", out var header) ||
            !TryRequiredObject(header, "config", out var config) ||
            !TryOptionalString(config, "provider", out var provider) ||
            !TryOptionalString(config, "model", out var configuredModel))
        {
            return false;
        }
        seq = eventSeq ?? 0;
        time = eventTime;
        if (configuredModel is not null)
            model = provider is null ? configuredModel : $"{provider}/{configuredModel}";
        return true;
    }

    private static bool TryParseAssistant(JsonElement root, out AssistantFields fields)
    {
        fields = default;
        if (!TryOptionalInt64(root, "seq", out var seq) ||
            !TryOptionalInt64(root, "time", out var time) ||
            !TryRequiredObject(root, "data", out var data) ||
            !TryOptionalInt64(data, "turn", out var turn) ||
            !TryOptionalInt64(data, "step", out var step) ||
            !TryParseUsage(data, "usage", out var usage, out var hasUsage) ||
            !TryParseChunk(data, "chunk", out var chunk) ||
            !TryParseStream(data, out var streamUsage))
        {
            return false;
        }
        fields = new AssistantFields(seq, time, turn, step, usage, hasUsage, chunk, streamUsage);
        return true;
    }

    private static bool TryParseUsage(JsonElement parent, string name,
        out RawTokenCounts? usage, out bool hasUsage)
    {
        usage = null;
        hasUsage = false;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Object) return false;
        hasUsage = true;
        if (!TryOptionalCount(element, "inputTokens", out var input) ||
            !TryOptionalCount(element, "outputTokens", out var output) ||
            !TryOptionalCache(element, "cacheReadTokens", out var cacheRead) ||
            !TryOptionalCache(element, "cacheWriteTokens", out var cacheWrite) ||
            !TryOptionalCount(element, "reasoningTokens", out var reasoning))
        {
            return false;
        }
        usage = new RawTokenCounts(input, output, cacheRead ?? 0, cacheWrite ?? 0, reasoning);
        return true;
    }

    private static bool TryParseChunk(JsonElement parent, string name, out ChunkInfo? chunk)
    {
        chunk = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!TryRequiredString(element, "type", out var kind)) return false;
        if (!TryParseUsage(element, "usage", out var usage, out _)) return false;
        chunk = new ChunkInfo(kind, usage);
        return true;
    }

    private static bool TryParseStream(JsonElement parent, out RawTokenCounts? lastUsage)
    {
        lastUsage = null;
        if (!parent.TryGetProperty("stream", out var stream)) return true;
        if (stream.ValueKind == JsonValueKind.Null) return false;
        if (stream.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in stream.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryRequiredString(item, "type", out var kind) ||
                !TryParseChunk(item, "chunk", out var chunk))
            {
                return false;
            }
            if (string.Equals(kind, "chunk", StringComparison.Ordinal) &&
                chunk is { Kind: "usage", Usage: { } usage })
            {
                lastUsage = usage;
            }
        }
        return true;
    }

    private static bool TryRequiredString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        var text = element.GetString();
        if (text is null) return false;
        value = text;
        return true;
    }

    private static bool TryRequiredUInt64(JsonElement parent, string name, out ulong value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetUInt64(out value);
    }

    private static bool TryRequiredObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (!parent.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        value = element;
        return true;
    }

    private static bool TryOptionalString(JsonElement parent, string name, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return true;
    }

    private static bool TryOptionalInt64(JsonElement parent, string name, out long? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var number))
            return false;
        value = number;
        return true;
    }

    private static bool TryOptionalBool(JsonElement parent, string name, out bool? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool TryOptionalCount(JsonElement parent, string name, out long? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetUInt64(out var number) || number > long.MaxValue)
        {
            return false;
        }
        value = (long)number;
        return true;
    }

    private static bool TryOptionalCache(JsonElement parent, string name, out long? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element)) return true;
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetUInt64(out var number) || number > long.MaxValue)
        {
            return false;
        }
        value = (long)number;
        return true;
    }

    private static DshFormatException Invalid(Exception? inner = null) =>
        new("unsupported or incomplete DSH session", inner);

    private sealed record Header(
        string Id,
        ulong Version,
        long? CreatedAt,
        string? Cwd,
        string? ParentSession,
        long? SeedLength,
        bool? IsSeeded,
        string? Origin);

    private abstract record DshEvent(long Seq, long? Time);

    private sealed record ModelEvent(long Seq, long? Time, string? Model) : DshEvent(Seq, Time);

    private sealed record RetryEvent(long Seq, long? Time, long Turn, long Step) : DshEvent(Seq, Time);

    private sealed record SampleEvent(long Seq, long? Time, long Turn, long Step,
        RawTokenCounts? Usage, bool Complete) : DshEvent(Seq, Time);

    private readonly record struct ChunkInfo(string Kind, RawTokenCounts? Usage);

    private readonly record struct AssistantFields(long? Seq, long? Time, long? Turn, long? Step,
        RawTokenCounts? Usage, bool HasUsage, ChunkInfo? Chunk, RawTokenCounts? StreamUsage);

    private sealed record Parsed(SessionImport Import, int Unparsed);
}

internal sealed class DshFormatException : Exception
{
    public DshFormatException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

internal sealed class DshLineConsumer
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private readonly Action<string, bool> _onLine;
    private readonly MemoryStream _line = new();

    public DshLineConsumer(Action<string, bool> onLine)
    {
        _onLine = onLine;
    }

    public void Append(byte[] buffer, int offset, int count)
    {
        var span = buffer.AsSpan(offset, count);
        while (span.Length > 0)
        {
            var newline = span.IndexOf((byte)'\n');
            if (newline < 0)
            {
                _line.Write(span);
                return;
            }

            var piece = span[..newline];
            if (_line.Length == 0)
            {
                Emit(piece, true);
            }
            else
            {
                _line.Write(piece);
                Emit(_line.GetBuffer().AsSpan(0, (int)_line.Length), true);
                _line.SetLength(0);
            }
            span = span[(newline + 1)..];
        }
    }

    public void Flush()
    {
        if (_line.Length == 0) return;
        Emit(_line.GetBuffer().AsSpan(0, (int)_line.Length), false);
        _line.SetLength(0);
    }

    private void Emit(ReadOnlySpan<byte> bytes, bool complete)
    {
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        _onLine(Utf8.GetString(bytes), complete);
    }
}

internal static class DshZstd
{
    private const uint FrameMagic = 0xFD2FB528;
    private const int BlockMaximum = 1 << 17;
    private const int MaximumWindow = 128 << 20;

    public static void Decompress(byte[] data, Action<byte[], int, int> sink)
    {
        var decoder = new FrameDecoder(data, sink);
        decoder.Run();
    }

    private sealed class FrameDecoder
    {
        private readonly byte[] _data;
        private readonly Action<byte[], int, int> _sink;
        private int _position;

        private byte[] _window = [];
        private int _windowCapacity;
        private int _windowStart;
        private int _windowCount;
        private long _frameOutput;
        private long _blockStart;
        private byte[] _block = new byte[BlockMaximum];

        private FseTable? _llTable;
        private FseTable? _ofTable;
        private FseTable? _mlTable;
        private HuffmanTable? _huffman;
        private XxHash64? _checksum;

        private int _rep0 = 1;
        private int _rep1 = 4;
        private int _rep2 = 8;

        public FrameDecoder(byte[] data, Action<byte[], int, int> sink)
        {
            _data = data;
            _sink = sink;
        }

        public void Run()
        {
            while (_position < _data.Length)
            {
                if (_data.Length - _position < 4) throw Corrupt("truncated frame");
                var magic = ReadU32(_data, _position);
                _position += 4;
                if ((magic & 0xFFFFFFF0u) == 0x184D2A50u)
                {
                    if (_data.Length - _position < 4) throw Corrupt("truncated skippable frame");
                    var size = ReadU32(_data, _position);
                    _position += 4;
                    if (size > (uint)(_data.Length - _position)) throw Corrupt("truncated skippable frame");
                    _position += (int)size;
                    continue;
                }
                if (magic != FrameMagic) throw Corrupt("bad zstd magic");
                DecodeFrame();
            }
        }

        private void DecodeFrame()
        {
            if (_data.Length - _position < 1) throw Corrupt("truncated frame header");
            var descriptor = _data[_position++];
            var fcsFlag = descriptor >> 6;
            var single = (descriptor & 0x20) != 0;
            var checksum = (descriptor & 0x04) != 0;
            var dictFlag = descriptor & 3;
            if ((descriptor & 0x08) != 0) throw Corrupt("reserved frame bit");

            long windowSize;
            if (!single)
            {
                if (_data.Length - _position < 1) throw Corrupt("truncated window descriptor");
                var descriptorByte = _data[_position++];
                var exponent = descriptorByte >> 3;
                var mantissa = descriptorByte & 7;
                if (exponent > 31) throw Corrupt("window too large");
                var windowLog = 10 + exponent;
                if (windowLog > 31) throw Corrupt("window too large");
                var windowBase = 1L << windowLog;
                windowSize = windowBase + (windowBase / 8) * mantissa;
            }
            else
            {
                windowSize = 0;
            }

            var dictSize = dictFlag switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
            if (_data.Length - _position < dictSize) throw Corrupt("truncated dictionary id");
            if (dictSize > 0)
            {
                uint dictionary = 0;
                for (var i = 0; i < dictSize; i++) dictionary |= (uint)_data[_position + i] << (8 * i);
                _position += dictSize;
                if (dictionary != 0) throw Corrupt("dictionary frames unsupported");
            }

            ulong contentSize = 0;
            var hasContentSize = false;
            var fcsSize = fcsFlag switch { 0 => single ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
            if (fcsSize > 0)
            {
                if (_data.Length - _position < fcsSize) throw Corrupt("truncated frame content size");
                contentSize = fcsSize switch
                {
                    1 => _data[_position],
                    2 => (ulong)(ReadU16(_data, _position) + 256),
                    4 => ReadU32(_data, _position),
                    _ => ReadU64(_data, _position),
                };
                _position += fcsSize;
                hasContentSize = true;
                if (single)
                {
                    if (contentSize > MaximumWindow) throw Corrupt("frame content too large");
                    windowSize = (long)contentSize;
                }
            }
            if (windowSize < 0 || windowSize > MaximumWindow) throw Corrupt("window too large");
            if (windowSize == 0) windowSize = 1;

            if (_window.Length < windowSize) _window = new byte[windowSize];
            _windowCapacity = (int)windowSize;
            _windowStart = 0;
            _windowCount = 0;
            _frameOutput = 0;
            _llTable = null;
            _ofTable = null;
            _mlTable = null;
            _huffman = null;
            _checksum = checksum ? new XxHash64() : null;
            _rep0 = 1;
            _rep1 = 4;
            _rep2 = 8;

            var last = false;
            var blocks = 0;
            while (!last)
            {
                if (_data.Length - _position < 3) throw Corrupt("truncated block header");
                var header = _data[_position] | (_data[_position + 1] << 8) | (_data[_position + 2] << 16);
                _position += 3;
                last = (header & 1) != 0;
                var type = (header >> 1) & 3;
                var size = header >> 3;
                blocks++;
                switch (type)
                {
                    case 0:
                        if (size > BlockLimit) throw Corrupt("raw block too large");
                        if (_data.Length - _position < size) throw Corrupt("truncated raw block");
                        PrepareBlock(size);
                        Array.Copy(_data, _position, _block, 0, size);
                        _position += size;
                        FinishBlock(size);
                        break;
                    case 1:
                        if (size > BlockLimit) throw Corrupt("rle block too large");
                        if (_data.Length - _position < 1) throw Corrupt("truncated rle block");
                        PrepareBlock(size);
                        Array.Fill(_block, _data[_position], 0, size);
                        _position += 1;
                        FinishBlock(size);
                        break;
                    case 2:
                        if (_data.Length - _position < size) throw Corrupt("truncated compressed block");
                        var blockEnd = _position + size;
                        var length = DecodeCompressedBlock(_position, blockEnd);
                        _position = blockEnd;
                        FinishBlock(length);
                        break;
                    default:
                        throw Corrupt("reserved block type");
                }
                if (hasContentSize && (ulong)_frameOutput > contentSize) throw Corrupt("frame exceeds content size");
                if (blocks > 1_000_000) throw Corrupt("too many blocks");
            }
            if (checksum)
            {
                if (_data.Length - _position < 4) throw Corrupt("truncated checksum");
                var expected = ReadU32(_data, _position);
                _position += 4;
                if ((uint)_checksum!.Digest() != expected) throw Corrupt("checksum mismatch");
            }
            if (hasContentSize && (ulong)_frameOutput != contentSize) throw Corrupt("content size mismatch");
        }

        private int BlockLimit => Math.Min(BlockMaximum, _windowCapacity);

        private void PrepareBlock(int length)
        {
            if (length < 0 || length > BlockLimit) throw Corrupt("block too large");
            _blockStart = _frameOutput;
        }

        private void FinishBlock(int length)
        {
            if (length > 0)
            {
                _checksum?.Update(_block.AsSpan(0, length));
                _sink(_block, 0, length);
                AppendWindow(_block, length);
            }
            _frameOutput += length;
        }

        private void AppendWindow(byte[] source, int length)
        {
            var capacity = _windowCapacity;
            if (length >= capacity)
            {
                Array.Copy(source, length - capacity, _window, 0, capacity);
                _windowStart = 0;
                _windowCount = capacity;
                return;
            }
            if (_windowCount + length <= capacity)
            {
                var first = Math.Min(length, capacity - ((_windowStart + _windowCount) % capacity));
                var start = (_windowStart + _windowCount) % capacity;
                Array.Copy(source, 0, _window, start, first);
                if (first < length) Array.Copy(source, first, _window, 0, length - first);
                _windowCount += length;
                return;
            }
            var needed = _windowCount + length - capacity;
            _windowStart = (_windowStart + needed) % capacity;
            _windowCount -= needed;
            AppendWindow(source, length);
        }

        private byte WindowByte(long absolute)
        {
            var oldest = _blockStart - _windowCount;
            if (absolute < oldest || absolute >= _blockStart) throw Corrupt("match offset");
            var index = (int)(absolute - oldest);
            return _window[(_windowStart + index) % _windowCapacity];
        }

        private int DecodeCompressedBlock(int start, int end)
        {
            var cursor = start;
            var literals = DecodeLiterals(ref cursor, end);
            if (cursor > end) throw Corrupt("literals overflow");
            if (end - cursor < 1) throw Corrupt("missing sequence header");
            var byte0 = _data[cursor++];
            int sequenceCount;
            if (byte0 == 0)
            {
                return CopyRemainingLiterals(literals, 0);
            }
            if (byte0 < 128)
            {
                sequenceCount = byte0;
            }
            else if (byte0 < 255)
            {
                if (cursor >= end) throw Corrupt("truncated sequence count");
                sequenceCount = ((byte0 - 128) << 8) + _data[cursor++];
            }
            else
            {
                if (end - cursor < 2) throw Corrupt("truncated sequence count");
                sequenceCount = _data[cursor] + (_data[cursor + 1] << 8) + 0x7F00;
                cursor += 2;
            }
            if (sequenceCount <= 0) throw Corrupt("bad sequence count");
            if (cursor >= end) throw Corrupt("missing sequence modes");
            var modes = _data[cursor++];
            if ((modes & 3) != 0) throw Corrupt("reserved sequence bits");
            var llTable = DecodeSequenceTable(ref cursor, end, (modes >> 6) & 3, SequenceKind.Literals);
            var ofTable = DecodeSequenceTable(ref cursor, end, (modes >> 4) & 3, SequenceKind.Offsets);
            var mlTable = DecodeSequenceTable(ref cursor, end, (modes >> 2) & 3, SequenceKind.Matches);
            _llTable = llTable;
            _ofTable = ofTable;
            _mlTable = mlTable;

            if (cursor >= end) throw Corrupt("missing sequence bitstream");
            var bits = new BitDStream();
            bits.Init(_data, cursor, end - cursor);

            var llState = (int)bits.ReadBits(llTable.TableLog);
            var ofState = (int)bits.ReadBits(ofTable.TableLog);
            var mlState = (int)bits.ReadBits(mlTable.TableLog);
            bits.Reload();
            var blockLength = 0;
            var literalPosition = 0;

            for (var index = 0; index < sequenceCount; index++)
            {
                if (llState >= llTable.Entries.Length || ofState >= ofTable.Entries.Length || mlState >= mlTable.Entries.Length)
                    throw Corrupt("fse state out of range");
                var ll = llTable.Entries[llState];
                var of = ofTable.Entries[ofState];
                var ml = mlTable.Entries[mlState];
                var llCode = ll.Symbol;
                var ofCode = of.Symbol;
                var mlCode = ml.Symbol;
                if (llCode > 35 || mlCode > 52 || ofCode > 31) throw Corrupt("sequence code out of range");

                var ll0 = llCode == 0;
                int offset;
                if (ofCode >= 2)
                {
                    offset = Offsets.Base[ofCode] + (int)bits.ReadBits(ofCode);
                    _rep2 = _rep1;
                    _rep1 = _rep0;
                    _rep0 = offset;
                }
                else if (ofCode == 0)
                {
                    if (ll0)
                    {
                        offset = _rep1;
                        _rep1 = _rep0;
                        _rep0 = offset;
                    }
                    else
                    {
                        offset = _rep0;
                    }
                }
                else
                {
                    var code = 1 + (ll0 ? 1 : 0) + (int)bits.ReadBits(1);
                    var resolved = code == 3 ? _rep0 - 1 : code == 1 ? _rep1 : _rep2;
                    if (resolved <= 0) throw Corrupt("bad repeat offset");
                    if (code != 1) _rep2 = _rep1;
                    _rep1 = _rep0;
                    _rep0 = resolved;
                    offset = resolved;
                }

                bits.Reload();
                var matchLength = MatchLengths.Base[mlCode] + (int)bits.ReadBits(MatchLengths.Bits[mlCode]);
                var literalLength = Literals.Base[llCode] + (int)bits.ReadBits(Literals.Bits[llCode]);
                bits.Reload();
                if (literalLength < 0 || literalPosition + literalLength > literals.Length)
                    throw Corrupt("literal overrun");
                if (blockLength + literalLength + matchLength > BlockLimit) throw Corrupt("block overrun");
                Array.Copy(literals, literalPosition, _block, blockLength, literalLength);
                literalPosition += literalLength;
                blockLength += literalLength;

                if (offset <= 0 || offset > _windowCapacity || offset > _frameOutput + blockLength)
                    throw Corrupt("offset before frame");
                var destination = _blockStart + blockLength;
                for (var i = 0; i < matchLength; i++)
                {
                    var source = destination + i - offset;
                    var value = source >= _blockStart
                        ? _block[(int)(source - _blockStart)]
                        : WindowByte(source);
                    _block[blockLength + i] = value;
                }
                blockLength += matchLength;

                if (index + 1 < sequenceCount)
                {
                    llState = ll.NewState + (int)bits.ReadBits(ll.NbBits);
                    mlState = ml.NewState + (int)bits.ReadBits(ml.NbBits);
                    ofState = of.NewState + (int)bits.ReadBits(of.NbBits);
                }
                bits.Reload();
            }

            if (literalPosition > literals.Length) throw Corrupt("literal overrun");
            var remaining = literals.Length - literalPosition;
            if (blockLength + remaining > BlockLimit) throw Corrupt("block overrun");
            Array.Copy(literals, literalPosition, _block, blockLength, remaining);
            blockLength += remaining;
            return blockLength;
        }

        private int CopyRemainingLiterals(byte[] literals, int consumed)
        {
            var remaining = literals.Length - consumed;
            if (remaining > BlockLimit) throw Corrupt("block overrun");
            Array.Copy(literals, consumed, _block, 0, remaining);
            return remaining;
        }

        private enum SequenceKind
        {
            Literals,
            Offsets,
            Matches,
        }

        private FseTable DecodeSequenceTable(ref int cursor, int end, int mode, SequenceKind kind)
        {
            var maxSymbol = kind switch { SequenceKind.Literals => 35, SequenceKind.Matches => 52, _ => 31 };
            var maxLog = kind == SequenceKind.Offsets ? 8 : 9;
            switch (mode)
            {
                case 0:
                    return kind switch
                    {
                        SequenceKind.Literals => FseTable.PredefinedLiterals,
                        SequenceKind.Matches => FseTable.PredefinedMatches,
                        _ => FseTable.PredefinedOffsets,
                    };
                case 1:
                    if (cursor >= end) throw Corrupt("truncated rle table");
                    var symbol = _data[cursor++];
                    if (symbol > maxSymbol) throw Corrupt("rle symbol out of range");
                    return FseTable.Rle(symbol);
                case 2:
                    var parsed = ParseFseTable(_data, cursor, end, maxSymbol, maxLog);
                    cursor = parsed.Next;
                    return parsed.Table;
                default:
                    var previous = kind switch
                    {
                        SequenceKind.Literals => _llTable,
                        SequenceKind.Matches => _mlTable,
                        _ => _ofTable,
                    };
                    if (previous is null) throw Corrupt("repeat table without predecessor");
                    return previous;
            }
        }

        private byte[] DecodeLiterals(ref int cursor, int end)
        {
            if (cursor >= end) throw Corrupt("missing literals header");
            var first = _data[cursor];
            var type = first & 3;
            var format = (first >> 2) & 3;
            int headerSize;
            int regenerated;
            int compressed;
            if (type is 0 or 1)
            {
                switch (format)
                {
                    case 0 or 2:
                        headerSize = 1;
                        regenerated = first >> 3;
                        break;
                    case 1:
                        if (end - cursor < 2) throw Corrupt("truncated literals header");
                        headerSize = 2;
                        regenerated = (first >> 4) | (_data[cursor + 1] << 4);
                        break;
                    default:
                        if (end - cursor < 3) throw Corrupt("truncated literals header");
                        headerSize = 3;
                        regenerated = (first >> 4) | (_data[cursor + 1] << 4) | (_data[cursor + 2] << 12);
                        break;
                }
                compressed = type == 0 ? regenerated : 1;
            }
            else
            {
                switch (format)
                {
                    case 0 or 1:
                        if (end - cursor < 3) throw Corrupt("truncated literals header");
                        headerSize = 3;
                        regenerated = (first >> 4) | ((_data[cursor + 1] & 0x3F) << 4);
                        compressed = (_data[cursor + 1] >> 6) | (_data[cursor + 2] << 2);
                        break;
                    case 2:
                        if (end - cursor < 4) throw Corrupt("truncated literals header");
                        headerSize = 4;
                        regenerated = (first >> 4) | (_data[cursor + 1] << 4) | ((_data[cursor + 2] & 3) << 12);
                        compressed = (_data[cursor + 2] >> 2) | (_data[cursor + 3] << 6);
                        break;
                    default:
                        if (end - cursor < 5) throw Corrupt("truncated literals header");
                        headerSize = 5;
                        regenerated = (first >> 4) | (_data[cursor + 1] << 4) | ((_data[cursor + 2] & 0x3F) << 12);
                        compressed = (_data[cursor + 2] >> 6) | (_data[cursor + 3] << 2) | ((_data[cursor + 4] & 0xFF) << 10);
                        break;
                }
            }
            if (regenerated < 0 || regenerated > BlockLimit) throw Corrupt("literals too large");
            var content = cursor + headerSize;
            if (compressed < 0 || content + compressed > end) throw Corrupt("literals overflow");
            cursor = content + compressed;
            var literals = new byte[regenerated];
            switch (type)
            {
                case 0:
                    Array.Copy(_data, content, literals, 0, regenerated);
                    return literals;
                case 1:
                    Array.Fill(literals, _data[content]);
                    return literals;
            }

            var table = _huffman;
            var streamStart = content;
            if (type == 2)
            {
                var parsed = ParseHuffmanTree(_data, content, content + compressed);
                table = parsed.Table;
                _huffman = table;
                streamStart = parsed.Next;
            }
            if (table is null) throw Corrupt("treeless literals without table");
            var streamLength = content + compressed - streamStart;

            if (format == 0)
            {
                DecodeHuffmanStream(table, streamStart, streamStart + streamLength, literals, 0, regenerated);
            }
            else
            {
                if (streamLength < 6) throw Corrupt("truncated jump table");
                var s1 = ReadU16(_data, streamStart);
                var s2 = ReadU16(_data, streamStart + 2);
                var s3 = ReadU16(_data, streamStart + 4);
                var s4 = streamLength - 6 - s1 - s2 - s3;

                if (s4 < 0) throw Corrupt("bad jump table");
                var segment = (regenerated + 3) / 4;
                var last = regenerated - 3 * segment;
                if (last < 0) throw Corrupt("bad four-stream size");
                var p1 = streamStart + 6;
                var p2 = p1 + s1;
                var p3 = p2 + s2;
                var p4 = p3 + s3;
                var end4 = streamStart + streamLength;
                if (p4 > end4) throw Corrupt("bad jump table");
                DecodeHuffmanStream(table, p1, p2, literals, 0, segment);
                DecodeHuffmanStream(table, p2, p3, literals, segment, segment);
                DecodeHuffmanStream(table, p3, p4, literals, 2 * segment, segment);
                DecodeHuffmanStream(table, p4, end4, literals, 3 * segment, last);
            }
            return literals;
        }

        private void DecodeHuffmanStream(HuffmanTable table, int start, int end, byte[] output, int offset, int count)
        {
            if (count == 0) return;
            if (start < 0 || end < start) throw Corrupt("bad huffman stream");
            var bits = new BitDStream();
            bits.Init(_data, start, end - start);
            for (var i = 0; i < count; i++)
            {
                var entry = table.Entries[(int)bits.LookBits(table.TableLog)];
                bits.SkipBits(entry.NbBits);
                output[offset + i] = entry.Symbol;
                bits.Reload();
            }
        }

    }

    private readonly record struct FseEntry(int Symbol, int NbBits, int NewState);
    private readonly record struct HuffmanEntry(byte Symbol, int NbBits);

    private sealed class FseTable
    {
        public int TableLog { get; }
        public FseEntry[] Entries { get; }

        public static readonly FseTable PredefinedLiterals = Build(
            [4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1,
             2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1,
             -1, -1, -1, -1], 6);

        public static readonly FseTable PredefinedMatches = Build(
            [1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
             1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
             1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1,
             -1, -1, -1, -1, -1], 6);

        public static readonly FseTable PredefinedOffsets = Build(
            [1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
             1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1], 5);

        private FseTable(int tableLog, FseEntry[] entries)
        {
            TableLog = tableLog;
            Entries = entries;
        }

        public static FseTable Rle(int symbol) => new(0, [new FseEntry(symbol, 0, 0)]);

        public static FseTable Build(int[] normalized, int tableLog)
        {
            var tableSize = 1 << tableLog;
            var maxSymbol = normalized.Length - 1;
            var symbols = new int[tableSize];
            var next = new int[maxSymbol + 1];
            var highThreshold = tableSize - 1;
            for (var symbol = 0; symbol <= maxSymbol; symbol++)
            {
                var count = normalized[symbol];
                if (count == -1)
                {
                    symbols[highThreshold--] = symbol;
                    next[symbol] = 1;
                }
                else
                {
                    if (count < 0) throw Corrupt("bad normalized count");
                    next[symbol] = count;
                }
            }
            var step = (tableSize >> 1) + (tableSize >> 3) + 3;
            var position = 0;
            for (var symbol = 0; symbol <= maxSymbol; symbol++)
            {
                var count = normalized[symbol];
                for (var i = 0; i < count; i++)
                {
                    symbols[position] = symbol;
                    position = (position + step) & (tableSize - 1);
                    while (position > highThreshold) position = (position + step) & (tableSize - 1);
                }
            }
            var entries = new FseEntry[tableSize];
            for (var state = 0; state < tableSize; state++)
            {
                var symbol = symbols[state];
                var nextState = next[symbol]++;
                if (nextState <= 0) throw Corrupt("bad fse state");
                var nbBits = tableLog - HighBit(nextState);
                var newState = (nextState << nbBits) - tableSize;
                entries[state] = new FseEntry(symbol, nbBits, newState);
            }
            return new FseTable(tableLog, entries);
        }

        public static FseTable BuildUnchecked(int[] normalized, int tableLog) => Build(normalized, tableLog);
    }

    private sealed class HuffmanTable
    {
        public int TableLog { get; }
        public HuffmanEntry[] Entries { get; }

        public HuffmanTable(int tableLog, HuffmanEntry[] entries)
        {
            TableLog = tableLog;
            Entries = entries;
        }
    }

    private static (FseTable Table, int Next) ParseFseTable(byte[] data, int start, int end, int maxSymbol, int maxLog)
    {
        var reader = new ForwardBits(data, start, end);
        var accuracyLog = reader.Read(4) + 5;
        if (accuracyLog > maxLog) throw Corrupt("fse accuracy too large");
        var remaining = (1 << accuracyLog) + 1;
        var threshold = 1 << accuracyLog;
        var nbBits = accuracyLog + 1;
        var counts = new short[256];
        var symbol = 0;
        var previous0 = false;
        while (remaining > 1)
        {
            if (symbol > maxSymbol) throw Corrupt("too many fse symbols");
            if (previous0)
            {
                var n0 = symbol;
                while (reader.Peek(16) == 0xFFFF)
                {
                    n0 += 24;
                    reader.Skip(16);
                }
                while (reader.Peek(2) == 3)
                {
                    n0 += 3;
                    reader.Skip(2);
                }
                n0 += reader.Peek(2);
                reader.Skip(2);
                if (n0 > maxSymbol) throw Corrupt("zero run out of range");
                while (symbol < n0) counts[symbol++] = 0;
            }
            var max = (2 * threshold - 1) - remaining;
            int count;

            if (reader.Peek(nbBits - 1) < max)
            {
                count = reader.Read(nbBits - 1);
            }
            else
            {
                count = reader.Read(nbBits);
                if (count >= threshold) count -= max;
            }
            count--;
            if (count < -1) throw Corrupt("bad fse count");
            remaining -= count < 0 ? -count : count;
            if (remaining <= 0) throw Corrupt("fse sum overflow");
            counts[symbol++] = (short)count;
            previous0 = count == 0;
            while (remaining < threshold)
            {
                nbBits--;
                threshold >>= 1;
            }
            if (nbBits < 1) throw Corrupt("bad fse bit width");
        }
        if (remaining != 1) throw Corrupt("incomplete fse table");
        var maxSymbolValue = symbol - 1;
        if (maxSymbolValue < 0) throw Corrupt("empty fse table");
        var used = (reader.BitPosition - start * 8 + 7) / 8;
        var table = FseTable.BuildUnchecked(counts.Take(maxSymbolValue + 1).Select(value => (int)value).ToArray(), accuracyLog);
        return (table, start + used);
    }

    private static (HuffmanTable Table, int Next) ParseHuffmanTree(byte[] data, int start, int end)
    {
        if (start >= end) throw Corrupt("missing huffman header");
        var cursor = start;
        var header = data[cursor++];
        var weights = new List<byte>(256);
        if (header >= 128)
        {
            var count = header - 127;
            var bytes = (count + 1) / 2;
            if (cursor + bytes > end) throw Corrupt("truncated huffman weights");
            for (var i = 0; i < count; i++)
            {
                var value = data[cursor + i / 2];
                weights.Add((byte)((i % 2 == 0) ? value >> 4 : value & 15));
            }
            cursor += bytes;
        }
        else
        {
            var compressed = header;
            if (cursor + compressed > end) throw Corrupt("truncated huffman weights");
            var fseEnd = cursor + compressed;
            var parsed = ParseFseTable(data, cursor, fseEnd, 255, 6);
            var table = parsed.Table;
            if (parsed.Next >= fseEnd) throw Corrupt("missing weight bitstream");
            var bits = new BitDStream();
            bits.Init(data, parsed.Next, fseEnd - parsed.Next);
            var state1 = (int)bits.ReadBits(table.TableLog);
            var state2 = (int)bits.ReadBits(table.TableLog);
            var firstReload = bits.Reload();
            if (firstReload == BitDStream.Status.Overflow) throw Corrupt("weight stream overflow");
            while (true)
            {
                if (weights.Count >= 255) throw Corrupt("too many huffman weights");
                var entry1 = table.Entries[state1];
                weights.Add((byte)entry1.Symbol);
                state1 = entry1.NewState + (int)bits.ReadBits(entry1.NbBits);
                if (bits.Reload() == BitDStream.Status.Overflow)
                {
                    var entry2 = table.Entries[state2];
                    weights.Add((byte)entry2.Symbol);
                    break;
                }
                if (weights.Count >= 255) throw Corrupt("too many huffman weights");
                var entry2b = table.Entries[state2];
                weights.Add((byte)entry2b.Symbol);
                state2 = entry2b.NewState + (int)bits.ReadBits(entry2b.NbBits);
                if (bits.Reload() == BitDStream.Status.Overflow)
                {
                    var entry1b = table.Entries[state1];
                    weights.Add((byte)entry1b.Symbol);
                    break;
                }
            }
            cursor = fseEnd;
        }

        var rank = new int[12];
        var weightTotal = 0;
        foreach (var weight in weights)
        {
            if (weight > 11) throw Corrupt("huffman weight too large");
            rank[weight]++;
            if (weight > 0) weightTotal += 1 << (weight - 1);
        }
        if (weightTotal == 0) throw Corrupt("empty huffman tree");
        var tableLog = HighBit(weightTotal) + 1;
        if (tableLog > 11) throw Corrupt("huffman table too deep");
        var total = 1 << tableLog;
        var rest = total - weightTotal;
        if (rest <= 0 || (rest & (rest - 1)) != 0) throw Corrupt($"bad huffman tree weights={weights.Count} total={weightTotal} tableLog={tableLog} rest={rest}");
        var lastWeight = HighBit(rest) + 1;
        if (lastWeight > 11) throw Corrupt("bad huffman last weight");
        weights.Add((byte)lastWeight);
        rank[lastWeight]++;
        if (rank[1] < 2 || (rank[1] & 1) != 0) throw Corrupt("invalid huffman ranks");

        var starts = new int[12];
        var nextStart = 0;
        for (var weight = 1; weight <= tableLog; weight++)
        {
            starts[weight] = nextStart;
            nextStart += rank[weight] * (1 << (weight - 1));
        }
        if (nextStart != total) throw Corrupt("huffman rank mismatch");
        var entries = new HuffmanEntry[total];
        for (var symbol = 0; symbol < weights.Count; symbol++)
        {
            var weight = weights[symbol];
            if (weight == 0) continue;
            if (weight > tableLog) throw Corrupt("huffman weight exceeds table");
            var length = 1 << (weight - 1);
            var nbBits = tableLog + 1 - weight;
            var position = starts[weight];
            for (var i = 0; i < length; i++)
            {
                entries[position + i] = new HuffmanEntry((byte)symbol, nbBits);
            }
            starts[weight] += length;
        }
        foreach (var entry in entries)
        {
            if (entry.NbBits == 0) throw Corrupt("incomplete huffman table");
        }
        return (new HuffmanTable(tableLog, entries), cursor);
    }

    private static DshFormatException Corrupt(string message) => new(message);

    private static int HighBit(int value)
    {
        var bit = 0;
        while (value > 1)
        {
            value >>= 1;
            bit++;
        }
        return bit;
    }

    private static int ReadU16(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8);

    private static uint ReadU32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static ulong ReadU64(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));

    private static ulong ReadLE64(byte[] data, int offset)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++) value |= (ulong)data[offset + i] << (8 * i);
        return value;
    }

    private static class Literals
    {
        public static readonly int[] Base =
        [
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 0x80, 0x100, 0x200, 0x400,
            0x800, 0x1000, 0x2000, 0x4000, 0x8000, 0x10000,
        ];

        public static readonly int[] Bits =
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12,
            13, 14, 15, 16,
        ];
    }

    private static class MatchLengths
    {
        public static readonly int[] Base =
        [
            3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
            35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 0x83, 0x103, 0x203,
            0x403, 0x803, 0x1003, 0x2003, 0x4003, 0x8003, 0x10003,
        ];

        public static readonly int[] Bits =
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11,
            12, 13, 14, 15, 16,
        ];
    }

    private static class Offsets
    {
        public static readonly int[] Base =
        [
            0, 1, 1, 5, 0xD, 0x1D, 0x3D, 0x7D, 0xFD, 0x1FD, 0x3FD, 0x7FD,
            0xFFD, 0x1FFD, 0x3FFD, 0x7FFD, 0xFFFD, 0x1FFFD, 0x3FFFD, 0x7FFFD,
            0xFFFFD, 0x1FFFFD, 0x3FFFFD, 0x7FFFFD, 0xFFFFFD, 0x1FFFFFD, 0x3FFFFFD,
            0x7FFFFFD, 0xFFFFFFD, 0x1FFFFFFD, 0x3FFFFFFD, 0x7FFFFFFD,
        ];
    }

    private sealed class XxHash64
    {
        private const ulong Prime1 = 11400714785074694791UL;
        private const ulong Prime2 = 14029467366897019727UL;
        private const ulong Prime3 = 1609587929392839161UL;
        private const ulong Prime4 = 9650029242287828579UL;
        private const ulong Prime5 = 2870177450012600261UL;

        private readonly byte[] _memory = new byte[32];
        private ulong _v1 = unchecked(Prime1 + Prime2);
        private ulong _v2 = Prime2;
        private ulong _v3;
        private ulong _v4 = unchecked(0UL - Prime1);
        private ulong _total;
        private int _memorySize;

        public void Update(ReadOnlySpan<byte> data)
        {
            _total += (ulong)data.Length;
            if (_memorySize + data.Length < 32)
            {
                data.CopyTo(_memory.AsSpan(_memorySize));
                _memorySize += data.Length;
                return;
            }

            if (_memorySize > 0)
            {
                var fill = 32 - _memorySize;
                data[..fill].CopyTo(_memory.AsSpan(_memorySize));
                ProcessStripe(_memory);
                data = data[fill..];
                _memorySize = 0;
            }

            while (data.Length >= 32)
            {
                ProcessStripe(data[..32]);
                data = data[32..];
            }

            data.CopyTo(_memory);
            _memorySize = data.Length;
        }

        public ulong Digest()
        {
            ulong hash;
            if (_total >= 32)
            {
                hash = Rotate(_v1, 1) + Rotate(_v2, 7) + Rotate(_v3, 12) + Rotate(_v4, 18);
                hash = Merge(hash, _v1);
                hash = Merge(hash, _v2);
                hash = Merge(hash, _v3);
                hash = Merge(hash, _v4);
            }
            else
            {
                hash = Prime5;
            }

            hash += _total;
            var index = 0;
            while (_memorySize - index >= 8)
            {
                hash ^= Round(0, Read64(_memory, index));
                hash = unchecked(Rotate(hash, 27) * Prime1 + Prime4);
                index += 8;
            }
            if (_memorySize - index >= 4)
            {
                hash ^= unchecked(Read32(_memory, index) * Prime1);
                hash = unchecked(Rotate(hash, 23) * Prime2 + Prime3);
                index += 4;
            }
            while (index < _memorySize)
            {
                hash ^= unchecked(_memory[index] * Prime5);
                hash = unchecked(Rotate(hash, 11) * Prime1);
                index++;
            }

            hash ^= hash >> 33;
            hash = unchecked(hash * Prime2);
            hash ^= hash >> 29;
            hash = unchecked(hash * Prime3);
            hash ^= hash >> 32;
            return hash;
        }

        private void ProcessStripe(ReadOnlySpan<byte> stripe)
        {
            _v1 = Round(_v1, Read64(stripe, 0));
            _v2 = Round(_v2, Read64(stripe, 8));
            _v3 = Round(_v3, Read64(stripe, 16));
            _v4 = Round(_v4, Read64(stripe, 24));
        }

        private static ulong Round(ulong accumulator, ulong input)
        {
            accumulator = unchecked(accumulator + input * Prime2);
            accumulator = Rotate(accumulator, 31);
            return unchecked(accumulator * Prime1);
        }

        private static ulong Merge(ulong hash, ulong value)
        {
            hash ^= Round(0, value);
            return unchecked(hash * Prime1 + Prime4);
        }

        private static ulong Rotate(ulong value, int bits) =>
            (value << bits) | (value >> (64 - bits));

        private static ulong Read64(ReadOnlySpan<byte> data, int offset)
        {
            ulong value = 0;
            for (var i = 0; i < 8; i++) value |= (ulong)data[offset + i] << (8 * i);
            return value;
        }

        private static uint Read32(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) |
                   (data[offset + 3] << 24));
    }

    private sealed class ForwardBits
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _bitPosition;

        public ForwardBits(byte[] data, int start, int end)
        {
            _data = data;
            _bitPosition = start * 8;
            _end = end * 8;
        }

        public int BitPosition => _bitPosition;

        public int Peek(int count)
        {
            var value = 0;
            var position = _bitPosition;
            for (var i = 0; i < count; i++)
            {
                var bit = 0;
                var index = position >> 3;
                if (index < _data.Length && position < _end)
                    bit = (_data[index] >> (position & 7)) & 1;
                value |= bit << i;
                position++;
            }
            return value;
        }

        public int Read(int count)
        {
            var value = Peek(count);
            _bitPosition += count;
            return value;
        }

        public void Skip(int count) => _bitPosition += count;
    }

    private struct BitDStream
    {
        public enum Status
        {
            Unfinished,
            EndOfBuffer,
            Completed,
            Overflow,
        }

        private byte[] _data;
        private int _start;
        private int _ptr;
        private int _limit;
        private ulong _container;
        private uint _consumed;

        public void Init(byte[] data, int start, int size)
        {
            if (size < 1) throw Corrupt("empty bitstream");
            _data = data;
            _start = start;
            _limit = start + 8;
            var last = data[start + size - 1];
            if (last == 0) throw Corrupt("zero terminal byte");
            var padding = 8 - HighBit(last);
            if (size >= 8)
            {
                _ptr = start + size - 8;
                _container = ReadLE64(data, _ptr);
                _consumed = (uint)padding;
            }
            else
            {
                _ptr = start;
                ulong container = 0;
                for (var i = 0; i < size; i++) container |= (ulong)data[start + i] << (8 * i);
                _container = container;
                _consumed = (uint)(padding + (8 - size) * 8);
            }
        }

        public ulong LookBits(int count)
        {
            if (count == 0) return 0;
            return (_container << (int)(_consumed & 63)) >> (64 - count);
        }

        public ulong ReadBits(int count)
        {
            var value = LookBits(count);
            _consumed += (uint)count;
            return value;
        }

        public void SkipBits(int count) => _consumed += (uint)count;

        public Status Reload()
        {
            if (_consumed > 64) return Status.Overflow;
            if (_ptr >= _limit)
            {
                _ptr -= (int)(_consumed >> 3);
                _consumed &= 7;
                _container = ReadLE64(_data, _ptr);
                return Status.Unfinished;
            }
            if (_ptr == _start)
            {
                return _consumed < 64 ? Status.EndOfBuffer : Status.Completed;
            }
            var bytes = (int)(_consumed >> 3);
            var status = Status.Unfinished;
            if (_ptr - bytes < _start)
            {
                bytes = _ptr - _start;
                status = Status.EndOfBuffer;
            }
            _ptr -= bytes;
            _consumed -= (uint)(bytes * 8);
            _container = ReadLE64(_data, _ptr);
            return status;
        }
    }
}
