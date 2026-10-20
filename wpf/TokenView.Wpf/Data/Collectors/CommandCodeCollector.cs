using System.Globalization;
using System.IO;
using System.Text.Json;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>Command Code 转录（`~/.commandcode/projects/&lt;project&gt;/&lt;session&gt;.jsonl`）；只读取元数据。</summary>
public sealed class CommandCodeCollector : IClientCollector
{
    private const int ParserVersion = 1;
    private const string Client = "command_code";
    private static readonly string[] SidecarSuffixes = [".checkpoints", ".prompts"];

    private readonly string? _root;

    public CommandCodeCollector() : this(DefaultRoot())
    {
    }

    public CommandCodeCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    public static string? DefaultRoot() =>
        CollectorSupport.Combine(CollectorSupport.HomeDir(), ".commandcode", "projects");

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_root is null || !Directory.Exists(_root))
        {
            if (_root is not null && File.Exists(_root))
            {
                throw SyncException.Directory(new IOException("Command Code root is not a directory"));
            }
            report.RootMissing = true;
            return report;
        }

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

        var rollups = new Dictionary<string, Rollup>(StringComparer.Ordinal);
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var parsed = ParseFile(path);
            report.FilesParsed++;
            report.UnparsedLines += parsed.Unparsed;
            report.IgnoredLines += parsed.Ignored;
            sources[index] = sources[index] with { UnparsedRecords = parsed.Unparsed };
            if (!rollups.TryGetValue(parsed.Meta.Id, out var rollup))
            {
                rollup = new Rollup(parsed.Meta, path);
                rollups[parsed.Meta.Id] = rollup;
            }
            rollup.Partial |= parsed.Unparsed > 0;
            foreach (var candidate in parsed.Requests)
            {
                rollup.Push(candidate, path);
            }
        }

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

        var imports = rollups.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value.Finish())
            .ToList();
        var imported = store.ReplaceClientSnapshot(Client, sources, imports);
        report.SessionsImported = imports.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private static ParsedFile ParseFile(string path)
    {
        var parsed = new ParsedFile();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long ordinal = 1;
        var headerDone = false;

        try
        {
            CollectorSupport.ReadJsonLines(path, (line, complete) =>
            {
                if (!headerDone)
                {
                    if (CollectorSupport.IsEmptyLine(line)) return;
                    parsed.Meta = ParseHeader(line, path);
                    headerDone = true;
                    return;
                }

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
                    parsed.Unparsed++;
                    return;
                }

                using (document)
                {
                    var element = document.RootElement;
                    if (!TryReadTag(element, out var kind))
                    {
                        if (!complete) throw InvalidSource();
                        parsed.Unparsed++;
                        return;
                    }
                    if (!string.Equals(kind, "message", StringComparison.Ordinal))
                    {
                        parsed.Ignored++;
                        return;
                    }
                    if (!TryReadRow(element, out var row, out var hasUsage) || row is null)
                    {
                        parsed.Unparsed++;
                        return;
                    }
                    if (row.Role is null)
                    {
                        parsed.Unparsed++;
                        return;
                    }
                    if (!hasUsage || !string.Equals(row.Role, "assistant", StringComparison.Ordinal))
                    {
                        parsed.Ignored++;
                        return;
                    }

                    var raw = Counts(row.Usage);
                    if (!CollectorSupport.ValidCounts(raw))
                    {
                        parsed.Unparsed++;
                        return;
                    }

                    var dedupKey = string.IsNullOrEmpty(row.Id) ? $"line:{ordinal}" : row.Id;
                    if (!seen.Add(dedupKey))
                    {
                        parsed.Ignored++;
                        return;
                    }

                    var time = TimeMs(row);
                    parsed.Requests.Add(new Candidate(new MessageRecord
                    {
                        Client = Client,
                        ClientSessionId = parsed.Meta.Id,
                        DedupKey = dedupKey,
                        Seq = ordinal,
                        CreatedAtMs = time,
                        CompletedAtMs = time,
                        Model = row.Model,
                        Agent = row.Role,
                        Usage = UsageNormalizer.Normalize(TokenSemanticsCatalog.CommandCode, raw),
                        IsSidechain = false,
                        SemanticsId = TokenSemanticsCatalog.CommandCode.Id,
                        SourceRef = $"line={ordinal}",
                    }, raw));
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

        if (!headerDone) throw InvalidSource();
        return parsed;
    }

    private static CommandCodeSessionMeta ParseHeader(string line, string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException error)
        {
            throw SyncException.FromSource(error);
        }

        using (document)
        {
            var element = document.RootElement;
            if (!TryReadTag(element, out var kind) ||
                !string.Equals(kind, "session", StringComparison.Ordinal))
            {
                throw InvalidSource();
            }
            if (!TryOptionalString(element, "id", out var id) ||
                !TryOptionalString(element, "timestamp", out var timestamp) ||
                !TryOptionalString(element, "cwd", out var cwd))
            {
                throw InvalidSource();
            }

            var sessionId = !string.IsNullOrEmpty(id) ? id : FileStem(path);
            if (string.IsNullOrEmpty(sessionId)) throw InvalidSource();
            var start = ParseUtcMs(timestamp);
            return new CommandCodeSessionMeta(sessionId, cwd, start is >= 0 ? start : null);
        }
    }

    private static bool TryReadTag(JsonElement element, out string? kind)
    {
        kind = null;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty("type", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        kind = value.GetString();
        return true;
    }

    private static bool TryReadRow(JsonElement element, out RowFields? row, out bool hasUsage)
    {
        row = null;
        hasUsage = false;
        if (!TryOptionalString(element, "id", out var id) ||
            !TryOptionalString(element, "timestamp", out var timestamp) ||
            !TryOptionalString(element, "model", out var model))
        {
            return false;
        }

        var usage = default(RawTokenCounts);
        if (element.TryGetProperty("usage", out var usageValue) &&
            usageValue.ValueKind != JsonValueKind.Null)
        {
            if (usageValue.ValueKind != JsonValueKind.Object) return false;
            if (!TryOptionalU64(usageValue, "inputTokens", out var input) ||
                !TryOptionalU64(usageValue, "outputTokens", out var output) ||
                !TryOptionalU64(usageValue, "cacheReadTokens", out var cacheRead) ||
                !TryOptionalU64(usageValue, "cacheWriteTokens", out var cacheWrite))
            {
                return false;
            }
            usage = new RawTokenCounts(input, output, cacheRead, cacheWrite);
            hasUsage = true;
        }

        string? role = null;
        long? createdAt = null;
        if (element.TryGetProperty("message", out var message) &&
            message.ValueKind != JsonValueKind.Null)
        {
            if (message.ValueKind != JsonValueKind.Object) return false;
            if (!TryOptionalString(message, "role", out role)) return false;
            if (message.TryGetProperty("meta", out var meta) && meta.ValueKind != JsonValueKind.Null)
            {
                if (meta.ValueKind != JsonValueKind.Object) return false;
                if (meta.TryGetProperty("createdAt", out var created) &&
                    created.ValueKind != JsonValueKind.Null)
                {
                    if (created.ValueKind != JsonValueKind.Number || !created.TryGetInt64(out var value))
                    {
                        return false;
                    }
                    createdAt = value;
                }
            }
        }

        row = new RowFields(id, timestamp, model, role, createdAt, usage);
        return true;
    }

    private static bool TryOptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property)) return true;
        if (property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
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

    /// <summary>`inputTokens` 已包含两个缓存字段，求和前必须扣除；下溢时 input 置为未知。</summary>
    private static RawTokenCounts Counts(RawTokenCounts usage)
    {
        var cacheRead = usage.CacheRead ?? 0;
        var cacheWrite = usage.CacheWrite ?? 0;
        long? input = null;
        if (usage.Input is { } total && total >= cacheRead && total - cacheRead >= cacheWrite)
        {
            input = total - cacheRead - cacheWrite;
        }
        return new RawTokenCounts(input, usage.Output, usage.CacheRead, usage.CacheWrite);
    }

    private static long? TimeMs(RowFields row)
    {
        var time = ParseUtcMs(row.Timestamp);
        if (time is null && row.CreatedAtMs is { } created && created >= 0) return created;
        return time;
    }

    /// <summary>只接受 UTC `Z` 形式（`YYYY-MM-DDTHH:MM:SS[.fff]Z`），不猜测时区。</summary>
    private static long? ParseUtcMs(string? value)
    {
        if (value is null || value.Length < 20) return null;
        if (value[4] != '-' || value[7] != '-' || value[10] != 'T' ||
            value[13] != ':' || value[16] != ':' || value[^1] != 'Z')
        {
            return null;
        }
        if (!TryPart(value, 0, 4, out var year) ||
            !TryPart(value, 5, 2, out var month) ||
            !TryPart(value, 8, 2, out var day) ||
            !TryPart(value, 11, 2, out var hour) ||
            !TryPart(value, 14, 2, out var minute) ||
            !TryPart(value, 17, 2, out var second))
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
            var fraction = value[20..^1];
            if (fraction.Length is < 1 or > 3) return null;
            foreach (var character in fraction)
            {
                if (character is < '0' or > '9') return null;
            }
            millis = long.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);
        }

        var days = DaysFromCivil(year, month, day);
        return (days * 86_400 + hour * 3_600 + minute * 60 + second) * 1_000 + millis;
    }

    private static bool TryPart(string value, int start, int length, out long number) =>
        long.TryParse(value.AsSpan(start, length), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out number);

    private static long DaysFromCivil(long year, long month, long day)
    {
        if (month <= 2) year -= 1;
        var era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - era * 400;
        var dayOfYear = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
        var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        return era * 146_097 + dayOfEra - 719_468;
    }

    private static bool Accepts(string path)
    {
        var (stem, extension) = SplitName(path);
        if (!string.Equals(extension, "jsonl", StringComparison.Ordinal)) return false;
        foreach (var suffix in SidecarSuffixes)
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string? FileStem(string path) => SplitName(path).Stem;

    private static (string Stem, string? Extension) SplitName(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        if (dot <= 0) return (name, null);
        return (name[..dot], name[(dot + 1)..]);
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

    private static SyncException InvalidSource() =>
        SyncException.FromSource(new InvalidDataException("unsupported or incomplete Command Code session"));

    private sealed record CommandCodeSessionMeta(string Id, string? Project, long? StartMs);

    private sealed record RowFields(string? Id, string? Timestamp, string? Model, string? Role,
        long? CreatedAtMs, RawTokenCounts Usage);

    private sealed record Candidate(MessageRecord Message, RawTokenCounts Raw);

    private sealed class ParsedFile
    {
        public CommandCodeSessionMeta Meta { get; set; } = new(string.Empty, null, null);
        public List<Candidate> Requests { get; } = new();
        public int Unparsed { get; set; }
        public int Ignored { get; set; }
    }

    private sealed class Rollup
    {
        private readonly CommandCodeSessionMeta _meta;
        private readonly string _sourcePath;
        private readonly Dictionary<string, (Candidate Candidate, string Path)> _requests =
            new(StringComparer.Ordinal);

        public Rollup(CommandCodeSessionMeta meta, string sourcePath)
        {
            _meta = meta;
            _sourcePath = sourcePath;
        }

        public bool Partial { get; set; }

        public void Push(Candidate candidate, string path) =>
            _requests.TryAdd(candidate.Message.DedupKey, (candidate, path));

        public SessionImport Finish()
        {
            var entries = _requests.Values.ToList();
            entries.Sort((left, right) =>
            {
                var compare = (left.Candidate.Message.Seq ?? 0)
                    .CompareTo(right.Candidate.Message.Seq ?? 0);
                return compare != 0
                    ? compare
                    : string.CompareOrdinal(left.Candidate.Message.DedupKey,
                        right.Candidate.Message.DedupKey);
            });

            var messages = new List<MessageRecord>(entries.Count);
            var counts = new List<RawTokenCounts>(entries.Count);
            var messageSourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (candidate, path) in entries)
            {
                messages.Add(candidate.Message);
                counts.Add(candidate.Raw);
                messageSourcePaths[candidate.Message.DedupKey] = path;
            }

            var session = RequestBuilder.SessionWithSemantics(Client, TokenSemanticsCatalog.CommandCode,
                new SessionMeta(_meta.Id, null, _meta.Project, _meta.StartMs, null),
                messages, counts, Partial);
            return new SessionImport(session, messages, _sourcePath, messageSourcePaths);
        }
    }
}
