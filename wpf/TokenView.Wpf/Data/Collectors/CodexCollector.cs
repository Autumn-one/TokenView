using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>Codex rollout（rollout-*.jsonl）采集器；只读取用量元数据，线程总量取客户端累计值。</summary>
public sealed class CodexCollector : IClientCollector
{
    private const string Client = "codex";
    private const long ParserVersion = 2;

    private readonly string? _root;

    public CodexCollector() : this(DefaultRoot())
    {
    }

    public CodexCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    /// <summary>CODEX_HOME 优先，否则 USERPROFILE / HOME 下的 .codex/sessions。</summary>
    public static string? DefaultRoot()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrEmpty(codexHome)) return Path.Combine(codexHome, "sessions");
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(profile)) profile = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, ".codex", "sessions");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (string.IsNullOrEmpty(_root))
        {
            report.RootMissing = true;
            return report;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(_root);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            report.RootMissing = true;
            return report;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                         ArgumentException or NotSupportedException)
        {
            throw SyncException.Directory(error);
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw SyncException.Directory(new IOException("Codex source is not a directory"));
        }

        var files = DiscoverRolloutFiles(_root, report);
        report.FilesSeen = files.Count;

        var parsedFiles = new List<ParsedFile>();
        foreach (var path in files)
        {
            if (TryFingerprint(path) is not { } fingerprint)
            {
                report.FilesFailed++;
                continue;
            }
            var (size, mtime) = fingerprint;
            var watermark = Store(() => store.SourceFile(Client, path));
            var reason = DecideScan(watermark, size, mtime, options.ForceRescan);
            CollectorSupport.CountReason(report, reason);
            if (reason == ScanReason.Unchanged)
            {
                report.FilesSkippedUnchanged++;
                continue;
            }
            var parsed = ParseFile(path, size, mtime);
            if (parsed is null)
            {
                report.FilesFailed++;
                continue;
            }
            report.FilesParsed++;
            report.UnparsedLines += parsed.UnparsedLines;
            parsedFiles.Add(parsed);
        }

        var threads = new SortedDictionary<string, ThreadAggregate>(StringComparer.Ordinal);
        foreach (var parsed in parsedFiles)
        {
            if (!FingerprintMatches(parsed))
            {
                report.FilesFailed++;
                continue;
            }
            if (parsed.ThreadId is { } threadId)
            {
                if (!threads.TryGetValue(threadId, out var aggregate))
                {
                    aggregate = new ThreadAggregate();
                    threads[threadId] = aggregate;
                }
                aggregate.Push(parsed);
            }
            else
            {
                report.FilesWithoutIdentity++;
            }
        }

        foreach (var (threadId, aggregate) in threads)
        {
            // 同线程任一文件在扫描期间变化，则整组放弃并保留旧水位（下次重试）。
            if (aggregate.Files.Any(file => !FingerprintMatches(file)))
            {
                report.FilesFailed += aggregate.Files.Count;
                continue;
            }
            var session = aggregate.SessionRecord(threadId);
            var ordered = aggregate.Files
                .OrderBy(file => file.MtimeMs)
                .ThenBy(file => file.Path, StringComparer.Ordinal)
                .ToList();
            var sessionCounted = false;
            foreach (var file in ordered)
            {
                var source = CollectorSupport.NewSourceFile(Client, file.Path, SourceFileKind.Jsonl,
                    file.SizeBytes, file.MtimeMs, file.SizeBytes, ParserVersion, file.UnparsedLines);
                var summary = Store(() => store.ReplaceFileImport(source, session, file.Messages));
                if (!sessionCounted)
                {
                    sessionCounted = true;
                    if (summary.Session == UpsertOutcome.SkippedStale)
                    {
                        report.SessionsSkippedStale++;
                    }
                    else if (summary.Session is not null)
                    {
                        report.SessionsImported++;
                    }
                }
                report.MessagesInserted += summary.MessagesInserted;
                report.MessagesUpdated += summary.MessagesUpdated;
                report.MessagesSkippedStale += summary.MessagesSkippedStale;
                report.MessagesPruned += summary.MessagesPruned;
            }
        }

        return report;
    }

    private static T Store<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SyncException)
        {
            throw;
        }
        catch (Exception error) when (error is SqliteException or ArgumentException)
        {
            throw SyncException.Store(error);
        }
    }

    /// <summary>递归发现 rollout-*.jsonl；根目录失败即错误，子目录失败只计数。</summary>
    private static List<string> DiscoverRolloutFiles(string root, SyncReport report)
    {
        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (string.Equals(directory, root, StringComparison.Ordinal))
                {
                    throw SyncException.Directory(error);
                }
                report.DirectoriesFailed++;
                continue;
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
                    report.FilesFailed++;
                    continue;
                }
                // 不跟随符号链接：链接既非目录也非文件。
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }
                var name = Path.GetFileName(entry);
                if (name.StartsWith("rollout-", StringComparison.Ordinal) &&
                    name.EndsWith(".jsonl", StringComparison.Ordinal))
                {
                    files.Add(entry);
                }
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static ScanReason DecideScan(SourceFileRow? watermark, long size, long mtime, bool force)
    {
        if (force) return ScanReason.Forced;
        if (watermark is null) return ScanReason.New;
        if (watermark.ParserVersion != ParserVersion) return ScanReason.ParserVersionChanged;
        if (size < watermark.ContentOffset || size < (watermark.SizeBytes ?? 0))
            return ScanReason.TruncatedOrReplaced;
        if (watermark.SizeBytes == size && watermark.MtimeMs == mtime) return ScanReason.Unchanged;
        return ScanReason.Appended;
    }

    private static (long Size, long MtimeMs)? TryFingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var modified = info.LastWriteTimeUtc;
            var mtime = modified < DateTime.UnixEpoch
                ? 0
                : new DateTimeOffset(modified).ToUnixTimeMilliseconds();
            return (info.Length, mtime);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool FingerprintMatches(ParsedFile parsed)
    {
        if (TryFingerprint(parsed.Path) is not { } fingerprint) return false;
        return fingerprint.Size == parsed.SizeBytes && fingerprint.MtimeMs == parsed.MtimeMs;
    }

    private static ParsedFile? ParseFile(string path, long size, long mtime)
    {
        var parsed = new ParsedFile(path, size, mtime);
        var newRecords = new List<MessageRecord>();
        var legacyEvents = new List<LegacyEvent>();
        var lineNumber = 0;
        try
        {
            CollectorSupport.ReadJsonLines(path, (line, complete) =>
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) return;
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException error)
                {
                    // 未完成的最后一行属于 I/O 级错误：放弃快照、保留水位，下次重试。
                    if (!complete) throw SyncException.FromSource(error);
                    parsed.UnparsedLines++;
                    return;
                }
                using (document)
                {
                    var value = document.RootElement;
                    if (value.ValueKind != JsonValueKind.Object) return;
                    if (ReadOrdinal(value) is { } ordinal) parsed.MarkOrdinal(ordinal);
                    switch (value.String("type"))
                    {
                        case "session_meta":
                            ParseSessionMeta(parsed, value);
                            break;
                        case "turn_context":
                            ParseTurnContext(parsed, value);
                            break;
                        case "token_usage_record":
                            ParseTokenUsageRecord(parsed, value, lineNumber, newRecords);
                            break;
                        case "event_msg":
                            ParseLegacyTokenCount(parsed, value, lineNumber, legacyEvents);
                            break;
                    }
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SyncException)
        {
            return null;
        }
        ResolveMessages(parsed, newRecords, legacyEvents);
        return FingerprintMatches(parsed) ? parsed : null;
    }

    private static void ParseSessionMeta(ParsedFile parsed, JsonElement value)
    {
        var payload = value.Element("payload");
        if (parsed.ThreadId is null) parsed.ThreadId = PayloadString(payload, "id");
        if (parsed.SessionId is null) parsed.SessionId = PayloadString(payload, "session_id");
        if (parsed.ParentThreadId is null) parsed.ParentThreadId = PayloadNestedString(payload, "history_base", "thread_id");
        if (parsed.Cwd is null) parsed.Cwd = PayloadString(payload, "cwd");
        if (parsed.Model is null) parsed.Model = PayloadNestedString(payload, "base_instructions", "provenance", "model");
        parsed.MarkTime(LineTimestamp(value));
    }

    private static void ParseTurnContext(ParsedFile parsed, JsonElement value)
    {
        var payload = value.Element("payload");
        if (payload is { } element)
        {
            // 实际运行的模型以 turn_context 为准。
            var model = element.String("model");
            if (model is not null) parsed.Model = model;
            if (parsed.Cwd is null) parsed.Cwd = element.String("cwd");
        }
        parsed.MarkTime(LineTimestamp(value));
    }

    private static void ParseTokenUsageRecord(ParsedFile parsed, JsonElement value, int line,
        List<MessageRecord> records)
    {
        var payload = value.Element("payload");
        var timestamp = LineTimestamp(value);
        var ordinal = ReadOrdinal(value);
        var threadId = PayloadString(payload, "thread_id") ?? parsed.ThreadId;
        if (threadId is null)
        {
            parsed.UnparsedLines++;
            return;
        }
        if (parsed.SessionId is null) parsed.SessionId = PayloadString(payload, "session_id");
        parsed.MarkTime(timestamp);
        var cumulative = payload is { } element && element.Element("thread_token_usage") is { } threadUsage
            ? ReadCounts(threadUsage)
            : (RawTokenCounts?)null;
        records.Add(CreateMessage(new CodexUsageRecord(
            threadId,
            PayloadString(payload, "response_id"),
            ordinal,
            cumulative,
            parsed.Path,
            line,
            timestamp,
            ReadCounts(payload is { } usagePayload ? usagePayload.Element("usage") : null))));
        if (cumulative is { } latest) parsed.NewCumulative = latest;
    }

    private static void ParseLegacyTokenCount(ParsedFile parsed, JsonElement value, int line,
        List<LegacyEvent> events)
    {
        var payload = value.Element("payload");
        if (payload is not { } element ||
            !string.Equals(element.String("type"), "token_count", StringComparison.Ordinal))
        {
            return;
        }
        if (element.Element("info") is not { } info) return;
        if (info.Element("last_token_usage") is not { } lastUsage) return;
        var timestamp = LineTimestamp(value);
        var ordinal = ReadOrdinal(value);
        var threadId = element.String("thread_id") ?? parsed.ThreadId;
        if (threadId is null)
        {
            parsed.UnparsedLines++;
            return;
        }
        parsed.MarkTime(timestamp);
        var cumulative = info.Element("total_token_usage") is { } totalUsage
            ? ReadCounts(totalUsage)
            : (RawTokenCounts?)null;
        events.Add(new LegacyEvent(ordinal, line, cumulative, timestamp, threadId, ReadCounts(lastUsage)));
        if (cumulative is { } latest) parsed.LegacyCumulative = latest;
    }

    /// <summary>旧格式镜像事件与新格式配对后丢弃，只保留旧格式前缀。</summary>
    private static void ResolveMessages(ParsedFile parsed, List<MessageRecord> newRecords,
        List<LegacyEvent> legacyEvents)
    {
        var mirrorIndex = 0;
        var seenLegacy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var legacy in legacyEvents)
        {
            var record = new CodexUsageRecord(legacy.ThreadId, null, legacy.Ordinal, legacy.Cumulative,
                parsed.Path, legacy.Line, legacy.Timestamp, legacy.Usage);
            if (legacy.Ordinal is null && !seenLegacy.Add(DedupKey(record))) continue;
            var isMirror = mirrorIndex < newRecords.Count &&
                           legacy.Usage.ClientTotal is { } total &&
                           newRecords[mirrorIndex].Usage.TotalTokens == total;
            if (isMirror)
            {
                mirrorIndex++;
                continue;
            }
            parsed.Messages.Add(CreateMessage(record));
        }
        parsed.Messages.AddRange(newRecords);
        // 无原生序号的记录按去重键只保留首次观测；有 ordinal 的记录不需要在此去重。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        parsed.Messages.RemoveAll(message => message.Seq is null && !seen.Add(message.DedupKey));
    }

    private static MessageRecord CreateMessage(CodexUsageRecord record)
    {
        var semantics = TokenSemanticsCatalog.Codex;
        return new MessageRecord
        {
            Client = Client,
            ClientSessionId = record.ThreadId,
            DedupKey = DedupKey(record),
            Seq = record.Ordinal,
            CreatedAtMs = ParseIso8601UtcMs(record.Timestamp),
            Model = null,
            Agent = null,
            Usage = UsageNormalizer.Normalize(semantics, record.Usage),
            IsSidechain = false,
            SemanticsId = semantics.Id,
            SourceKind = SourceKind.Measured,
            SourceRef = SourceRef(record),
            CostSource = CostSource.None,
        };
    }

    private static string DedupKey(CodexUsageRecord record)
    {
        if (!string.IsNullOrEmpty(record.ResponseId)) return record.ResponseId;
        if (record.Ordinal is { } ordinal)
            return "token_count:" + ordinal.ToString(CultureInfo.InvariantCulture);
        if (record.Cumulative is { ClientTotal: not null } cumulative)
            return "token_count:cumulative:" + CumulativeKey(cumulative);
        return "token_count:file:" + record.SourcePath + ":line:" +
               record.Line.ToString(CultureInfo.InvariantCulture);
    }

    private static string SourceRef(CodexUsageRecord record)
    {
        if (record.Ordinal is { } ordinal)
            return "ordinal=" + ordinal.ToString(CultureInfo.InvariantCulture);
        if (record.Cumulative is { ClientTotal: not null } cumulative)
            return "token_count:cumulative:" + CumulativeKey(cumulative);
        return "file=" + record.SourcePath + ";line=" + record.Line.ToString(CultureInfo.InvariantCulture);
    }

    private static string CumulativeKey(RawTokenCounts counts)
    {
        static string Part(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "?";
        return string.Join(":", new[]
        {
            Part(counts.Input), Part(counts.Output), Part(counts.CacheRead),
            Part(counts.CacheWrite), Part(counts.Reasoning), Part(counts.ClientTotal),
        });
    }

    private static long? ReadOrdinal(JsonElement value)
    {
        if (!value.TryGetProperty("ordinal", out var element) || element.ValueKind != JsonValueKind.Number)
            return null;
        var raw = element.GetRawText();
        if (raw.Length == 0 || raw.Any(character => character is '.' or 'e' or 'E')) return null;
        return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ordinal)
            ? ordinal
            : null;
    }

    /// <summary>Codex 字段名 → 统一计数；只接受 JSON 非负整数，缺失保持 null。</summary>
    private static RawTokenCounts ReadCounts(JsonElement? usage)
    {
        if (usage is not { } value) return default;
        return new RawTokenCounts(
            Count(value, "input_tokens"),
            Count(value, "output_tokens"),
            Count(value, "cached_input_tokens"),
            Count(value, "cache_write_input_tokens"),
            Count(value, "reasoning_output_tokens"),
            Count(value, "total_tokens"));
    }

    private static long? Count(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        var raw = value.GetRawText();
        if (raw.Length == 0 || raw.Any(character => character is '.' or 'e' or 'E' or '-' or '+'))
            return null;
        return ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
               number <= long.MaxValue
            ? (long)number
            : null;
    }

    private static string? LineTimestamp(JsonElement value)
    {
        var timestamp = value.String("timestamp");
        if (timestamp is not null) return timestamp;
        return value.Element("payload") is { } payload ? payload.String("timestamp") : null;
    }

    private static string? PayloadString(JsonElement? payload, string name) =>
        payload is { } value ? value.String(name) : null;

    private static string? PayloadNestedString(JsonElement? payload, string outer, string inner) =>
        payload is { } value && value.Element(outer) is { } nested ? nested.String(inner) : null;

    private static string? PayloadNestedString(JsonElement? payload, string outer, string middle, string inner)
    {
        if (payload is not { } value || value.Element(outer) is not { } first) return null;
        return first.Element(middle) is { } second ? second.String(inner) : null;
    }

    /// <summary>只接受 UTC `Z` 形式，毫秒部分最多三位。</summary>
    private static long? ParseIso8601UtcMs(string? value)
    {
        if (value is null) return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (bytes.Length < 20 || bytes[4] != (byte)'-' || bytes[7] != (byte)'-' ||
            bytes[10] != (byte)'T' || bytes[13] != (byte)':' || bytes[16] != (byte)':' ||
            bytes[^1] != (byte)'Z')
        {
            return null;
        }
        if (ParseNumber(bytes, 0, 4) is not { } year ||
            ParseNumber(bytes, 5, 7) is not { } month ||
            ParseNumber(bytes, 8, 10) is not { } day ||
            ParseNumber(bytes, 11, 13) is not { } hour ||
            ParseNumber(bytes, 14, 16) is not { } minute ||
            ParseNumber(bytes, 17, 19) is not { } second)
        {
            return null;
        }
        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 60)
            return null;
        long millis = 0;
        if (bytes.Length > 20)
        {
            if (bytes[19] != (byte)'.') return null;
            var fraction = bytes.Length - 21;
            if (fraction is < 1 or > 3) return null;
            for (var index = 20; index < bytes.Length - 1; index++)
            {
                if (bytes[index] is < (byte)'0' or > (byte)'9') return null;
                millis = millis * 10 + (bytes[index] - (byte)'0');
            }
            if (fraction == 1) millis *= 100;
            else if (fraction == 2) millis *= 10;
        }
        var days = DaysFromCivil(year, month, day);
        return ((days * 86_400 + hour * 3_600 + minute * 60 + second) * 1_000) + millis;
    }

    private static long? ParseNumber(byte[] bytes, int start, int end)
    {
        var negative = false;
        var index = start;
        if (bytes[index] is (byte)'+' or (byte)'-')
        {
            negative = bytes[index] == (byte)'-';
            index++;
            if (index == end) return null;
        }
        long number = 0;
        for (; index < end; index++)
        {
            var current = bytes[index];
            if (current is < (byte)'0' or > (byte)'9') return null;
            number = number * 10 + (current - (byte)'0');
        }
        return negative ? -number : number;
    }

    private static long DaysFromCivil(long year, long month, long day)
    {
        year = month <= 2 ? year - 1 : year;
        var era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - era * 400;
        var dayOfYear = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
        var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        return era * 146_097 + dayOfEra - 719_468;
    }

    private sealed record CodexUsageRecord(
        string ThreadId,
        string? ResponseId,
        long? Ordinal,
        RawTokenCounts? Cumulative,
        string SourcePath,
        int Line,
        string? Timestamp,
        RawTokenCounts Usage);

    private sealed record LegacyEvent(
        long? Ordinal,
        int Line,
        RawTokenCounts? Cumulative,
        string? Timestamp,
        string ThreadId,
        RawTokenCounts Usage);

    private sealed class ParsedFile
    {
        public ParsedFile(string path, long sizeBytes, long mtimeMs)
        {
            Path = path;
            SizeBytes = sizeBytes;
            MtimeMs = mtimeMs;
        }

        public string Path { get; }

        public long SizeBytes { get; }

        public long MtimeMs { get; }

        public string? ThreadId { get; set; }

        public string? SessionId { get; set; }

        public string? ParentThreadId { get; set; }

        public string? Cwd { get; set; }

        public string? Model { get; set; }

        public string? StartedAt { get; private set; }

        public string? EndedAt { get; private set; }

        public long? FirstOrdinal { get; private set; }

        public RawTokenCounts? NewCumulative { get; set; }

        public RawTokenCounts? LegacyCumulative { get; set; }

        public int UnparsedLines { get; set; }

        public List<MessageRecord> Messages { get; } = new();

        public void MarkTime(string? timestamp)
        {
            if (timestamp is null) return;
            if (StartedAt is null || string.CompareOrdinal(timestamp, StartedAt) < 0)
                StartedAt = timestamp;
            if (EndedAt is null || string.CompareOrdinal(timestamp, EndedAt) > 0)
                EndedAt = timestamp;
        }

        public void MarkOrdinal(long ordinal) =>
            FirstOrdinal = FirstOrdinal is { } first ? Math.Min(first, ordinal) : ordinal;
    }

    private sealed class ThreadAggregate
    {
        private RawTokenCounts? _cumulative;
        private (long Total, long Input, long CacheRead)? _cumulativeKey;

        public string? SessionId { get; set; }

        public string? ParentThreadId { get; set; }

        public string? Cwd { get; set; }

        public string? Model { get; set; }

        public string? StartedAt { get; private set; }

        public string? EndedAt { get; private set; }

        public long? FirstOrdinal { get; private set; }

        public int MessageCount { get; private set; }

        public List<ParsedFile> Files { get; } = new();

        public void Push(ParsedFile file)
        {
            SessionId ??= file.SessionId;
            ParentThreadId ??= file.ParentThreadId;
            Cwd ??= file.Cwd;
            Model ??= file.Model;
            if (file.StartedAt is { } start &&
                (StartedAt is null || string.CompareOrdinal(start, StartedAt) < 0))
            {
                StartedAt = start;
            }
            if (file.EndedAt is { } end &&
                (EndedAt is null || string.CompareOrdinal(end, EndedAt) > 0))
            {
                EndedAt = end;
            }
            if (file.FirstOrdinal is { } firstOrdinal)
            {
                FirstOrdinal = FirstOrdinal is { } current ? Math.Min(current, firstOrdinal) : firstOrdinal;
            }
            MessageCount += file.Messages.Count;
            // 新旧累计计数器的起点可能不同：取最大值，绝不相加。
            if (file.NewCumulative is { } newCumulative) ObserveCumulative(newCumulative);
            if (file.LegacyCumulative is { } legacyCumulative) ObserveCumulative(legacyCumulative);
            Files.Add(file);
        }

        private void ObserveCumulative(RawTokenCounts candidate)
        {
            var key = (candidate.ClientTotal ?? 0, candidate.Input ?? 0, candidate.CacheRead ?? 0);
            if (_cumulative is null || _cumulativeKey is not { } current || key.CompareTo(current) > 0)
            {
                _cumulative = candidate;
                _cumulativeKey = key;
            }
        }

        public SessionRecord SessionRecord(string threadId)
        {
            var coverage = MessageCount == 0
                ? MessageCoverage.NoMessages
                : FirstOrdinal == 0
                    ? MessageCoverage.Complete
                    : MessageCoverage.Partial;
            var parent = ParentThreadId is { Length: > 0 } candidate &&
                         !string.Equals(candidate, threadId, StringComparison.Ordinal)
                ? candidate
                : null;
            var semantics = TokenSemanticsCatalog.Codex;
            return new SessionRecord
            {
                Client = Client,
                ClientSessionId = threadId,
                ParentClientSessionId = parent,
                ProjectPath = Cwd,
                Model = Model,
                StartedAtMs = ParseIso8601UtcMs(StartedAt),
                EndedAtMs = ParseIso8601UtcMs(EndedAt),
                SemanticsId = semantics.Id,
                TotalBasis = SessionTotalBasis.ClientCumulative,
                Coverage = coverage,
                Usage = UsageNormalizer.Normalize(semantics, _cumulative ?? default),
                SourceKind = SourceKind.Measured,
                CostSource = CostSource.None,
                ClientCostUsd = null,
            };
        }
    }
}
