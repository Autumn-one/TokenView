using System.IO;
using System.Text;
using System.Text.Json;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>Pi Desktop 会话转录（schema 1）；物理 operation 展开后只统计一次。</summary>
public sealed class PiDesktopCollector : IClientCollector
{
    private const string Client = "pi_desktop";
    private const int ParserVersion = 1;

    private readonly string? _root;

    public PiDesktopCollector() : this(DefaultRoot())
    {
    }

    public PiDesktopCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    public static string? DefaultRoot()
    {
        var sessionDirectory = Environment.GetEnvironmentVariable("PI_DESKTOP_SESSIONS_DIR");
        if (sessionDirectory is not null) return sessionDirectory;
        return CollectorSupport.Combine(CollectorSupport.HomeDir(), ".pi-desktop", "sessions");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_root is null) return MissingRoot(report);
        if (File.Exists(_root))
        {
            throw SyncException.Directory(new IOException("Pi Desktop root is not a directory"));
        }
        if (!Directory.Exists(_root)) return MissingRoot(report);

        var paths = CollectorSupport.DiscoverFiles(_root, report, Accepts);
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

        var metas = new SortedDictionary<string, SessionAccum>(StringComparer.Ordinal);
        var requests = new SortedDictionary<string, (Candidate Candidate, string Path)>(
            StringComparer.Ordinal);
        for (var index = 0; index < paths.Count; index++)
        {
            var parsed = ParseFile(paths[index]);
            report.FilesParsed++;
            report.UnparsedLines += parsed.Unparsed;
            sources[index] = sources[index] with { UnparsedRecords = parsed.Unparsed };
            if (metas.ContainsKey(parsed.Meta.Id))
            {
                throw SyncException.FromSource(
                    new InvalidDataException("duplicate Pi Desktop session identity"));
            }

            foreach (var candidate in parsed.Requests)
            {
                // Operation UUIDs are global; legacy message ids are scoped to their session.
                var key = candidate.Message.DedupKey.StartsWith("operation:", StringComparison.Ordinal)
                    ? candidate.Message.DedupKey
                    : $"{parsed.Meta.Id}:{candidate.Message.DedupKey}";
                if (!requests.TryGetValue(key, out var old) ||
                    PreferTotal(candidate.Message.Usage.TotalTokens,
                        old.Candidate.Message.Usage.TotalTokens))
                {
                    requests[key] = (candidate, sources[index].Path);
                }
            }

            metas[parsed.Meta.Id] = new SessionAccum(parsed.Meta, sources[index].Path,
                parsed.Unparsed > 0);
        }

        var details = new Dictionary<string, List<(Candidate Candidate, string Path)>>(
            StringComparer.Ordinal);
        foreach (var (candidate, path) in requests.Values)
        {
            var sessionId = candidate.Message.ClientSessionId;
            if (!details.TryGetValue(sessionId, out var list))
            {
                list = [];
                details[sessionId] = list;
            }
            list.Add((candidate, path));
        }

        var imports = new List<SessionImport>(metas.Count);
        foreach (var (id, accum) in metas)
        {
            details.Remove(id, out var found);
            var messages = new List<MessageRecord>();
            var counts = new List<RawTokenCounts>();
            var messageSourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (candidate, sourcePath) in found ?? new List<(Candidate, string)>())
            {
                messageSourcePaths[candidate.Message.DedupKey] = sourcePath;
                counts.Add(candidate.Raw);
                messages.Add(candidate.Message);
            }
            imports.Add(new SessionImport(
                RequestBuilder.Session(Client, accum.Meta, messages, counts, accum.Partial),
                messages, accum.Path, messageSourcePaths));
        }

        // Never publish a snapshot that raced an append, replacement or newly discovered file.
        var currentPaths = CollectorSupport.DiscoverFiles(_root, new SyncReport(), Accepts);
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

        var imported = store.ReplaceClientSnapshot(Client, sources, imports);
        report.SessionsImported = imports.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private static SyncReport MissingRoot(SyncReport report)
    {
        report.RootMissing = true;
        return report;
    }

    private static bool PreferTotal(long? candidate, long? current) =>
        candidate is { } value && (current is not { } old || value > old);

    private static bool Accepts(string path) =>
        string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.Ordinal);

    private static ParsedFile ParseFile(string path)
    {
        SessionMeta? meta = null;
        var requests = new List<Candidate>();
        var unparsed = 0;
        long ordinal = 0;

        try
        {
            CollectorSupport.ReadJsonLines(path, (line, complete) =>
            {
                ordinal++;
                if (CollectorSupport.IsEmptyLine(line)) return;

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException error)
                {
                    if (!complete) throw SyncException.FromSource(error);
                    if (meta is null) throw InvalidSession();
                    unparsed++;
                    return;
                }

                using (document)
                {
                    var element = document.RootElement;
                    if (!TryReadKind(element, out var kind))
                    {
                        if (meta is null) throw InvalidSession();
                        unparsed++;
                        return;
                    }
                    if (meta is null)
                    {
                        meta = ReadHeader(element, kind);
                        return;
                    }
                    if (!string.Equals(kind, "message", StringComparison.Ordinal))
                    {
                        unparsed++;
                        return;
                    }
                    if (!TryReadRow(element, out var row))
                    {
                        unparsed++;
                        return;
                    }

                    var time = ParseIso8601UtcMs(row.CreatedAt);
                    if (time is { } end && (meta.EndMs is null || end > meta.EndMs))
                    {
                        meta = meta with { EndMs = end };
                    }

                    if (!string.Equals(row.Role, "assistant", StringComparison.Ordinal))
                    {
                        if (row.Role is "user" or "system" or "tool") return;
                        unparsed++;
                        return;
                    }
                    if (string.IsNullOrEmpty(row.Id))
                    {
                        unparsed++;
                        return;
                    }

                    List<ExpandedUsage> expanded;
                    if (row.Usage is { } usage)
                    {
                        if (!usage.TryExpand(row.Id, out expanded))
                        {
                            unparsed++;
                            return;
                        }
                    }
                    else
                    {
                        expanded = [new ExpandedUsage($"message:{row.Id}", null, default)];
                    }

                    foreach (var item in expanded)
                    {
                        var message = RequestBuilder.Request(Client, meta.Id, new RequestMeta(
                            item.Key,
                            item.Model ?? row.Model,
                            null,
                            time,
                            time,
                            false,
                            $"line={ordinal}"), item.Raw);
                        requests.Add(new Candidate(message, item.Raw));
                    }
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

        return meta is null ? throw InvalidSession() : new ParsedFile(meta, requests, unparsed);
    }

    private static SessionMeta ReadHeader(JsonElement element, string kind)
    {
        if (!element.TryGetProperty("schema", out var schemaElement) ||
            schemaElement.ValueKind != JsonValueKind.Number ||
            !schemaElement.TryGetUInt64(out var schema) ||
            !TryRequiredString(element, "sessionId", out var sessionId) ||
            !TryRequiredString(element, "createdAt", out var createdAt) ||
            !TryOptionalString(element, "cwd", out var cwd))
        {
            throw InvalidSession();
        }

        var start = ParseIso8601UtcMs(createdAt);
        if (!string.Equals(kind, "session", StringComparison.Ordinal) || schema != 1 ||
            sessionId.Length == 0 || start is null)
        {
            throw InvalidSession();
        }
        return new SessionMeta(sessionId, null, cwd, start, start);
    }

    private static bool TryReadKind(JsonElement element, out string kind)
    {
        kind = string.Empty;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("type", out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        kind = value.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadRow(JsonElement element, out RowInfo row)
    {
        row = null!;
        if (!TryRequiredString(element, "id", out var id) ||
            !TryRequiredString(element, "role", out var role) ||
            !TryOptionalString(element, "createdAt", out var createdAt) ||
            !TryOptionalObject(element, "meta", out var metaElement))
        {
            return false;
        }

        string? model = null;
        Usage? usage = null;
        if (metaElement is { } meta)
        {
            if (!TryOptionalString(meta, "modelId", out model) ||
                !TryOptionalObject(meta, "usage", out var usageElement))
            {
                return false;
            }
            if (usageElement is { } usageValue && !TryReadUsage(usageValue, out usage))
            {
                return false;
            }
        }

        row = new RowInfo(id, role, createdAt, model, usage);
        return true;
    }

    private static bool TryReadUsage(JsonElement element, out Usage usage)
    {
        usage = null!;
        if (!TryOptionalString(element, "operationId", out var operationId) ||
            !TryOptionalString(element, "aggregation", out var aggregation) ||
            !TryOptionalString(element, "usageOrigin", out var usageOrigin) ||
            !TryOptionalString(element, "modelId", out var modelId) ||
            !TryOptionalU64(element, "inputTokens", out var input) ||
            !TryOptionalU64(element, "outputTokens", out var output) ||
            !TryOptionalU64(element, "totalTokens", out var total) ||
            !TryOptionalU64(element, "reasoningTokens", out var reasoning) ||
            !TryDefaultU64(element, "cacheReadTokens", out var cacheRead) ||
            !TryDefaultU64(element, "cacheWriteTokens", out var cacheWrite))
        {
            return false;
        }

        var operations = new List<Usage>();
        if (element.TryGetProperty("operations", out var operationsElement))
        {
            if (operationsElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var operation in operationsElement.EnumerateArray())
            {
                if (operation.ValueKind != JsonValueKind.Object ||
                    !TryReadUsage(operation, out var child))
                {
                    return false;
                }
                operations.Add(child);
            }
        }

        usage = new Usage(operationId, aggregation, usageOrigin, modelId, input, output, total,
            reasoning, cacheRead, cacheWrite, operations);
        return true;
    }

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

    private static bool TryOptionalObject(JsonElement element, string name, out JsonElement? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.Object) return false;
        value = property;
        return true;
    }

    private static bool TryOptionalU64(JsonElement element, string name, out long? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetUInt64(out var number) ||
            number > long.MaxValue)
        {
            return false;
        }
        value = (long)number;
        return true;
    }

    /// <summary>字段缺失按 0 处理，显式 null 视为无效。</summary>
    private static bool TryDefaultU64(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property)) return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetUInt64(out var number) ||
            number > long.MaxValue)
        {
            return false;
        }
        value = (long)number;
        return true;
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
        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 ||
            second > 60)
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

    private static SyncException InvalidSession() =>
        SyncException.FromSource(
            new InvalidDataException("unsupported or incomplete Pi Desktop session"));

    private sealed record ExpandedUsage(string Key, string? Model, RawTokenCounts Raw);

    private sealed record RowInfo(string Id, string Role, string? CreatedAt, string? Model,
        Usage? Usage);

    private sealed record Candidate(MessageRecord Message, RawTokenCounts Raw);

    private sealed record ParsedFile(SessionMeta Meta, List<Candidate> Requests, int Unparsed);

    private sealed class SessionAccum
    {
        public SessionAccum(SessionMeta meta, string path, bool partial)
        {
            Meta = meta;
            Path = path;
            Partial = partial;
        }

        public SessionMeta Meta { get; }
        public string Path { get; }
        public bool Partial { get; }
    }

    /// <summary>对应源数据中的 `Usage` 记录；聚合只能展开一次。</summary>
    private sealed class Usage
    {
        public Usage(string? operationId, string? aggregation, string? usageOrigin,
            string? modelId, long? input, long? output, long? total, long? reasoning,
            long cacheRead, long cacheWrite, List<Usage> operations)
        {
            OperationId = operationId;
            Aggregation = aggregation;
            UsageOrigin = usageOrigin;
            ModelId = modelId;
            Input = input;
            Output = output;
            Total = total;
            Reasoning = reasoning;
            CacheRead = cacheRead;
            CacheWrite = cacheWrite;
            Operations = operations;
        }

        private string? OperationId { get; }
        private string? Aggregation { get; }
        private string? UsageOrigin { get; }
        private string? ModelId { get; }
        private long? Input { get; }
        private long? Output { get; }
        private long? Total { get; }
        private long? Reasoning { get; }
        private long CacheRead { get; }
        private long CacheWrite { get; }
        private IReadOnlyList<Usage> Operations { get; }

        public RawTokenCounts Raw => new(Input, Output, CacheRead, CacheWrite, Reasoning, Total);

        private bool IsValid =>
            CollectorSupport.ValidCounts(Raw) &&
            (UsageOrigin is null || UsageOrigin is "pi" or "legacy") &&
            (Aggregation is null || Aggregation is "operation" or "aggregate");

        public bool TryExpand(string messageId, out List<ExpandedUsage> result)
        {
            result = [];
            if (!IsValid) return false;

            if (Operations.Count == 0)
            {
                var key = string.IsNullOrEmpty(OperationId)
                    ? $"message:{messageId}"
                    : $"operation:{OperationId}";
                result.Add(new ExpandedUsage(key, ModelId, Raw));
                return true;
            }

            if (!string.Equals(Aggregation, "aggregate", StringComparison.Ordinal)) return false;

            var operations = new SortedDictionary<string, (string? Model, RawTokenCounts Raw)>(
                StringComparer.Ordinal);
            foreach (var operation in Operations)
            {
                var id = operation.OperationId;
                if (string.IsNullOrEmpty(id) || !operation.IsValid ||
                    operation.Operations.Count != 0)
                {
                    return false;
                }
                var item = (operation.ModelId, operation.Raw);
                if (!operations.TryGetValue(id, out var current) ||
                    PreferRawTotal(item.Item2, current.Raw))
                {
                    operations[id] = item;
                }
            }

            var sum = RequestBuilder.SumCompleteCounts(
                operations.Values.Select(item => item.Raw).ToList());
            var raw = Raw;
            if (!TryDifference(raw.Input, sum.Input, out var input) ||
                !TryDifference(raw.Output, sum.Output, out var output) ||
                !TryDifference(raw.CacheRead, sum.CacheRead, out var cacheRead) ||
                !TryDifference(raw.CacheWrite, sum.CacheWrite, out var cacheWrite) ||
                !TryDifference(raw.Reasoning, sum.Reasoning, out var reasoning) ||
                !TryDifference(raw.ClientTotal, sum.ClientTotal, out var clientTotal))
            {
                return false;
            }

            foreach (var (id, item) in operations)
            {
                result.Add(new ExpandedUsage($"operation:{id}", item.Model, item.Raw));
            }

            // Legacy unattributed usage remains additive, as in Pi's native ledger contract.
            var residual = new RawTokenCounts(input, output, cacheRead, cacheWrite, reasoning,
                clientTotal);
            if (residual.Input is > 0 || residual.Output is > 0 || residual.CacheRead is > 0 ||
                residual.CacheWrite is > 0 || residual.Reasoning is > 0 ||
                residual.ClientTotal is > 0)
            {
                result.Add(new ExpandedUsage($"message:{messageId}:residual", ModelId, residual));
            }
            return true;
        }

        private static bool PreferRawTotal(RawTokenCounts candidate, RawTokenCounts current) =>
            candidate.ClientTotal is { } value &&
            (current.ClientTotal is not { } old || value > old);

        private static bool TryDifference(long? whole, long? part, out long? difference)
        {
            if (whole is { } value && part is { } subtrahend)
            {
                if (value < subtrahend)
                {
                    difference = null;
                    return false;
                }
                difference = value - subtrahend;
                return true;
            }
            if (whole is null && part is null)
            {
                difference = null;
                return true;
            }
            difference = null;
            return false;
        }
    }
}
