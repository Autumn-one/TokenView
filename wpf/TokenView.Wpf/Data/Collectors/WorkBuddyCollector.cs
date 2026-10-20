using System.IO;
using System.Text.Json;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>WorkBuddy 项目转录（`~/.workbuddy/projects`）；只读取元数据。</summary>
public sealed class WorkBuddyCollector : IClientCollector
{
    private const int ParserVersion = 1;
    private const string Client = "workbuddy";

    private static readonly HashSet<string> RecognizedTypes = new(StringComparer.Ordinal)
    {
        "message", "assistant", "user", "function_call", "reasoning",
        "function_call_result", "function_call_output",
    };

    private static readonly HashSet<string> IgnoredTypes = new(StringComparer.Ordinal)
    {
        "file-history-snapshot", "ai-title", "custom-title", "summary",
    };

    private readonly string? _root;

    public WorkBuddyCollector() : this(DefaultRoot())
    {
    }

    public WorkBuddyCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    public static string? DefaultRoot()
    {
        var configured = CollectorSupport.EnvPath("WORKBUDDY_CONFIG_DIR");
        var root = configured ?? CollectorSupport.Combine(CollectorSupport.HomeDir(), ".workbuddy");
        return CollectorSupport.Combine(root, "projects");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_root is null || !Directory.Exists(_root))
        {
            if (_root is not null && File.Exists(_root))
            {
                throw SyncException.Directory(new IOException("WorkBuddy root is not a directory"));
            }
            report.RootMissing = true;
            return report;
        }

        var paths = CollectorSupport.JsonlFiles(_root, report);
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
        unchanged &= store.SourcePaths(Client)
            .SequenceEqual(sources.Select(source => source.Path), StringComparer.Ordinal);
        if (unchanged)
        {
            report.FilesSkippedUnchanged = paths.Count;
            return report;
        }

        var sessions = new SortedDictionary<string, Rollup>(StringComparer.Ordinal);
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var parsed = ParseFile(path);
            report.FilesParsed++;
            report.UnparsedLines += parsed.Unparsed;
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
            var (size, mtime) = Fingerprint(paths[index]);
            if (sources[index].SizeBytes != size || sources[index].MtimeMs != mtime)
            {
                throw SyncException.ChangedDuringScan();
            }
        }

        var partial = report.UnparsedLines > 0;
        var imports = sessions.Select(pair => pair.Value.Finish(pair.Key, partial)).ToList();
        ImportSummary imported;
        try
        {
            imported = store.ReplaceClientSnapshot(Client, sources, imports);
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or
                                          ArgumentException or InvalidOperationException)
        {
            throw SyncException.Store(error);
        }
        report.SessionsImported = imports.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
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

    private static ParsedFile ParseFile(string path)
    {
        var parsed = new ParsedFile();
        var recognized = false;
        var nonEmpty = false;
        try
        {
            CollectorSupport.ReadJsonLines(path, (line, complete) =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                nonEmpty = true;

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
                    if (!TryReadRecord(document.RootElement, out var record))
                    {
                        if (!complete)
                        {
                            throw SyncException.FromSource(
                                new InvalidDataException("incomplete WorkBuddy transcript record"));
                        }
                        parsed.Unparsed++;
                        return;
                    }

                    var kind = record.Kind;
                    if (kind is not null && RecognizedTypes.Contains(kind))
                    {
                        recognized = true;
                        if (string.IsNullOrEmpty(record.SessionId))
                        {
                            parsed.Unparsed++;
                            return;
                        }
                        if (record.RawCounts is { } rawCounts)
                        {
                            if (string.IsNullOrEmpty(record.Id) ||
                                kind is not ("message" or "assistant" or "function_call") ||
                                string.Equals(record.Role, "user", StringComparison.Ordinal) ||
                                record.HasAggregateUsage ||
                                record.HasInvalidDetails ||
                                !ValidCounts(rawCounts))
                            {
                                parsed.Unparsed++;
                                return;
                            }
                            record.Counts = ToRaw(rawCounts);
                        }
                        parsed.Records.Add(record);
                    }
                    else if (kind is not null && IgnoredTypes.Contains(kind))
                    {
                        recognized = true;
                    }
                    else
                    {
                        parsed.Unparsed++;
                    }
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }

        if (nonEmpty && (!recognized || (parsed.Unparsed > 0 && parsed.Records.Count == 0)))
        {
            throw SyncException.FromSource(
                new InvalidDataException("unsupported WorkBuddy transcript format"));
        }
        return parsed;
    }

    private static bool TryReadRecord(JsonElement element, out ParsedRecord record)
    {
        record = null!;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!TryReadOptionalString(element, "type", out var kind) ||
            !TryReadOptionalString(element, "id", out var id) ||
            !TryReadOptionalString(element, "sessionId", out var sessionId) ||
            !TryReadOptionalInt64(element, "timestamp", out var timestamp) ||
            !TryReadOptionalString(element, "cwd", out var cwd) ||
            !TryReadOptionalString(element, "role", out var role) ||
            !TryReadOptionalBool(element, "isSidechain", out var isSidechain) ||
            !TryReadOptionalObject(element, "providerData", out var provider) ||
            !TryReadOptionalObject(element, "message", out var message))
        {
            return false;
        }

        string? model = null;
        string? agent = null;
        NativeUsage? providerUsage = null;
        if (provider is { } providerElement)
        {
            if (!TryReadOptionalString(providerElement, "model", out model) ||
                !TryReadOptionalString(providerElement, "agent", out agent) ||
                !TryReadOptionalUsage(providerElement, "usage", out providerUsage))
            {
                return false;
            }
        }

        NativeUsage? messageUsage = null;
        if (message is { } messageElement &&
            !TryReadOptionalUsage(messageElement, "usage", out messageUsage))
        {
            return false;
        }

        record = new ParsedRecord(kind, id, sessionId, timestamp, cwd, role, isSidechain,
            model, agent, providerUsage, messageUsage);
        return true;
    }

    private static bool TryReadOptionalUsage(JsonElement parent, string name, out NativeUsage? usage)
    {
        usage = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (element.ValueKind != JsonValueKind.Object) return false;
        return TryReadUsage(element, out usage);
    }

    private static bool TryReadUsage(JsonElement element, out NativeUsage? usage)
    {
        usage = null;
        if (!TryReadOptionalU64(element, "requests", out var requests) ||
            !TryReadOptionalU64(element, "inputTokens", "input_tokens", out var input) ||
            !TryReadOptionalU64(element, "outputTokens", "output_tokens", out var output) ||
            !TryReadOptionalU64(element, "totalTokens", "total_tokens", out var total) ||
            !TryReadOptionalU64(element, "cacheReadInputTokens", "cache_read_input_tokens",
                out var cacheRead) ||
            !TryReadOptionalU64(element, "cacheCreationInputTokens",
                "cache_creation_input_tokens", out var cacheWrite) ||
            !TryReadOptionalU64(element, "reasoningTokens", "reasoning_tokens", out var reasoning) ||
            !TryReadOptionalDetails(element, "inputTokensDetails", out var inputDetails) ||
            !TryReadOptionalDetails(element, "outputTokensDetails", out var outputDetails))
        {
            return false;
        }

        usage = new NativeUsage(requests, input, output, total, cacheRead, cacheWrite, reasoning,
            inputDetails, outputDetails);
        return true;
    }

    private static bool TryReadOptionalDetails(JsonElement parent, string name, out Details? details)
    {
        details = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (!TryReadDetail(element, out var single)) return false;
                details = new Details([single]);
                return true;
            case JsonValueKind.Array:
            {
                var values = new List<Detail>();
                foreach (var item in element.EnumerateArray())
                {
                    if (!TryReadDetail(item, out var detail)) return false;
                    values.Add(detail);
                }
                details = new Details(values);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryReadDetail(JsonElement element, out Detail detail)
    {
        detail = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!TryReadOptionalU64(element, "cached_tokens", out var cached) ||
            !TryReadOptionalU64(element, "reasoning_tokens", out var reasoning))
        {
            return false;
        }
        detail = new Detail(cached, reasoning);
        return true;
    }

    private static bool TryReadOptionalObject(JsonElement parent, string name, out JsonElement? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element)) return true;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Object:
                value = element;
                return true;
            default:
                return false;
        }
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

    private static bool TryReadOptionalBool(JsonElement parent, string name, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(name, out var element)) return true;
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = element.GetBoolean();
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

    private static bool TryReadOptionalU64(JsonElement parent, string name, out ulong? value)
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

    private static bool TryReadOptionalU64(JsonElement parent, string name, string alias,
        out ulong? value)
    {
        value = null;
        var hasPrimary = parent.TryGetProperty(name, out var primary);
        var hasAlias = parent.TryGetProperty(alias, out var aliasElement);
        if (hasPrimary && hasAlias) return false;
        var element = hasPrimary ? primary : aliasElement;
        if (!hasPrimary && !hasAlias) return true;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetUInt64(out var number))
        {
            return false;
        }
        value = number;
        return true;
    }

    private static bool ValidCounts(U64Counts counts)
    {
        ulong total = 0;
        foreach (var value in new[]
                 {
                     counts.Input, counts.Output, counts.CacheRead, counts.CacheWrite,
                     counts.Reasoning,
                 })
        {
            if (value is not { } number) continue;
            if (total > ulong.MaxValue - number) return false;
            total += number;
        }
        if (total > long.MaxValue) return false;
        return counts.ClientTotal is not { } clientTotal || clientTotal <= long.MaxValue;
    }

    private static RawTokenCounts ToRaw(U64Counts counts) => new(
        ToLong(counts.Input),
        ToLong(counts.Output),
        ToLong(counts.CacheRead),
        ToLong(counts.CacheWrite),
        ToLong(counts.Reasoning),
        ToLong(counts.ClientTotal));

    private static long? ToLong(ulong? value) => value is { } number ? (long)number : null;

    private readonly record struct Detail(ulong? CachedTokens, ulong? ReasoningTokens);

    private sealed class Details
    {
        private readonly List<Detail> _values;

        public Details(List<Detail> values)
        {
            _values = values;
        }

        public ulong? Sum(Func<Detail, ulong?> selector)
        {
            if (_values.Count == 1) return selector(_values[0]);
            if (_values.Count == 0) return null;
            ulong sum = 0;
            foreach (var value in _values)
            {
                if (selector(value) is not { } number) return null;
                if (sum > ulong.MaxValue - number) return null;
                sum += number;
            }
            return sum;
        }

        public bool Valid()
        {
            static ulong? CheckedSum(IReadOnlyList<Detail> values, Func<Detail, ulong?> selector)
            {
                ulong sum = 0;
                foreach (var value in values)
                {
                    if (selector(value) is not { } number) continue;
                    if (sum > ulong.MaxValue - number) return null;
                    sum += number;
                }
                return sum;
            }

            return CheckedSum(_values, detail => detail.CachedTokens) is { } cached &&
                   cached <= long.MaxValue &&
                   CheckedSum(_values, detail => detail.ReasoningTokens) is { } reasoning &&
                   reasoning <= long.MaxValue;
        }
    }

    private sealed record NativeUsage(
        ulong? Requests,
        ulong? Input,
        ulong? Output,
        ulong? ClientTotal,
        ulong? CacheRead,
        ulong? CacheWrite,
        ulong? Reasoning,
        Details? InputDetails,
        Details? OutputDetails)
    {
        public U64Counts Counts() => new(
            Input,
            Output,
            CacheRead ?? InputDetails?.Sum(detail => detail.CachedTokens),
            CacheWrite,
            Reasoning ?? OutputDetails?.Sum(detail => detail.ReasoningTokens),
            ClientTotal);

        public bool IsValidDetails =>
            (InputDetails?.Valid() ?? true) && (OutputDetails?.Valid() ?? true);
    }

    private readonly record struct U64Counts(
        ulong? Input,
        ulong? Output,
        ulong? CacheRead,
        ulong? CacheWrite,
        ulong? Reasoning,
        ulong? ClientTotal)
    {
        public static U64Counts Merge(U64Counts native, U64Counts fallback) => new(
            native.Input ?? fallback.Input,
            native.Output ?? fallback.Output,
            native.CacheRead ?? fallback.CacheRead,
            native.CacheWrite ?? fallback.CacheWrite,
            native.Reasoning ?? fallback.Reasoning,
            native.ClientTotal ?? fallback.ClientTotal);
    }

    private sealed class ParsedRecord
    {
        public ParsedRecord(string? kind, string? id, string? sessionId, long? timestamp,
            string? cwd, string? role, bool isSidechain, string? model, string? agent,
            NativeUsage? providerUsage, NativeUsage? messageUsage)
        {
            Kind = kind;
            Id = id;
            SessionId = sessionId;
            Timestamp = timestamp;
            Cwd = cwd;
            Role = role;
            IsSidechain = isSidechain;
            Model = model;
            Agent = agent;

            if (providerUsage is not null || messageUsage is not null)
            {
                var native = providerUsage?.Counts() ?? default;
                var fallback = messageUsage?.Counts() ?? default;
                RawCounts = U64Counts.Merge(native, fallback);
            }

            var aggregate = providerUsage ?? messageUsage;
            HasAggregateUsage = aggregate?.Requests is { } requests && requests != 1;
            HasInvalidDetails =
                (providerUsage is not null && !providerUsage.IsValidDetails) ||
                (messageUsage is not null && !messageUsage.IsValidDetails);
        }

        public string? Kind { get; }
        public string? Id { get; }
        public string? SessionId { get; }
        public long? Timestamp { get; }
        public string? Cwd { get; }
        public string? Role { get; }
        public bool IsSidechain { get; }
        public string? Model { get; }
        public string? Agent { get; }
        public U64Counts? RawCounts { get; }
        public RawTokenCounts? Counts { get; set; }
        public bool HasAggregateUsage { get; }
        public bool HasInvalidDetails { get; }
    }

    private sealed class ParsedFile
    {
        public List<ParsedRecord> Records { get; } = new();
        public int Unparsed { get; set; }
    }

    private sealed class Rollup
    {
        private readonly SortedDictionary<string, (ParsedRecord Record, RawTokenCounts Counts, string Path)>
            _requests = new(StringComparer.Ordinal);

        private string? _project;
        private long? _start;
        private long? _end;
        private string? _source;

        public void Push(ParsedRecord record, string path)
        {
            _source ??= path;
            if (record.Timestamp is { } time)
            {
                if (_start is null || time < _start) _start = time;
                if (_end is null || time > _end) _end = time;
            }
            if (_project is null) _project = record.Cwd;
            if (record.Counts is not { } counts || string.IsNullOrEmpty(record.Id)) return;

            if (!_requests.TryGetValue(record.Id, out var old) ||
                AtLeast(record.Timestamp, old.Record.Timestamp))
            {
                _requests[record.Id] = (record, counts, path);
            }
        }

        private static bool AtLeast(long? candidate, long? current) =>
            current is null || (candidate is { } value && value >= current.Value);

        public SessionImport Finish(string sessionId, bool partial)
        {
            var messages = new List<MessageRecord>(_requests.Count);
            var counts = new List<RawTokenCounts>(_requests.Count);
            var messageSourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, entry) in _requests)
            {
                var meta = new RequestMeta(key, entry.Record.Model, entry.Record.Agent,
                    entry.Record.Timestamp, null, entry.Record.IsSidechain, $"item:{key}");
                messages.Add(RequestBuilder.Request(Client, sessionId, meta, entry.Counts));
                counts.Add(entry.Counts);
                messageSourcePaths[key] = entry.Path;
            }

            var sessionMeta = new SessionMeta(sessionId, null, _project, _start, _end);
            var session = RequestBuilder.Session(Client, sessionMeta, messages, counts, partial);
            return new SessionImport(session, messages, _source ?? string.Empty, messageSourcePaths);
        }
    }
}
