using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>Grok 权威用量快照；只读取 usage.json 与 summary.json，不读取聊天/事件日志。</summary>
public sealed class GrokCollector : IClientCollector
{
    private const string Client = "grok";
    private const long ParserVersion = 1;
    private const string IncompleteRelation = "grok.usage_incomplete";

    private readonly string? _root;

    public GrokCollector() : this(DefaultRoot())
    {
    }

    public GrokCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    public static string? DefaultRoot()
    {
        var configured = CollectorSupport.EnvPath("GROK_HOME");
        var home = CollectorSupport.HomeDir();
        var grokHome = configured ?? CollectorSupport.Combine(home, ".grok");
        return CollectorSupport.Combine(grokHome, "sessions");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_root is null)
        {
            report.RootMissing = true;
            return report;
        }
        if (!Directory.Exists(_root))
        {
            if (File.Exists(_root))
            {
                // A file at the root is not an empty root; it fails like an unreadable directory.
                DiscoverFiles(_root, report);
            }
            report.RootMissing = true;
            return report;
        }

        var paths = DiscoverFiles(_root, report);
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
        unchanged &= store.SourcePaths(Client).SequenceEqual(sources.Select(source => source.Path),
            StringComparer.Ordinal);
        if (unchanged)
        {
            report.FilesSkippedUnchanged = paths.Count;
            return report;
        }

        var sessions = new SortedDictionary<string, SessionImport>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!string.Equals(Path.GetFileName(path), "usage.json", StringComparison.Ordinal))
            {
                continue;
            }

            SessionImport import;
            try
            {
                var snapshot = ReadSnapshot(path);
                var summaryPath = SummaryPath(path);
                var summary = paths.BinarySearch(summaryPath, StringComparer.Ordinal) >= 0
                    ? ReadSummary(summaryPath)
                    : new Summary();
                import = Import(snapshot, summary, path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                              or JsonException or InvalidDataException or FormatException)
            {
                report.FilesFailed++;
                return report;
            }

            if (sessions.TryGetValue(import.Session.ClientSessionId, out var old))
            {
                var comparison = CompareNullable(old.Session.EndedAtMs, import.Session.EndedAtMs);
                if (comparison == 0)
                {
                    // Mirrors of the same snapshot must agree; never sum their absolute totals.
                    if (!SameSnapshot(old, import))
                    {
                        report.FilesFailed++;
                        return report;
                    }
                    continue;
                }
                if (comparison > 0)
                {
                    continue;
                }
            }
            sessions[import.Session.ClientSessionId] = import;
        }

        report.FilesParsed = paths.Count;
        var current = DiscoverFiles(_root, new SyncReport());
        if (!current.SequenceEqual(paths, StringComparer.Ordinal))
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

        var imports = sessions.Values.ToList();
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

    private static List<string> DiscoverFiles(string root, SyncReport report)
    {
        var paths = new List<string>();
        foreach (var workspace in Enumerate(root))
        {
            if (!IsPlainDirectory(workspace))
            {
                continue;
            }
            foreach (var session in Enumerate(workspace))
            {
                if (!IsPlainDirectory(session))
                {
                    continue;
                }
                var usage = Path.Combine(session, "usage.json");
                if (!PathExists(usage))
                {
                    continue;
                }
                if (!IsRegularFile(usage))
                {
                    throw SyncException.FromSource(new IOException("unsupported usage source"));
                }
                paths.Add(usage);
                var summary = Path.Combine(session, "summary.json");
                if (!PathExists(summary))
                {
                    continue;
                }
                if (!IsRegularFile(summary))
                {
                    throw SyncException.FromSource(new IOException("unsupported summary source"));
                }
                paths.Add(summary);
            }
        }
        paths.Sort(StringComparer.Ordinal);
        report.FilesSeen = paths.Count;
        return paths;
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

    private static FileAttributes? Attributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
    }

    private static bool PathExists(string path) => Attributes(path) is not null;

    private static bool IsPlainDirectory(string path) =>
        Attributes(path) is { } attributes &&
        (attributes & FileAttributes.Directory) != 0 &&
        (attributes & FileAttributes.ReparsePoint) == 0;

    private static bool IsRegularFile(string path) =>
        Attributes(path) is { } attributes &&
        (attributes & FileAttributes.Directory) == 0 &&
        (attributes & FileAttributes.ReparsePoint) == 0;

    /// <summary>只替换最后一段文件名，不规范化既有分隔符。</summary>
    private static string SummaryPath(string usagePath)
    {
        var separator = usagePath.LastIndexOfAny(['\\', '/']);
        return separator < 0
            ? "summary.json"
            : string.Concat(usagePath.AsSpan(0, separator + 1), "summary.json");
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

    /// <summary>共享读写方式打开，允许客户端同时写入。</summary>
    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static Snapshot ReadSnapshot(string path)
    {
        using var stream = OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }
        if (!root.TryGetProperty("sessionId", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            throw Invalid();
        }
        var sessionId = idElement.GetString()!;
        var updatedAt = OptionalString(root, "updatedAt");
        if (!root.TryGetProperty("session", out var sessionElement) ||
            sessionElement.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }
        var session = ReadUsage(sessionElement);
        var turns = new List<GrokUsage>();
        if (root.TryGetProperty("turns", out var turnsElement))
        {
            if (turnsElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid();
            }
            foreach (var turn in turnsElement.EnumerateArray())
            {
                if (turn.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid();
                }
                turns.Add(ReadUsage(turn));
            }
        }
        if (string.IsNullOrWhiteSpace(sessionId) || !session.Valid())
        {
            throw Invalid();
        }
        return new Snapshot(sessionId, updatedAt, session, turns);
    }

    private static Summary ReadSummary(string path)
    {
        using var stream = OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }
        Info? info = null;
        if (root.TryGetProperty("info", out var infoElement))
        {
            if (infoElement.ValueKind == JsonValueKind.Object)
            {
                info = new Info(OptionalString(infoElement, "id"), OptionalString(infoElement, "cwd"));
            }
            else if (infoElement.ValueKind != JsonValueKind.Null)
            {
                throw Invalid();
            }
        }
        return new Summary(info,
            OptionalString(root, "created_at"),
            OptionalString(root, "updated_at"),
            OptionalString(root, "last_active_at"));
    }

    private static GrokUsage ReadUsage(JsonElement element) => new(
        Count(element, "inputTokens"),
        Count(element, "outputTokens"),
        Count(element, "cachedReadTokens"),
        Count(element, "cacheCreationTokens"),
        Count(element, "reasoningTokens"),
        Count(element, "totalTokens"),
        Count(element, "modelCalls"),
        Count(element, "turnCount"),
        OptionalInt64(element, "turnNumber"),
        OptionalString(element, "primaryModelId"),
        OptionalString(element, "endedAt"),
        OptionalBool(element, "usageIsIncomplete"),
        ReadModelUsage(element));

    private static Dictionary<string, GrokUsage> ReadModelUsage(JsonElement element)
    {
        var result = new Dictionary<string, GrokUsage>(StringComparer.Ordinal);
        if (!element.TryGetProperty("modelUsage", out var usage))
        {
            return result;
        }
        if (usage.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }
        foreach (var property in usage.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw Invalid();
            }
            result[property.Name] = ReadUsage(property.Value);
        }
        return result;
    }

    private static ulong? Count(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number))
        {
            throw Invalid();
        }
        return number;
    }

    private static long? OptionalInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw Invalid();
        }
        return number;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid();
        }
        return value.GetString();
    }

    private static bool OptionalBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(),
        };
    }

    private static SessionImport Import(Snapshot snapshot, Summary summary, string sourcePath)
    {
        if (summary.Info?.Id is { } summaryId &&
            !string.Equals(summaryId, snapshot.SessionId, StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var turns = new SortedDictionary<long, GrokUsage>();
        foreach (var turn in snapshot.Turns)
        {
            if (turn.TurnNumber is not { } number || number < 0)
            {
                throw Invalid();
            }
            if (!turn.Valid())
            {
                throw Invalid();
            }
            if (turns.TryGetValue(number, out var old) && !SameUsage(old, turn))
            {
                // An ambiguous duplicate cannot overwrite the last confirmed ledger.
                throw Invalid();
            }
            turns[number] = turn;
        }

        var incomplete = snapshot.Session.UsageIsIncomplete ||
                         turns.Values.Any(turn => turn.UsageIsIncomplete);
        var session = new SessionRecord
        {
            Client = Client,
            ClientSessionId = snapshot.SessionId,
            ParentClientSessionId = null,
            ProjectPath = summary.Info?.Cwd,
            Model = snapshot.Session.Model(),
            StartedAtMs = TimestampMs(summary.CreatedAt),
            EndedAtMs = TimestampMs(snapshot.UpdatedAt) ??
                        TimestampMs(summary.LastActiveAt) ??
                        TimestampMs(summary.UpdatedAt),
            SemanticsId = TokenSemanticsCatalog.Grok.Id,
            TotalBasis = SessionTotalBasis.ClientReported,
            Coverage = Coverage(turns, snapshot.Session, incomplete),
            Usage = AddIncomplete(NormalizeUsage(snapshot.Session.Raw()), incomplete),
            SourceKind = SourceKind.Measured,
            CostSource = CostSource.None,
            ClientCostUsd = null,
        };

        var messages = new List<MessageRecord>(turns.Count);
        foreach (var (number, turn) in turns)
        {
            var endedAt = TimestampMs(turn.EndedAt);
            messages.Add(new MessageRecord
            {
                Client = Client,
                ClientSessionId = snapshot.SessionId,
                DedupKey = "turn:" + number.ToString(CultureInfo.InvariantCulture),
                Seq = number,
                CreatedAtMs = endedAt,
                CompletedAtMs = endedAt,
                Model = turn.Model(),
                Agent = null,
                Usage = AddIncomplete(NormalizeUsage(turn.Raw()), turn.UsageIsIncomplete),
                IsSidechain = false,
                SemanticsId = TokenSemanticsCatalog.Grok.Id,
                SourceKind = SourceKind.Measured,
                SourceRef = "grok.turn.v1:turnNumber=" + number.ToString(CultureInfo.InvariantCulture) +
                            ";timeBasis=turnEndedAt",
                CostSource = CostSource.None,
                ClientCostUsd = null,
            });
        }
        return SessionImport.Create(session, messages, sourcePath);
    }

    private static MessageCoverage Coverage(SortedDictionary<long, GrokUsage> turns,
        GrokUsage session, bool incomplete)
    {
        if (turns.Count == 0)
        {
            return MessageCoverage.NoMessages;
        }
        if (incomplete || !HasCompleteCounts(session) ||
            turns.Values.Any(turn => !HasCompleteCounts(turn)) ||
            !SameRawSum(turns.Values, session) ||
            session.TurnCount is not { } turnCount || turnCount != (ulong)turns.Count ||
            session.ModelCalls is not { } modelCalls ||
            SumCalls(turns.Values) is not { } sumCalls || modelCalls != sumCalls)
        {
            return MessageCoverage.Partial;
        }
        return MessageCoverage.Complete;
    }

    private static bool HasCompleteCounts(GrokUsage usage) =>
        usage.InputTokens is not null &&
        usage.OutputTokens is not null &&
        usage.CachedReadTokens is not null &&
        usage.CacheCreationTokens is not null &&
        usage.ReasoningTokens is not null &&
        usage.TotalTokens is not null;

    private static bool SameRawSum(IEnumerable<GrokUsage> turns, GrokUsage session)
    {
        if (session.InputTokens is not { } sessionInput ||
            session.OutputTokens is not { } sessionOutput ||
            session.CachedReadTokens is not { } sessionCacheRead ||
            session.CacheCreationTokens is not { } sessionCacheWrite ||
            session.ReasoningTokens is not { } sessionReasoning ||
            session.TotalTokens is not { } sessionTotal)
        {
            return false;
        }

        ulong input = 0;
        ulong output = 0;
        ulong cacheRead = 0;
        ulong cacheWrite = 0;
        ulong reasoning = 0;
        ulong total = 0;
        foreach (var turn in turns)
        {
            if (turn.InputTokens is not { } turnInput ||
                turn.OutputTokens is not { } turnOutput ||
                turn.CachedReadTokens is not { } turnCacheRead ||
                turn.CacheCreationTokens is not { } turnCacheWrite ||
                turn.ReasoningTokens is not { } turnReasoning ||
                turn.TotalTokens is not { } turnTotal)
            {
                return false;
            }
            input = SaturatingAdd(input, turnInput);
            output = SaturatingAdd(output, turnOutput);
            cacheRead = SaturatingAdd(cacheRead, turnCacheRead);
            cacheWrite = SaturatingAdd(cacheWrite, turnCacheWrite);
            reasoning = SaturatingAdd(reasoning, turnReasoning);
            total = SaturatingAdd(total, turnTotal);
        }
        return input == sessionInput &&
               output == sessionOutput &&
               cacheRead == sessionCacheRead &&
               cacheWrite == sessionCacheWrite &&
               reasoning == sessionReasoning &&
               total == sessionTotal;
    }

    private static ulong? SumCalls(IEnumerable<GrokUsage> turns)
    {
        ulong sum = 0;
        foreach (var turn in turns)
        {
            if (turn.ModelCalls is not { } calls || sum > ulong.MaxValue - calls)
            {
                return null;
            }
            sum += calls;
        }
        return sum;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        left > ulong.MaxValue - right ? ulong.MaxValue : left + right;

    private static NormalizedUsage NormalizeUsage(RawTokenCounts raw)
    {
        var unknown = new List<string>();
        var input = Subset(raw.Input, raw.CacheRead,
            TokenSemanticsCatalog.UnknownCacheReadMissing,
            TokenSemanticsCatalog.AnomalyCacheReadExceedsInput, unknown);
        var output = Subset(raw.Output, raw.Reasoning,
            TokenSemanticsCatalog.UnknownReasoningMissing,
            TokenSemanticsCatalog.AnomalyReasoningExceedsOutput, unknown);
        if (raw.CacheWrite is not null)
        {
            unknown.Add(TokenSemanticsCatalog.UnknownCacheWriteRelation);
        }

        UsageTotalBasis totalBasis;
        if (raw.ClientTotal is not null)
        {
            totalBasis = UsageTotalBasis.ClientReported;
        }
        else
        {
            unknown.Add(TokenSemanticsCatalog.UnknownTotalIncomplete);
            totalBasis = UsageTotalBasis.Unavailable;
        }

        var comparable = raw.CacheWrite is 0;
        ulong? nativeSum = raw.Input is { } inputCount && raw.Output is { } outputCount
            ? (ulong)inputCount + (ulong)outputCount
            : null;
        var mismatch = comparable && raw.ClientTotal is { } reported &&
                       nativeSum is { } sum && (ulong)reported != sum;

        unknown.Sort(StringComparer.Ordinal);
        var deduped = new List<string>(unknown.Count);
        foreach (var relation in unknown)
        {
            if (deduped.Count == 0 || !string.Equals(deduped[^1], relation, StringComparison.Ordinal))
            {
                deduped.Add(relation);
            }
        }
        return new NormalizedUsage(
            new TokenBuckets(input, output, raw.Reasoning, raw.CacheRead, raw.CacheWrite),
            raw.ClientTotal,
            totalBasis,
            deduped,
            mismatch);
    }

    private static long? Subset(long? full, long? part, string missing, string anomaly,
        List<string> unknown)
    {
        if (full is not { } fullValue)
        {
            return null;
        }
        if (part is not { } partValue)
        {
            unknown.Add(missing);
            return null;
        }
        if (fullValue >= partValue)
        {
            return fullValue - partValue;
        }
        unknown.Add(anomaly);
        return null;
    }

    private static NormalizedUsage AddIncomplete(NormalizedUsage usage, bool incomplete)
    {
        if (!incomplete)
        {
            return usage;
        }
        var unknown = usage.UnknownRelations.ToList();
        unknown.Add(IncompleteRelation);
        unknown.Sort(StringComparer.Ordinal);
        return usage with { UnknownRelations = unknown };
    }

    private static bool SameSnapshot(SessionImport left, SessionImport right)
    {
        if (!SameUsage(left.Session.Usage, right.Session.Usage) ||
            left.Messages.Count != right.Messages.Count)
        {
            return false;
        }
        for (var index = 0; index < left.Messages.Count; index++)
        {
            if (!string.Equals(left.Messages[index].DedupKey, right.Messages[index].DedupKey,
                    StringComparison.Ordinal) ||
                !SameUsage(left.Messages[index].Usage, right.Messages[index].Usage))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SameUsage(NormalizedUsage left, NormalizedUsage right) =>
        left.Buckets == right.Buckets &&
        left.TotalTokens == right.TotalTokens &&
        left.TotalBasis == right.TotalBasis &&
        left.ClientTotalMismatch == right.ClientTotalMismatch &&
        left.UnknownRelations.SequenceEqual(right.UnknownRelations, StringComparer.Ordinal);

    private static bool SameUsage(GrokUsage left, GrokUsage right)
    {
        if (left.InputTokens != right.InputTokens ||
            left.OutputTokens != right.OutputTokens ||
            left.CachedReadTokens != right.CachedReadTokens ||
            left.CacheCreationTokens != right.CacheCreationTokens ||
            left.ReasoningTokens != right.ReasoningTokens ||
            left.TotalTokens != right.TotalTokens ||
            left.ModelCalls != right.ModelCalls ||
            left.TurnCount != right.TurnCount ||
            left.TurnNumber != right.TurnNumber ||
            left.UsageIsIncomplete != right.UsageIsIncomplete ||
            !string.Equals(left.PrimaryModelId, right.PrimaryModelId, StringComparison.Ordinal) ||
            !string.Equals(left.EndedAt, right.EndedAt, StringComparison.Ordinal) ||
            left.ModelUsage.Count != right.ModelUsage.Count)
        {
            return false;
        }
        foreach (var (key, usage) in left.ModelUsage)
        {
            if (!right.ModelUsage.TryGetValue(key, out var other) || !SameUsage(usage, other))
            {
                return false;
            }
        }
        return true;
    }

    private static int CompareNullable(long? left, long? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }
        return right is null ? 1 : left.Value.CompareTo(right.Value);
    }

    /// <summary>Grok 的纳秒 UTC 时间戳；只接受 Z 与 +00:00 两种形式。</summary>
    private static long? TimestampMs(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string seconds;
        if (value.EndsWith('Z'))
        {
            seconds = value[..^1];
        }
        else if (value.EndsWith("+00:00", StringComparison.Ordinal))
        {
            seconds = value[..^6];
        }
        else
        {
            return null;
        }

        string? fraction = null;
        var dot = seconds.IndexOf('.');
        if (dot >= 0)
        {
            fraction = seconds[(dot + 1)..];
            seconds = seconds[..dot];
        }
        if (seconds.Length != 19)
        {
            return null;
        }
        if (fraction is not null &&
            (fraction.Length is 0 or > 9 || !fraction.All(char.IsAsciiDigit)))
        {
            return null;
        }
        var millisText = fraction is null
            ? null
            : fraction[..Math.Min(3, fraction.Length)].PadRight(3, '0');

        if (seconds[4] != '-' || seconds[7] != '-' || seconds[10] != 'T' ||
            seconds[13] != ':' || seconds[16] != ':')
        {
            return null;
        }
        if (!TryParseComponent(seconds[..4], out var year) ||
            !TryParseComponent(seconds[5..7], out var month) ||
            !TryParseComponent(seconds[8..10], out var day) ||
            !TryParseComponent(seconds[11..13], out var hour) ||
            !TryParseComponent(seconds[14..16], out var minute) ||
            !TryParseComponent(seconds[17..19], out var second))
        {
            return null;
        }
        if (month is < 1 or > 12 || day is < 1 or > 31 ||
            hour > 23 || minute > 59 || second > 60)
        {
            return null;
        }
        var millis = 0L;
        if (millisText is not null &&
            !long.TryParse(millisText, NumberStyles.None, CultureInfo.InvariantCulture, out millis))
        {
            return null;
        }

        var days = DaysFromCivil(year, month, day);
        return ((days * 86_400 + hour * 3_600 + minute * 60 + second) * 1_000) + millis;
    }

    private static bool TryParseComponent(string value, out long result) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);

    private static long DaysFromCivil(long year, long month, long day)
    {
        year = month <= 2 ? year - 1 : year;
        var era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - era * 400;
        var dayOfYear = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
        var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        return era * 146_097 + dayOfEra - 719_468;
    }

    private static InvalidDataException Invalid() =>
        new("unsupported Grok statistics snapshot");

    private sealed record Snapshot(
        string SessionId,
        string? UpdatedAt,
        GrokUsage Session,
        List<GrokUsage> Turns);

    private sealed record Summary(
        Info? Info = null,
        string? CreatedAt = null,
        string? UpdatedAt = null,
        string? LastActiveAt = null);

    private sealed record Info(string? Id, string? Cwd);

    private sealed class GrokUsage
    {
        public GrokUsage(
            ulong? inputTokens,
            ulong? outputTokens,
            ulong? cachedReadTokens,
            ulong? cacheCreationTokens,
            ulong? reasoningTokens,
            ulong? totalTokens,
            ulong? modelCalls,
            ulong? turnCount,
            long? turnNumber,
            string? primaryModelId,
            string? endedAt,
            bool usageIsIncomplete,
            Dictionary<string, GrokUsage> modelUsage)
        {
            InputTokens = inputTokens;
            OutputTokens = outputTokens;
            CachedReadTokens = cachedReadTokens;
            CacheCreationTokens = cacheCreationTokens;
            ReasoningTokens = reasoningTokens;
            TotalTokens = totalTokens;
            ModelCalls = modelCalls;
            TurnCount = turnCount;
            TurnNumber = turnNumber;
            PrimaryModelId = primaryModelId;
            EndedAt = endedAt;
            UsageIsIncomplete = usageIsIncomplete;
            ModelUsage = modelUsage;
        }

        public ulong? InputTokens { get; }
        public ulong? OutputTokens { get; }
        public ulong? CachedReadTokens { get; }
        public ulong? CacheCreationTokens { get; }
        public ulong? ReasoningTokens { get; }
        public ulong? TotalTokens { get; }
        public ulong? ModelCalls { get; }
        public ulong? TurnCount { get; }
        public long? TurnNumber { get; }
        public string? PrimaryModelId { get; }
        public string? EndedAt { get; }
        public bool UsageIsIncomplete { get; }
        public Dictionary<string, GrokUsage> ModelUsage { get; }

        public RawTokenCounts Raw() => new(
            ToCount(InputTokens),
            ToCount(OutputTokens),
            ToCount(CachedReadTokens),
            ToCount(CacheCreationTokens),
            ToCount(ReasoningTokens),
            ToCount(TotalTokens));

        public string? Model()
        {
            if (PrimaryModelId is { } primary && !string.IsNullOrWhiteSpace(primary))
            {
                return primary;
            }
            return ModelUsage.Count == 1 ? ModelUsage.Keys.First() : null;
        }

        public bool Valid() =>
            IsSqliteCount(InputTokens) &&
            IsSqliteCount(OutputTokens) &&
            IsSqliteCount(CachedReadTokens) &&
            IsSqliteCount(CacheCreationTokens) &&
            IsSqliteCount(ReasoningTokens) &&
            IsSqliteCount(TotalTokens) &&
            IsSqliteCount(ModelCalls) &&
            IsSqliteCount(TurnCount) &&
            ModelUsage.Values.All(usage => usage.Valid());

        private static bool IsSqliteCount(ulong? value) =>
            value is not { } count || count <= long.MaxValue;

        private static long? ToCount(ulong? value) =>
            value is { } count ? checked((long)count) : null;
    }
}
