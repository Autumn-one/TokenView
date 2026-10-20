using System.IO;
using System.Text.Json;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>CodeArts agent kernel 会话：只读取 meta.json 与 messages.index.jsonl 的用量元数据。</summary>
public sealed class CodeArtsCollector : IClientCollector
{
    private const string Client = "codearts";
    private const int ParserVersion = 1;

    private readonly string? _root;

    public CodeArtsCollector() : this(DefaultRoot())
    {
    }

    public CodeArtsCollector(string? root)
    {
        _root = root;
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths => _root is null ? [] : [_root];

    public static string? DefaultRoot()
    {
        var direct = CollectorSupport.EnvPath("CODEARTS_SESSIONS_DIR");
        if (direct is not null) return direct;
        var home = CollectorSupport.EnvPath("CODEARTS_HOME");
        var baseRoot = home ?? CollectorSupport.Combine(CollectorSupport.HomeDir(), ".codearts");
        return CollectorSupport.Combine(baseRoot, "kernel", "sessions");
    }

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        if (_root is null || !Directory.Exists(_root))
        {
            report.RootMissing = true;
            return report;
        }

        var metaPaths = DiscoverMetaFiles(_root, report);
        var sources = new List<SourceFile>();
        var unchanged = !options.ForceRescan;
        foreach (var path in metaPaths)
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
            report.FilesSkippedUnchanged = metaPaths.Count;
            return report;
        }

        var imports = new List<SessionImport>();
        for (var index = 0; index < metaPaths.Count; index++)
        {
            var path = metaPaths[index];
            var parsed = ParseSession(path);
            report.FilesParsed++;
            report.UnparsedLines += parsed.UnparsedLines;
            sources[index] = sources[index] with { UnparsedRecords = parsed.UnparsedLines };
            if (string.IsNullOrEmpty(parsed.SessionId))
            {
                report.FilesWithoutIdentity++;
                continue;
            }
            imports.Add(parsed.Import!);
        }

        var current = DiscoverMetaFiles(_root, new SyncReport());
        if (!current.SequenceEqual(metaPaths, StringComparer.Ordinal))
        {
            throw SyncException.ChangedDuringScan();
        }
        for (var index = 0; index < metaPaths.Count; index++)
        {
            var (size, mtime) = CollectorSupport.Fingerprint(metaPaths[index]);
            if (sources[index].SizeBytes != size || sources[index].MtimeMs != mtime)
            {
                throw SyncException.ChangedDuringScan();
            }
        }

        if (report.UnparsedLines > 0)
        {
            imports = imports.Select(entry => entry with
            {
                Session = entry.Session with { Coverage = MessageCoverage.Partial },
            }).ToList();
        }

        var imported = store.ReplaceClientSnapshot(Client, sources, imports);
        report.SessionsImported = imports.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private static List<string> DiscoverMetaFiles(string root, SyncReport report)
    {
        try
        {
            var files = Directory.EnumerateFiles(root, "meta.json", SearchOption.AllDirectories)
                .ToList();
            files.Sort(StringComparer.Ordinal);
            report.FilesSeen = files.Count;
            return files;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.Directory(error);
        }
    }

    private static ParsedSession ParseSession(string metaPath)
    {
        JsonDocument meta;
        try
        {
            meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
        catch (JsonException error)
        {
            throw SyncException.FromSource(error);
        }

        using (meta)
        {
            var root = meta.RootElement;
            var directory = Path.GetDirectoryName(metaPath);
            var rawSessionId = root.String("session_id");
            var sessionId = string.IsNullOrEmpty(rawSessionId)
                ? (directory is null ? "" : Path.GetFileName(directory) ?? "")
                : rawSessionId;
            if (sessionId.Length == 0)
            {
                return new ParsedSession(null, 0, null);
            }
            var created = UsageTime.ParseIso8601UtcMs(root.String("created_at"));
            var updated = UsageTime.ParseIso8601UtcMs(root.String("updated_at"));
            var project = root.String("working_directory");
            var parent = ReadParent(root);
            var model = root.Element("current_model")?.String("model_id");

            var messages = new List<MessageRecord>();
            var counts = new List<RawTokenCounts>();
            var unparsed = 0;
            var messagesPath = directory is null ? null : Path.Combine(directory, "messages.index.jsonl");
            if (messagesPath is not null && File.Exists(messagesPath))
            {
                var line = 0;
                CollectorSupport.ReadJsonLines(messagesPath, (text, complete) =>
                {
                    line++;
                    if (string.IsNullOrWhiteSpace(text)) return;
                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(text);
                    }
                    catch (JsonException error)
                    {
                        if (!complete) throw SyncException.FromSource(error);
                        unparsed++;
                        return;
                    }
                    using (document)
                    {
                        var entry = document.RootElement;
                        var id = entry.String("id");
                        var usage = entry.Element("token_usage");
                        if (usage is null) return;
                        if (string.IsNullOrEmpty(id))
                        {
                            unparsed++;
                            return;
                        }
                        var raw = new RawTokenCounts(
                            usage.Value.Long("prompt_tokens"),
                            usage.Value.Long("completion_tokens"),
                            usage.Value.Long("cached_tokens"),
                            Reasoning: usage.Value.Long("reasoning_tokens"),
                            ClientTotal: usage.Value.Long("total_tokens"));
                        if (!CollectorSupport.ValidCounts(raw))
                        {
                            unparsed++;
                            return;
                        }
                        counts.Add(raw);
                        var messageModel = entry.Element("model_ref")?.String("model_id") ?? model;
                        messages.Add(RequestBuilder.MessageWithSemantics(Client,
                            TokenSemanticsCatalog.CodeArts, sessionId,
                            new RequestMeta(id, messageModel, entry.String("agent"),
                                UsageTime.ParseIso8601UtcMs(entry.String("timestamp")), null, false,
                                $"line={line}"),
                            raw));
                    }
                });
            }

            var rawSession = new RawTokenCounts(
                root.Long("input_tokens") ?? SumComplete(counts, value => value.Input),
                root.Long("output_tokens") ?? SumComplete(counts, value => value.Output),
                SumKnownCached(counts),
                Reasoning: root.Long("reasoning_tokens") ?? SumComplete(counts, value => value.Reasoning),
                ClientTotal: root.Long("used_tokens") ?? SumComplete(counts, value => value.ClientTotal));
            var usage = UsageNormalizer.Normalize(TokenSemanticsCatalog.CodeArts, rawSession);
            var session = new SessionRecord
            {
                Client = Client,
                ClientSessionId = sessionId,
                ParentClientSessionId = parent,
                ProjectPath = project,
                Model = messages.AsEnumerable().Reverse().FirstOrDefault(message => message.Model is not null)?.Model
                        ?? model,
                StartedAtMs = created,
                EndedAtMs = updated ?? messages.Max(message => message.CompletedAtMs ?? message.CreatedAtMs),
                SemanticsId = TokenSemanticsCatalog.CodeArts.Id,
                TotalBasis = SessionTotalBasis.ClientReported,
                Coverage = unparsed > 0
                    ? MessageCoverage.Partial
                    : messages.Count == 0
                        ? MessageCoverage.NoMessages
                        : MessageCoverage.Complete,
                Usage = usage,
            };
            return new ParsedSession(sessionId, unparsed,
                new SessionImport(session, messages, metaPath, new Dictionary<string, string>()));
        }
    }

    private static long? SumComplete(IReadOnlyList<RawTokenCounts> counts, Func<RawTokenCounts, long?> selector)
    {
        if (counts.Count == 0) return null;
        long total = 0;
        foreach (var count in counts)
        {
            if (selector(count) is not { } value || total > long.MaxValue - value) return null;
            total += value;
        }
        return total;
    }

    private static long? SumKnownCached(IReadOnlyList<RawTokenCounts> counts)
    {
        long total = 0;
        foreach (var count in counts)
        {
            if (count.CacheRead is null) return null;
            if (total > long.MaxValue - count.CacheRead.Value) return null;
            total += count.CacheRead.Value;
        }
        return total;
    }

    private static string? ReadParent(JsonElement root)
    {
        if (!root.TryGetProperty("parent", out var parent)) return null;
        return parent.ValueKind switch
        {
            JsonValueKind.String => parent.GetString(),
            JsonValueKind.Object => parent.String("session_id") ?? parent.String("id"),
            _ => null,
        };
    }

    private sealed record ParsedSession(string? SessionId, int UnparsedLines, SessionImport? Import);
}
