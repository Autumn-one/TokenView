using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>Gemini CLI 会话日志；只回放统计所需元数据，排除 Antigravity。</summary>
public sealed class GeminiCliCollector : IClientCollector
{
    private const string Client = "gemini_cli";
    private const int ParserVersion = 1;
    private const long ProjectMarkerLimit = 16384;
    private const string InputSplitMissingCache = "gemini_cli.input_split_missing_cache";
    private const string ToolRelationUnverified = "gemini_cli.tool_relation_unverified";

    private static readonly HashSet<string> KnownRecordKinds =
        new(["user", "gemini", "info", "error"], StringComparer.Ordinal);

    private readonly string? _root;

    public GeminiCliCollector() : this(DefaultRoot())
    {
    }

    public GeminiCliCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    public static string? DefaultRoot() =>
        ResolveRoot(Environment.GetEnvironmentVariable("GEMINI_CLI_HOME"), CollectorSupport.HomeDir());

    /// <summary>安装版 Storage 把 GEMINI_CLI_HOME 当作用户 home，再追加 .gemini。</summary>
    public static string? ResolveRoot(string? config, string? home)
    {
        var userHome = string.IsNullOrEmpty(config) ? home : config;
        return userHome is null ? null : Path.Combine(userHome, ".gemini");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_root is null || !Path.Exists(_root) || !Path.Exists(Path.Combine(_root, "tmp")))
        {
            report.RootMissing = true;
            return report;
        }

        var journals = Discover(_root, report);
        var paths = SourcePathsFor(_root, journals);
        var sources = new List<SourceFile>(paths.Count);
        var unchanged = !options.ForceRescan;
        foreach (var path in paths)
        {
            var (size, mtime) = Fingerprint(path);
            var old = store.SourceFile(Client, path);
            // 已有日志被清空可能是一次尚未完成的改写。
            if (size == 0 && old?.SizeBytes is > 0)
            {
                throw SyncException.ChangedDuringScan();
            }
            unchanged &= old is not null && old.SizeBytes == size && old.MtimeMs == mtime &&
                         old.ParserVersion == ParserVersion;
            sources.Add(CollectorSupport.NewSourceFile(Client, path, SourceFileKind.Jsonl,
                size, mtime, size, ParserVersion, 0));
        }
        unchanged &= store.SourcePaths(Client).SequenceEqual(sources.Select(source => source.Path),
            StringComparer.Ordinal);
        if (unchanged)
        {
            report.FilesSkippedUnchanged = journals.Count;
            return report;
        }

        var sessions = new SortedDictionary<string, SessionImport>(StringComparer.Ordinal);
        foreach (var path in journals)
        {
            ParsedJournal parsed;
            try
            {
                parsed = ParseFile(path);
            }
            catch (Exception error) when (IsParseFailure(error))
            {
                report.FilesFailed++;
                continue;
            }

            report.FilesParsed++;
            report.UnparsedLines += parsed.Unparsed;
            if (!parsed.Nonempty && store.SourceFile(Client, path)?.SizeBytes is > 0)
            {
                throw SyncException.ChangedDuringScan();
            }
            var index = sources.FindIndex(source => string.Equals(source.Path, path, StringComparison.Ordinal));
            if (index >= 0)
            {
                sources[index] = sources[index] with { UnparsedRecords = parsed.Unparsed };
            }
            if (parsed.Metadata.SessionId is not { } id)
            {
                continue;
            }

            var project = ResolveProject(_root, path, parsed.Metadata);
            var import = BuildImport(id, project, parsed, path);
            if (sessions.TryGetValue(id, out var previous))
            {
                // 同一会话的镜像只是快照，绝不叠加 token。
                if (import.Session.EndedAtMs == previous.Session.EndedAtMs && !SameSnapshot(previous, import))
                {
                    report.FilesFailed++;
                    continue;
                }
                if (!EndedAtLeast(import.Session.EndedAtMs, previous.Session.EndedAtMs))
                {
                    continue;
                }
            }
            sessions[id] = import;
        }
        // 回放失败可能意味着身份改写、压缩或镜像变化；没有完整日志集时保留全部历史。
        if (report.FilesFailed > 0)
        {
            return report;
        }

        var newJournals = Discover(_root, new SyncReport());
        if (!newJournals.SequenceEqual(journals, StringComparer.Ordinal) ||
            !SourcePathsFor(_root, newJournals).SequenceEqual(paths, StringComparer.Ordinal))
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
        var imported = store.ReplaceClientSnapshot(Client, sources, imports);
        report.SessionsImported = imports.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private static List<string> Discover(string root, SyncReport report)
    {
        var tmp = Path.Combine(root, "tmp");
        if (!Path.Exists(tmp))
        {
            report.FilesSeen = 0;
            return [];
        }
        var paths = new List<string>();
        foreach (var entry in EnumerateEntries(tmp))
        {
            var attributes = EntryAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !attributes.HasFlag(FileAttributes.Directory))
            {
                continue;
            }
            var chats = Path.Combine(entry, "chats");
            if (!Path.Exists(chats))
            {
                continue;
            }
            var chatsAttributes = EntryAttributes(chats);
            if (chatsAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }
            DiscoverFiles(chats, paths);
        }
        paths.Sort(PathOrderComparer.Instance);
        report.FilesSeen = paths.Count;
        return paths;
    }

    private static void DiscoverFiles(string root, List<string> files)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            foreach (var entry in EnumerateEntries(directory))
            {
                var attributes = EntryAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    directories.Push(entry);
                }
                else if (IsJournal(entry))
                {
                    files.Add(entry);
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.Directory(error);
        }
    }

    private static FileAttributes EntryAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw SyncException.FromSource(error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
    }

    private static bool IsJournal(string path)
    {
        var name = Path.GetFileName(path);
        return !string.Equals(name, ".jsonl", StringComparison.Ordinal) &&
               name.EndsWith(".jsonl", StringComparison.Ordinal);
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

    private static List<string> SourcePathsFor(string root, List<string> journals)
    {
        var paths = new SortedSet<string>(PathOrderComparer.Instance);
        foreach (var journal in journals)
        {
            paths.Add(journal);
        }
        foreach (var journal in journals)
        {
            if (ProjectMarker(root, journal) is { } marker)
            {
                paths.Add(marker);
            }
        }
        return paths.ToList();
    }

    private static string? ProjectMarker(string root, string journal)
    {
        var tmp = Path.Combine(root, "tmp");
        if (!journal.StartsWith(tmp, StringComparison.Ordinal))
        {
            return null;
        }
        var remainder = journal[tmp.Length..];
        if (remainder.Length == 0 ||
            (remainder[0] != Path.DirectorySeparatorChar && remainder[0] != Path.AltDirectorySeparatorChar))
        {
            return null;
        }
        remainder = remainder[1..];
        var separator = remainder.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var project = separator < 0 ? remainder : remainder[..separator];
        if (project.Length == 0)
        {
            return null;
        }
        foreach (var baseName in new[] { "tmp", "history" })
        {
            var marker = Path.Combine(root, baseName, project, ".project_root");
            if (!Path.Exists(marker))
            {
                continue;
            }
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(marker);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw SyncException.FromSource(error);
            }
            if (!attributes.HasFlag(FileAttributes.Directory) &&
                !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return marker;
            }
        }
        return null;
    }

    private static string? ResolveProject(string root, string journal, Metadata metadata)
    {
        if (metadata.Directories is { } directories)
        {
            return directories.Count > 0 ? directories[0] : null;
        }
        if (ProjectMarker(root, journal) is not { } marker)
        {
            return null;
        }
        long length;
        try
        {
            length = new FileInfo(marker).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
        if (length > ProjectMarkerLimit)
        {
            throw SyncException.FromSource(
                new InvalidDataException("invalid Gemini project marker"));
        }
        string value;
        try
        {
            value = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(marker));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          DecoderFallbackException)
        {
            throw SyncException.FromSource(error);
        }
        value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    private static ParsedJournal ParseFile(string path)
    {
        var state = new JournalState();
        CollectorSupport.ReadJsonLines(path, (line, _) =>
        {
            if (CollectorSupport.IsEmptyLine(line))
            {
                return;
            }
            state.Nonempty = true;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                throw InvalidJournal();
            }
            using (document)
            {
                var element = document.RootElement;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidJournal();
                }
                var metadata = ReadMetadata(element);
                var patch = ReadOptionalObject(element, "$set") is { } patchElement
                    ? ReadMetadata(patchElement)
                    : null;
                var rewind = ReadOptionalString(element, "$rewindTo");
                var id = ReadOptionalString(element, "id");
                var kind = ReadOptionalString(element, "type");
                var timestamp = ReadOptionalString(element, "timestamp");
                var model = ReadOptionalString(element, "model");
                var tokens = ReadOptionalUsage(element, "tokens");

                if (rewind is not null)
                {
                    var index = state.Order.FindIndex(candidate =>
                        string.Equals(candidate, rewind, StringComparison.Ordinal));
                    if (index < 0)
                    {
                        index = 0;
                    }
                    foreach (var removed in state.Order.Skip(index).ToList())
                    {
                        state.Records.Remove(removed);
                    }
                    state.Order.RemoveRange(index, state.Order.Count - index);
                }
                else if (id is not null)
                {
                    if (kind is null)
                    {
                        throw InvalidJournal();
                    }
                    Push(new Record(id, kind, timestamp, model, tokens), state);
                }
                else if (patch is not null)
                {
                    if (patch.Messages is { } messages)
                    {
                        // $set.messages 全量替换会话（例如压缩）。
                        state.Order.Clear();
                        state.Records.Clear();
                        foreach (var message in messages)
                        {
                            Push(message, state);
                        }
                    }
                    Update(state.Metadata, patch);
                }
                else if (metadata.SessionId is not null && metadata.ProjectHash is not null)
                {
                    if (metadata.Messages is { } messages)
                    {
                        foreach (var message in messages)
                        {
                            Push(message, state);
                        }
                    }
                    Update(state.Metadata, metadata);
                }
                else
                {
                    state.Unparsed++;
                }
            }
        });

        if (state.Nonempty && string.IsNullOrEmpty(state.Metadata.SessionId))
        {
            throw InvalidJournal();
        }
        // 新的操作式格式不得静默清空既有用量。
        if (state.Unparsed > 0 && state.Records.Count == 0)
        {
            throw InvalidJournal();
        }
        var records = state.Order
            .Where(id => state.Records.ContainsKey(id))
            .Select(id => state.Records[id])
            .ToList();
        return new ParsedJournal(state.Metadata, records, state.Unparsed, state.Nonempty);
    }

    private static void Push(Record record, JournalState state)
    {
        if (record.Id.Length == 0)
        {
            throw InvalidJournal();
        }
        if (!KnownRecordKinds.Contains(record.Kind))
        {
            state.Unparsed++;
        }
        if (record.Tokens is { } tokens && !tokens.Valid)
        {
            throw InvalidJournal();
        }
        if (!state.Records.ContainsKey(record.Id))
        {
            state.Order.Add(record.Id);
        }
        // 与安装版 pushMessage 一致：写入完整记录而不是字段级补丁。
        state.Records[record.Id] = record;
    }

    private static void Update(Metadata target, Metadata patch)
    {
        if (patch.SessionId is not null) target.SessionId = patch.SessionId;
        if (patch.ProjectHash is not null) target.ProjectHash = patch.ProjectHash;
        if (patch.StartTime is not null) target.StartTime = patch.StartTime;
        if (patch.LastUpdated is not null) target.LastUpdated = patch.LastUpdated;
        if (patch.Kind is not null) target.Kind = patch.Kind;
        if (patch.Directories is not null) target.Directories = patch.Directories;
    }

    private static Metadata ReadMetadata(JsonElement element) => new()
    {
        SessionId = ReadOptionalString(element, "sessionId"),
        ProjectHash = ReadOptionalString(element, "projectHash"),
        StartTime = ReadOptionalString(element, "startTime"),
        LastUpdated = ReadOptionalString(element, "lastUpdated"),
        Kind = ReadOptionalString(element, "kind"),
        Directories = ReadOptionalStringList(element, "directories"),
        Messages = ReadOptionalRecords(element, "messages"),
    };

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidJournal();
        }
        return value.GetString();
    }

    private static JsonElement? ReadOptionalObject(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidJournal();
        }
        return value;
    }

    private static List<string>? ReadOptionalStringList(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidJournal();
        }
        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw InvalidJournal();
            }
            items.Add(item.GetString() ?? string.Empty);
        }
        return items;
    }

    private static List<Record>? ReadOptionalRecords(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidJournal();
        }
        var records = new List<Record>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw InvalidJournal();
            }
            records.Add(ReadRecord(item));
        }
        return records;
    }

    private static Record ReadRecord(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String)
        {
            throw InvalidJournal();
        }
        return new Record(
            id.GetString() ?? string.Empty,
            kind.GetString() ?? string.Empty,
            ReadOptionalString(element, "timestamp"),
            ReadOptionalString(element, "model"),
            ReadOptionalUsage(element, "tokens"));
    }

    private static GeminiUsage? ReadOptionalUsage(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidJournal();
        }
        return new GeminiUsage(
            ReadOptionalU64(value, "input"),
            ReadOptionalU64(value, "output"),
            ReadOptionalU64(value, "cached"),
            ReadOptionalU64(value, "thoughts"),
            ReadOptionalU64(value, "tool"),
            ReadOptionalU64(value, "total"));
    }

    private static ulong? ReadOptionalU64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var count))
        {
            throw InvalidJournal();
        }
        return count;
    }

    private static SessionImport BuildImport(string id, string? project, ParsedJournal parsed, string sourcePath)
    {
        var sidechain = string.Equals(parsed.Metadata.Kind, "subagent", StringComparison.Ordinal);
        var messages = new List<MessageRecord>();
        var messagePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var seq = 0; seq < parsed.Records.Count; seq++)
        {
            var record = parsed.Records[seq];
            if (!string.Equals(record.Kind, "gemini", StringComparison.Ordinal))
            {
                continue;
            }
            var usage = Normalize(record.Tokens ?? default);
            var message = new MessageRecord
            {
                Client = Client,
                ClientSessionId = id,
                DedupKey = record.Id,
                Seq = seq,
                CreatedAtMs = ParseIso8601UtcMs(record.Timestamp),
                CompletedAtMs = null,
                Model = record.Model,
                Agent = null,
                Usage = usage,
                IsSidechain = sidechain,
                SemanticsId = TokenSemanticsCatalog.GeminiCli.Id,
                SourceKind = usage.TotalBasis == UsageTotalBasis.DerivedFromBuckets
                    ? SourceKind.Derived
                    : SourceKind.Measured,
                SourceRef = $"message:{record.Id}",
            };
            messages.Add(message);
            messagePaths[record.Id] = sourcePath;
        }
        var session = BuildSession(id, project, parsed.Metadata.StartTime, parsed.Metadata.LastUpdated,
            messages, parsed.Unparsed > 0);
        return new SessionImport(session, messages, sourcePath, messagePaths);
    }

    /// <summary>prompt 含缓存；thoughts 是独立输出；非零 tool 关系未经验证。</summary>
    private static NormalizedUsage Normalize(GeminiUsage raw)
    {
        var unknown = new List<string>();
        long? input = null;
        if (raw.Input is { } rawInput && raw.Cached is { } cached)
        {
            if (rawInput >= cached)
            {
                input = (long)(rawInput - cached);
            }
            else
            {
                unknown.Add(TokenSemanticsCatalog.AnomalyCacheReadExceedsInput);
            }
        }
        if (raw.Input is not null && raw.Cached is null)
        {
            unknown.Add(InputSplitMissingCache);
        }
        if (raw.Tool != 0UL)
        {
            unknown.Add(ToolRelationUnverified);
        }

        long? cacheRead = raw.Cached is { } cachedValue ? (long)cachedValue : null;
        long? output = raw.Output is { } outputValue ? (long)outputValue : null;
        long? reasoning = raw.Thoughts is { } thoughtsValue ? (long)thoughtsValue : null;

        long? sum = null;
        if (raw.Tool == 0UL && input is { } inputValue && cacheRead is { } cacheValue &&
            output is { } outputBucket && reasoning is { } reasoningBucket)
        {
            sum = CheckedSum(inputValue, cacheValue, outputBucket, reasoningBucket);
        }

        long? clientTotal = raw.Total is { } totalValue ? (long)totalValue : null;
        long? total;
        UsageTotalBasis totalBasis;
        if (clientTotal is not null)
        {
            total = clientTotal;
            totalBasis = UsageTotalBasis.ClientReported;
        }
        else if (sum is not null)
        {
            total = sum;
            totalBasis = UsageTotalBasis.DerivedFromBuckets;
        }
        else
        {
            unknown.Add(TokenSemanticsCatalog.UnknownTotalIncomplete);
            total = null;
            totalBasis = UsageTotalBasis.Unavailable;
        }
        unknown.Sort(StringComparer.Ordinal);

        return new NormalizedUsage(
            new TokenBuckets(input, output, reasoning, cacheRead, null),
            total,
            totalBasis,
            unknown,
            clientTotal is { } reported && sum is { } derived && reported != derived);
    }

    private static long? CheckedSum(params long[] values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (total > long.MaxValue - value)
            {
                return null;
            }
            total += value;
        }
        return total;
    }

    private static SessionRecord BuildSession(string id, string? project, string? startTime,
        string? lastUpdated, List<MessageRecord> messages, bool partial)
    {
        var unknown = new List<string>();
        foreach (var message in messages)
        {
            unknown.AddRange(message.Usage.UnknownRelations);
        }
        var total = SumTotals(messages);
        if (total is null)
        {
            unknown.Add(TokenSemanticsCatalog.UnknownTotalIncomplete);
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

        long? ended = ParseIso8601UtcMs(lastUpdated);
        foreach (var message in messages)
        {
            ended = MaxNullable(ended, message.CompletedAtMs ?? message.CreatedAtMs);
        }

        MessageRecord? modelMessage = null;
        foreach (var message in messages)
        {
            if (message.Model is null)
            {
                continue;
            }
            if (modelMessage is null || CompareNullable(message.CreatedAtMs, modelMessage.CreatedAtMs) >= 0)
            {
                modelMessage = message;
            }
        }

        var usage = new NormalizedUsage(
            new TokenBuckets(
                SumField(messages, buckets => buckets.Input),
                SumField(messages, buckets => buckets.Output),
                SumField(messages, buckets => buckets.Reasoning),
                SumField(messages, buckets => buckets.CacheRead),
                null),
            total,
            total is null ? UsageTotalBasis.Unavailable : UsageTotalBasis.DerivedFromBuckets,
            deduped,
            messages.Any(message => message.Usage.ClientTotalMismatch));

        return new SessionRecord
        {
            Client = Client,
            ClientSessionId = id,
            ParentClientSessionId = null,
            ProjectPath = project,
            Model = modelMessage?.Model,
            StartedAtMs = ParseIso8601UtcMs(startTime),
            EndedAtMs = ended,
            SemanticsId = TokenSemanticsCatalog.GeminiCli.Id,
            TotalBasis = SessionTotalBasis.SumOfMessages,
            Coverage = partial
                ? MessageCoverage.Partial
                : messages.Count == 0
                    ? MessageCoverage.NoMessages
                    : MessageCoverage.Complete,
            Usage = usage,
            SourceKind = SourceKind.Derived,
        };
    }

    private static long? SumField(List<MessageRecord> messages, Func<TokenBuckets, long?> selector)
    {
        if (messages.Count == 0)
        {
            return null;
        }
        long total = 0;
        foreach (var message in messages)
        {
            if (selector(message.Usage.Buckets) is not { } value || total > long.MaxValue - value)
            {
                return null;
            }
            total += value;
        }
        return total;
    }

    private static long? SumTotals(List<MessageRecord> messages)
    {
        if (messages.Count == 0)
        {
            return null;
        }
        long total = 0;
        foreach (var message in messages)
        {
            if (message.Usage.TotalTokens is not { } value || total > long.MaxValue - value)
            {
                return null;
            }
            total += value;
        }
        return total;
    }

    private static long? MaxNullable(long? left, long? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left.Value >= right.Value ? left : right;
    }

    private static int CompareNullable(long? left, long? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        return left.Value.CompareTo(right.Value);
    }

    private static bool EndedAtLeast(long? candidate, long? existing) =>
        candidate is { } value ? existing is null || value >= existing.Value : existing is null;

    private static bool SameSnapshot(SessionImport left, SessionImport right)
    {
        if (!string.Equals(left.Session.ProjectPath, right.Session.ProjectPath, StringComparison.Ordinal) ||
            left.Session.StartedAtMs != right.Session.StartedAtMs ||
            !string.Equals(left.Session.Model, right.Session.Model, StringComparison.Ordinal) ||
            !SameUsage(left.Session.Usage, right.Session.Usage) ||
            left.Messages.Count != right.Messages.Count)
        {
            return false;
        }
        for (var index = 0; index < left.Messages.Count; index++)
        {
            var a = left.Messages[index];
            var b = right.Messages[index];
            if (!string.Equals(a.DedupKey, b.DedupKey, StringComparison.Ordinal) ||
                !string.Equals(a.Model, b.Model, StringComparison.Ordinal) ||
                a.CreatedAtMs != b.CreatedAtMs ||
                a.IsSidechain != b.IsSidechain ||
                !SameUsage(a.Usage, b.Usage))
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

    private static InvalidJournalException InvalidJournal() => new();

    private static bool IsParseFailure(Exception error) =>
        error is InvalidJournalException or JsonException or IOException or UnauthorizedAccessException or
            InvalidDataException or DecoderFallbackException or NotSupportedException;

    /// <summary>按路径组件比较，而不是按整串字节排序。</summary>
    private sealed class PathOrderComparer : IComparer<string>
    {
        public static readonly PathOrderComparer Instance = new();

        private static readonly char[] Separators = ['\\', '/'];

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var leftParts = Split(left);
            var rightParts = Split(right);
            var shared = Math.Min(leftParts.Length, rightParts.Length);
            for (var index = 0; index < shared; index++)
            {
                var comparison = string.CompareOrdinal(leftParts[index], rightParts[index]);
                if (comparison != 0) return comparison;
            }
            return leftParts.Length.CompareTo(rightParts.Length);
        }

        private static string[] Split(string path) =>
            path.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Where(component => !string.Equals(component, ".", StringComparison.Ordinal))
                .ToArray();
    }

    private sealed class InvalidJournalException : Exception
    {
    }

    private sealed class JournalState
    {
        public Metadata Metadata { get; } = new();
        public List<string> Order { get; } = [];
        public Dictionary<string, Record> Records { get; } = new(StringComparer.Ordinal);
        public int Unparsed { get; set; }
        public bool Nonempty { get; set; }
    }

    private sealed class Metadata
    {
        public string? SessionId { get; set; }
        public string? ProjectHash { get; set; }
        public string? StartTime { get; set; }
        public string? LastUpdated { get; set; }
        public string? Kind { get; set; }
        public List<string>? Directories { get; set; }
        public List<Record>? Messages { get; set; }
    }

    private sealed record Record(string Id, string Kind, string? Timestamp, string? Model,
        GeminiUsage? Tokens);

    private readonly record struct GeminiUsage(ulong? Input, ulong? Output, ulong? Cached,
        ulong? Thoughts, ulong? Tool, ulong? Total)
    {
        public bool Valid
        {
            get
            {
                foreach (var value in new[] { Input, Output, Cached, Thoughts, Tool, Total })
                {
                    if (value is { } count && count > long.MaxValue) return false;
                }
                return true;
            }
        }
    }

    private sealed record ParsedJournal(Metadata Metadata, List<Record> Records, int Unparsed,
        bool Nonempty);
}
