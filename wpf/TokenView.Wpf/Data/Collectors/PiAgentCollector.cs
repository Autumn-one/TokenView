using System.Globalization;
using System.IO;
using System.Text.Json;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>Pi coding agent v3 会话转录；只读取统计所需元数据。</summary>
public sealed class PiAgentCollector : IClientCollector
{
    private const string Client = "pi_agent";
    private const int ParserVersion = 1;

    private readonly string? _root;

    public PiAgentCollector() : this(DefaultRoot())
    {
    }

    public PiAgentCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    public static string? DefaultRoot()
    {
        var sessionDirectory = Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR");
        if (sessionDirectory is not null) return sessionDirectory;
        var agentDirectory = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        if (agentDirectory is not null) return Path.Combine(agentDirectory, "sessions");
        return CollectorSupport.Combine(CollectorSupport.HomeDir(), ".pi", "agent", "sessions");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_root is null || !Directory.Exists(_root))
        {
            report.RootMissing = true;
            return report;
        }

        var paths = DiscoverFiles(_root, report);
        var sources = new List<SourceFile>(paths.Count);
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

        var sessions = new Dictionary<string, SessionAccum>(StringComparer.Ordinal);
        for (var index = 0; index < paths.Count; index++)
        {
            var parsed = ParseFile(paths[index]);
            report.FilesParsed++;
            report.UnparsedLines += parsed.Unparsed;
            sources[index] = sources[index] with { UnparsedRecords = parsed.Unparsed };
            if (!sessions.TryGetValue(parsed.Meta.Id, out var accum))
            {
                accum = new SessionAccum(parsed.Meta, parsed.Unparsed > 0, sources[index].Path);
                sessions[parsed.Meta.Id] = accum;
            }
            else if (parsed.Unparsed > 0)
            {
                accum.Partial = true;
            }
            foreach (var candidate in parsed.Requests)
            {
                if (!accum.Requests.ContainsKey(candidate.Message.DedupKey))
                {
                    accum.Requests[candidate.Message.DedupKey] = (candidate, sources[index].Path);
                }
            }
        }

        var imports = new List<SessionImport>(sessions.Count);
        foreach (var accum in sessions.Values)
        {
            var messages = new List<MessageRecord>(accum.Requests.Count);
            var counts = new List<RawTokenCounts>(accum.Requests.Count);
            var messagePaths = new Dictionary<string, string>(accum.Requests.Count, StringComparer.Ordinal);
            foreach (var (candidate, sourcePath) in accum.Requests.Values)
            {
                messagePaths[candidate.Message.DedupKey] = sourcePath;
                counts.Add(candidate.Raw);
                messages.Add(candidate.Message);
            }
            imports.Add(new SessionImport(
                RequestBuilder.Session(Client, accum.Meta, messages, counts, accum.Partial),
                messages, accum.SourcePath, messagePaths));
        }

        // 绝不发布与追加、替换或新发现文件竞争的快照。
        var currentPaths = DiscoverFiles(_root, new SyncReport());
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

        var imported = store.ReplaceClientSnapshot(Client, sources, imports);
        report.SessionsImported = imports.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private static List<string> DiscoverFiles(string root, SyncReport report) =>
        CollectorSupport.DiscoverFiles(root, report, path =>
            string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.Ordinal));

    private static ParsedFile ParseFile(string path)
    {
        SessionMeta? meta = null;
        var requests = new List<Candidate>();
        var unparsed = 0;
        var ordinal = 0L;
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
                if (!string.Equals(kind, "message", StringComparison.Ordinal)) return;
                if (!TryReadRow(element, out var row))
                {
                    unparsed++;
                    return;
                }
                if (row.Message is not { } message) return;
                if (!string.Equals(message.Role, "assistant", StringComparison.Ordinal)) return;
                if (message.Usage is not { } raw) return;
                if (!CollectorSupport.ValidCounts(raw))
                {
                    unparsed++;
                    return;
                }
                var createdMs = ParseIso8601UtcMs(row.Timestamp) ?? message.Timestamp;
                var key = string.IsNullOrEmpty(row.Id) ? $"line:{ordinal}" : row.Id;
                var record = RequestBuilder.Request(Client, meta.Id, new RequestMeta(
                    key, message.Model, null, createdMs, createdMs, false, $"line={ordinal}"), raw);
                record = record with { Seq = ordinal };
                requests.Add(new Candidate(record, raw));
            }
        });
        return meta is null
            ? throw InvalidSession()
            : new ParsedFile(meta, requests, unparsed);
    }

    private static SessionMeta ReadHeader(JsonElement element, string? kind)
    {
        if (!element.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt64(out var version) || version < 0)
        {
            throw InvalidSession();
        }
        if (!TryRequiredString(element, "id", out var id) ||
            !TryRequiredString(element, "timestamp", out var timestamp) ||
            !TryOptionalString(element, "cwd", out var cwd))
        {
            throw InvalidSession();
        }
        var start = ParseIso8601UtcMs(timestamp);
        if (!string.Equals(kind, "session", StringComparison.Ordinal) || version != 3 ||
            id.Length == 0 || start is null)
        {
            throw InvalidSession();
        }
        return new SessionMeta(id, null, cwd, start, null);
    }

    private static bool TryReadKind(JsonElement element, out string? kind)
    {
        kind = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("type", out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        kind = value.GetString();
        return true;
    }

    private static bool TryReadRow(JsonElement element, out RowInfo row)
    {
        row = null!;
        if (!TryOptionalString(element, "id", out var id) ||
            !TryOptionalString(element, "timestamp", out var timestamp) ||
            !TryOptionalObject(element, "message", out var messageElement))
        {
            return false;
        }
        if (messageElement is null)
        {
            row = new RowInfo(id, timestamp, null);
            return true;
        }
        var message = messageElement.Value;
        if (!TryOptionalString(message, "role", out var role) ||
            !TryOptionalString(message, "model", out var model) ||
            !TryOptionalObject(message, "usage", out var usageElement) ||
            !TryOptionalInt64(message, "timestamp", out var messageTimestamp))
        {
            return false;
        }
        RawTokenCounts? usage = null;
        if (usageElement is { } counts)
        {
            if (!TryOptionalU64(counts, "input", out var input) ||
                !TryOptionalU64(counts, "output", out var output) ||
                !TryOptionalU64(counts, "cacheRead", out var cacheRead) ||
                !TryOptionalU64(counts, "cacheWrite", out var cacheWrite) ||
                !TryOptionalU64(counts, "reasoning", out var reasoning) ||
                !TryOptionalU64(counts, "totalTokens", out var totalTokens))
            {
                return false;
            }
            usage = new RawTokenCounts(input, output, cacheRead, cacheWrite, reasoning, totalTokens);
        }
        row = new RowInfo(id, timestamp, new MessageInfo(role, model, messageTimestamp, usage));
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
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
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
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.Object) return false;
        value = property;
        return true;
    }

    private static bool TryOptionalInt64(JsonElement element, string name, out long? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number))
        {
            return false;
        }
        value = number;
        return true;
    }

    private static bool TryOptionalU64(JsonElement element, string name, out long? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number) ||
            number < 0)
        {
            return false;
        }
        value = number;
        return true;
    }

    /// <summary>解析 ISO 8601 UTC 时间至毫秒：仅接受 UTC `Z` 形式，毫秒最多三位。</summary>
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
            if (value[19] != '.') return null;
            var fractionLength = value.Length - 21;
            if (fractionLength is < 1 or > 3) return null;
            var fraction = value.AsSpan(20, fractionLength);
            foreach (var character in fraction)
            {
                if (character is < '0' or > '9') return null;
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

    private static SyncException InvalidSession() =>
        SyncException.FromSource(
            new InvalidDataException("unsupported or incomplete Pi agent session"));

    private sealed record Candidate(MessageRecord Message, RawTokenCounts Raw);

    private sealed record RowInfo(string? Id, string? Timestamp, MessageInfo? Message);

    private sealed record MessageInfo(string? Role, string? Model, long? Timestamp,
        RawTokenCounts? Usage);

    private sealed record ParsedFile(SessionMeta Meta, List<Candidate> Requests, int Unparsed);

    private sealed class SessionAccum
    {
        public SessionAccum(SessionMeta meta, bool partial, string sourcePath)
        {
            Meta = meta;
            Partial = partial;
            SourcePath = sourcePath;
        }

        public SessionMeta Meta { get; }
        public bool Partial { get; set; }
        public string SourcePath { get; }
        public SortedDictionary<string, (Candidate Candidate, string SourcePath)> Requests { get; } =
            new(StringComparer.Ordinal);
    }
}
