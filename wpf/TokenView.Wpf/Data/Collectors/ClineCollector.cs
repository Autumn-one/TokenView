using System.Globalization;
using System.IO;
using System.Text.Json;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>
/// Cline CLI 会话（<c>sessions/&lt;id&gt;/&lt;id&gt;.json</c>）与 VS Code 扩展任务
/// （<c>globalStorage/saoudrizwan.claude-dev</c>）。只读取身份、时间、模型与 token 用量字段。
/// </summary>
public sealed class ClineCollector : IClientCollector
{
    private const string Client = "cline";
    private const int ParserVersion = 1;
    private const string ExtensionId = "saoudrizwan.claude-dev";

    private static readonly string[] EditorVariants = ["Code", "Code - Insiders", "Cursor", "VSCodium"];
    private static readonly string[] TaskDocuments = ["ui_messages.json", "task_metadata.json"];

    private readonly string? _cliRoot;
    private readonly IReadOnlyList<string> _vscodeRoots;

    public ClineCollector() : this(DefaultCliRoot(), DefaultVscodeRoots())
    {
    }

    public ClineCollector(string? cliRoot, IEnumerable<string> vscodeRoots)
    {
        _cliRoot = cliRoot;
        _vscodeRoots = vscodeRoots.ToList();
    }

    public string ClientKey => Client;

    public IReadOnlyList<string> SourcePaths
    {
        get
        {
            var paths = new List<string>();
            if (_cliRoot is not null) paths.Add(_cliRoot);
            paths.AddRange(_vscodeRoots);
            return paths;
        }
    }

    /// <summary>CLI 根目录优先级：<c>CLINE_DATA_DIR</c> → <c>CLINE_DIR/data</c> → <c>~/.cline/data</c>。</summary>
    public static string? ResolveCliRoot(string? data, string? dir, string? home)
    {
        if (!string.IsNullOrWhiteSpace(data)) return data;
        if (!string.IsNullOrWhiteSpace(dir)) return Path.Combine(dir, "data");
        return home is null ? null : Path.Combine(home, ".cline", "data");
    }

    public static string? DefaultCliRoot() =>
        ResolveCliRoot(CollectorSupport.EnvPath("CLINE_DATA_DIR"),
            CollectorSupport.EnvPath("CLINE_DIR"), CollectorSupport.HomeDir());

    /// <summary>已登记的编辑器变体下实际存在的扩展根目录，按字典序去重。</summary>
    public static List<string> DefaultVscodeRoots()
    {
        var bases = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            if (CollectorSupport.EnvPath("APPDATA") is { } appData) bases.Add(appData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (CollectorSupport.HomeDir() is { } home)
            {
                bases.Add(Path.Combine(home, "Library", "Application Support"));
            }
        }
        else if (CollectorSupport.EnvPath("XDG_CONFIG_HOME") is { } config)
        {
            bases.Add(config);
        }
        else if (CollectorSupport.HomeDir() is { } home)
        {
            bases.Add(Path.Combine(home, ".config"));
        }

        var found = new List<string>();
        foreach (var root in bases.SelectMany(VscodeRootsUnder))
        {
            if (!Path.Exists(root) || found.Contains(root, StringComparer.Ordinal)) continue;
            found.Add(root);
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static IEnumerable<string> VscodeRootsUnder(string root) =>
        EditorVariants.Select(variant =>
            Path.Combine(root, variant, "User", "globalStorage", ExtensionId));

    public SyncReport Sync(UsageStore store, SyncOptions options)
    {
        var report = new SyncReport();
        var (cli, vscode) = ExistingRoots();
        if (cli is null && vscode.Count == 0)
        {
            report.RootMissing = true;
            return report;
        }

        var paths = Discover(cli, vscode, report);
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
        unchanged &= store.SourcePaths(Client).SequenceEqual(
            sources.Select(source => source.Path), StringComparer.Ordinal);
        if (unchanged)
        {
            report.FilesSkippedUnchanged = paths.Count;
            return report;
        }

        var scan = new Scan();
        if (cli is not null) scan.AddCli(cli, paths);
        foreach (var root in vscode) scan.AddVscode(root, paths);
        report.FilesParsed = paths.Count;

        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < paths.Count; index++) indexes[paths[index]] = index;
        foreach (var (path, unparsed) in scan.UnparsedPerFile)
        {
            report.UnparsedLines += unparsed;
            if (indexes.TryGetValue(path, out var index))
            {
                sources[index] = sources[index] with { UnparsedRecords = unparsed };
            }
        }

        var sessions = Merge(scan.Drafts, report);
        // 扫描期间新增、替换或追加文件时绝不发布快照。
        if (!Discover(cli, vscode, new SyncReport()).SequenceEqual(paths, StringComparer.Ordinal))
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

        var imported = store.ReplaceClientSnapshot(Client, sources, sessions);
        report.SessionsImported = sessions.Count;
        report.MessagesInserted = imported.MessagesInserted;
        report.MessagesPruned = imported.MessagesPruned;
        return report;
    }

    private (string? Cli, List<string> Vscode) ExistingRoots()
    {
        var cli = _cliRoot is not null && Path.Exists(_cliRoot) ? _cliRoot : null;
        var vscode = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in _vscodeRoots)
        {
            if (Path.Exists(root) && seen.Add(root)) vscode.Add(root);
        }
        vscode.Sort(StringComparer.Ordinal);
        return (cli, vscode);
    }

    private static List<string> Discover(string? cli, IReadOnlyList<string> vscode, SyncReport report)
    {
        var paths = new List<string>();
        if (cli is not null) paths.AddRange(DiscoverCliFiles(cli));
        foreach (var root in vscode) paths.AddRange(DiscoverVscodeFiles(root));
        paths.Sort(StringComparer.Ordinal);
        var unique = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (unique.Count == 0 || !string.Equals(unique[^1], path, StringComparison.Ordinal))
            {
                unique.Add(path);
            }
        }
        report.FilesSeen = unique.Count;
        return unique;
    }

    private static List<string> DiscoverCliFiles(string root)
    {
        var sessions = Path.Combine(root, "sessions");
        if (!Path.Exists(sessions)) return [];
        return CollectorSupport.DiscoverFiles(sessions, new SyncReport(), AcceptsCli);
    }

    private static bool AcceptsCli(string path)
    {
        var name = Path.GetFileName(path);
        var directory = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        if (name.Length == 0 || directory.Length == 0) return false;
        return string.Equals(name, directory + ".json", StringComparison.Ordinal) ||
               string.Equals(name, directory + ".messages.json", StringComparison.Ordinal);
    }

    private static List<string> DiscoverVscodeFiles(string root)
    {
        var files = new List<string>();
        var history = Path.Combine(root, "state", "taskHistory.json");
        if (IsRegularFile(history)) files.Add(history);

        var tasks = Path.Combine(root, "tasks");
        if (Path.Exists(tasks))
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(tasks).ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw SyncException.Directory(error);
            }
            foreach (var entry in entries)
            {
                if (!IsRealDirectory(entry)) continue;
                foreach (var name in TaskDocuments)
                {
                    var path = Path.Combine(entry, name);
                    if (IsRegularFile(path)) files.Add(path);
                }
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory) &&
                   !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
    }

    private static bool IsRealDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) &&
                   !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SyncException.FromSource(error);
        }
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

    // ------------------------------------------------------------------ scan

    private sealed class Draft
    {
        public Draft(SessionImport import, bool fromCli)
        {
            Import = import;
            FromCli = fromCli;
        }

        public SessionImport Import { get; }
        public bool FromCli { get; }
        public int Requests => Import.Messages.Count;
    }

    private sealed class Scan
    {
        public List<Draft> Drafts { get; } = new();
        public SortedDictionary<string, int> UnparsedPerFile { get; } = new(StringComparer.Ordinal);

        public void Charge(string path, int unparsed)
        {
            if (unparsed <= 0) return;
            UnparsedPerFile[path] = UnparsedPerFile.GetValueOrDefault(path) + unparsed;
        }

        /// <summary>CLI：<c>&lt;data&gt;/sessions/&lt;id&gt;/&lt;id&gt;.json</c> + <c>&lt;id&gt;.messages.json</c>。</summary>
        public void AddCli(string root, IReadOnlyList<string> paths)
        {
            var sessions = Path.Combine(root, "sessions");
            foreach (var (directory, group) in CliGroups(Scoped(paths, sessions)))
            {
                var fallbackId = Path.GetFileName(directory);
                if (fallbackId.Length == 0) continue;
                var primary = group.Messages ?? group.Session;
                if (primary is null) continue;

                var unparsed = 0;
                CliSession? session = null;
                if (group.Session is { } sessionPath)
                {
                    if (TryParseCliSession(sessionPath, fallbackId, out var parsed))
                    {
                        session = parsed;
                    }
                    else
                    {
                        unparsed++;
                        Charge(sessionPath, 1);
                    }
                }

                // 会话 id 以文档为准；请求必须挂在同一个 id 下，否则快照校验拒绝写入。
                var id = session?.Id ?? fallbackId;
                var model = session?.Model;
                var requests = new Requests();
                if (group.Messages is { } messagesPath)
                {
                    requests = ParseCliMessages(messagesPath, id, model);
                    unparsed += requests.Unparsed;
                    Charge(messagesPath, requests.Unparsed);
                }

                var meta = new ClineSessionMeta(id, session?.Project, model,
                    session?.StartMs, session?.EndMs ?? requests.UpdatedMs);
                var import = Assemble(meta, session?.Aggregate, requests, unparsed,
                    primary, group.Messages ?? group.Session);
                Drafts.Add(new Draft(import, true));
            }
        }

        /// <summary>VS Code：<c>state/taskHistory.json</c> + <c>tasks/&lt;id&gt;/{ui_messages,task_metadata}.json</c>。</summary>
        public void AddVscode(string root, IReadOnlyList<string> paths)
        {
            var scoped = Scoped(paths, root);
            var history = new SortedDictionary<string, TaskRow>(StringComparer.Ordinal);
            string? historyPath = null;
            foreach (var path in scoped.Where(path =>
                         string.Equals(Path.GetFileName(path), "taskHistory.json", StringComparison.Ordinal)))
            {
                historyPath = path;
                var (rows, unparsed) = ParseHistory(path);
                Charge(path, unparsed);
                foreach (var row in rows) history[row.Id] = row;
            }

            foreach (var (directory, group) in TaskGroups(scoped))
            {
                var id = Path.GetFileName(directory);
                if (id.Length == 0) continue;
                history.Remove(id, out var row);

                var unparsed = 0;
                var models = new List<ModelUsage>();
                if (group.Metadata is { } metadataPath)
                {
                    var (entries, broken) = ParseMetadata(metadataPath);
                    unparsed += broken;
                    Charge(metadataPath, broken);
                    models = entries;
                }
                var sessionModel = SessionModel(models);
                var requests = new Requests();
                if (group.Messages is { } messagesPath)
                {
                    requests = ParseTaskMessages(messagesPath, id, models, sessionModel);
                    unparsed += requests.Unparsed;
                    Charge(messagesPath, requests.Unparsed);
                }

                var meta = new ClineSessionMeta(id, row?.Project, sessionModel, row?.StartMs,
                    requests.UpdatedMs ?? row?.StartMs);
                var aggregate = row is { Reported: true } ? row.Usage : (RawTokenCounts?)null;
                // 没有 ui_messages.json 时，会话记录仍归属 taskHistory.json，再退回元数据文件。
                var primary = group.Messages ?? historyPath ?? group.Metadata;
                if (primary is null) continue;
                var import = Assemble(meta, aggregate, requests, unparsed, primary, group.Messages);
                Drafts.Add(new Draft(import, false));
            }

            // taskHistory 中的任务可能已被清理：仍保留为只有会话总量的记录。
            if (historyPath is null) return;
            foreach (var row in history.Values)
            {
                var meta = new ClineSessionMeta(row.Id, row.Project, null, row.StartMs, row.StartMs);
                var aggregate = row.Reported ? row.Usage : (RawTokenCounts?)null;
                var import = Assemble(meta, aggregate, new Requests(), 0, historyPath, null);
                Drafts.Add(new Draft(import, false));
            }
        }
    }

    private sealed class CliGroup
    {
        public string? Session { get; set; }
        public string? Messages { get; set; }
    }

    private sealed class TaskGroup
    {
        public string? Messages { get; set; }
        public string? Metadata { get; set; }
    }

    private static List<string> Scoped(IReadOnlyList<string> paths, string root) =>
        paths.Where(path => IsUnder(path, root)).ToList();

    private static bool IsUnder(string path, string root)
    {
        var pathParts = SplitPath(path);
        var rootParts = SplitPath(root);
        if (pathParts.Length < rootParts.Length) return false;
        for (var index = 0; index < rootParts.Length; index++)
        {
            if (!string.Equals(pathParts[index], rootParts[index], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string[] SplitPath(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    private static SortedDictionary<string, CliGroup> CliGroups(IReadOnlyList<string> paths)
    {
        var groups = new SortedDictionary<string, CliGroup>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!AcceptsCli(path)) continue;
            var directory = Path.GetDirectoryName(path);
            if (directory is null) continue;
            if (!groups.TryGetValue(directory, out var group))
            {
                group = new CliGroup();
                groups[directory] = group;
            }
            var name = Path.GetFileName(path);
            var directoryName = Path.GetFileName(directory);
            if (string.Equals(name, directoryName + ".messages.json", StringComparison.Ordinal))
            {
                group.Messages = path;
            }
            else if (string.Equals(name, directoryName + ".json", StringComparison.Ordinal))
            {
                group.Session = path;
            }
        }
        return groups;
    }

    private static SortedDictionary<string, TaskGroup> TaskGroups(IReadOnlyList<string> paths)
    {
        var groups = new SortedDictionary<string, TaskGroup>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!string.Equals(name, "ui_messages.json", StringComparison.Ordinal) &&
                !string.Equals(name, "task_metadata.json", StringComparison.Ordinal))
            {
                continue;
            }
            var directory = Path.GetDirectoryName(path);
            if (directory is null) continue;
            if (!groups.TryGetValue(directory, out var group))
            {
                group = new TaskGroup();
                groups[directory] = group;
            }
            if (string.Equals(name, "ui_messages.json", StringComparison.Ordinal))
            {
                group.Messages = path;
            }
            else
            {
                group.Metadata = path;
            }
        }
        return groups;
    }

    /// <summary>CLI 与 VS Code 会话 id 冲突时保留请求明细更多的一方，数量相同优先 CLI。</summary>
    private static List<SessionImport> Merge(List<Draft> drafts, SyncReport report)
    {
        var best = new SortedDictionary<string, Draft>(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            var id = draft.Import.Session.ClientSessionId;
            if (!best.TryGetValue(id, out var existing))
            {
                best[id] = draft;
                continue;
            }
            // 重复 id 只记计数，不代表解析失败，也不因此把会话判成不完整。
            report.IgnoredLines++;
            var replace = draft.Requests > existing.Requests ||
                          (draft.Requests == existing.Requests && draft.FromCli && !existing.FromCli);
            if (replace) best[id] = draft;
        }
        return best.Values.Select(draft => draft.Import).ToList();
    }

    // --------------------------------------------------------------- parsing

    private sealed record ClineSessionMeta(string Id, string? Project, string? Model,
        long? StartMs, long? EndMs);

    private sealed class CliSession
    {
        public required string Id { get; init; }
        public string? Project { get; init; }
        public string? Model { get; init; }
        public long? StartMs { get; init; }
        public long? EndMs { get; init; }
        public RawTokenCounts? Aggregate { get; init; }
    }

    private sealed record TaskRow(string Id, long? StartMs, string? Project,
        RawTokenCounts Usage, bool Reported);

    private sealed record ModelUsage(long? TimeMs, string? Model);

    private sealed class Requests
    {
        public List<MessageRecord> Messages { get; } = new();
        public List<RawTokenCounts> Counts { get; } = new();
        public int Unparsed { get; set; }
        public long? UpdatedMs { get; set; }

        public static Requests Broken() => new() { Unparsed = 1 };
    }

    private readonly record struct UsageCounts(ulong? Input, ulong? Output,
        ulong? CacheRead, ulong? CacheWrite)
    {
        public bool Any =>
            Input is not null || Output is not null || CacheRead is not null || CacheWrite is not null;

        /// <summary>非负整数的和不得超过 i64。</summary>
        public bool Valid
        {
            get
            {
                ulong total = 0;
                foreach (var value in new[] { Input, Output, CacheRead, CacheWrite })
                {
                    if (value is not { } number) continue;
                    if (total > ulong.MaxValue - number) return false;
                    total += number;
                }
                return total <= long.MaxValue;
            }
        }

        public RawTokenCounts Raw => new(
            Input is { } input ? (long)input : null,
            Output is { } output ? (long)output : null,
            CacheRead is { } cacheRead ? (long)cacheRead : null,
            CacheWrite is { } cacheWrite ? (long)cacheWrite : null);

        public RawTokenCounts RawUnchecked => new(
            Input is { } input ? unchecked((long)input) : null,
            Output is { } output ? unchecked((long)output) : null,
            CacheRead is { } cacheRead ? unchecked((long)cacheRead) : null,
            CacheWrite is { } cacheWrite ? unchecked((long)cacheWrite) : null);
    }

    private static bool TryParseCliSession(string path, string directoryId, out CliSession session)
    {
        session = null!;
        using var document = ReadDocument(path);
        if (document is null) return false;
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var sessionId = OptionalString(root, "session_id");
            var started = ReadStamp(root, "started_at");
            var ended = ReadStamp(root, "ended_at");
            var model = OptionalString(root, "model");
            var cwd = OptionalString(root, "cwd");
            var workspace = OptionalString(root, "workspace_root");

            RawTokenCounts? aggregate = null;
            if (OptionalObject(root, "metadata") is { } metadata)
            {
                // aggregateUsage 优先；缺失时才退回 usage，两者都不叠加。
                var aggregateUsage = OptionalObject(metadata, "aggregateUsage");
                var usage = OptionalObject(metadata, "usage");
                if ((aggregateUsage ?? usage) is { } selected)
                {
                    aggregate = ReadCliUsage(selected).RawUnchecked;
                }
            }

            session = new CliSession
            {
                Id = string.IsNullOrEmpty(sessionId) ? directoryId : sessionId,
                Project = cwd ?? workspace,
                Model = string.IsNullOrEmpty(model) ? null : model,
                StartMs = started,
                EndMs = ended,
                Aggregate = aggregate,
            };
            return true;
        }
        catch (InvalidDocumentException)
        {
            return false;
        }
    }

    private static Requests ParseCliMessages(string path, string sessionId, string? fallbackModel)
    {
        using var document = ReadDocument(path);
        if (document is null) return Requests.Broken();
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Requests.Broken();
            var parsed = new Requests { UpdatedMs = ReadStamp(root, "updated_at") };
            if (!root.TryGetProperty("messages", out var messages))
            {
                return parsed;
            }
            if (messages.ValueKind != JsonValueKind.Array) return Requests.Broken();

            var ordinal = 0;
            foreach (var item in messages.EnumerateArray())
            {
                var index = ordinal++;
                if (item.ValueKind != JsonValueKind.Object) return Requests.Broken();
                var id = OptionalString(item, "id");
                var time = ReadStamp(item, "ts");
                var model = OptionalObject(item, "modelInfo") is { } info
                    ? OptionalString(info, "id")
                    : null;
                var metrics = OptionalObject(item, "metrics");
                // 只有带 metrics 的消息才是一次 API 请求；文本消息按设计跳过。
                if (metrics is not { } usageObject) continue;
                var usage = ReadCliUsage(usageObject);
                if (!usage.Valid)
                {
                    parsed.Unparsed++;
                    continue;
                }
                var key = string.IsNullOrEmpty(id) ? $"index:{index}" : $"message:{id}";
                var requestModel = string.IsNullOrEmpty(model) ? fallbackModel : model;
                var raw = usage.Raw;
                parsed.Messages.Add(ClineMessage(sessionId, key, requestModel, time, index,
                    $"message_index={index}", raw));
                parsed.Counts.Add(raw);
            }
            return parsed;
        }
        catch (InvalidDocumentException)
        {
            return Requests.Broken();
        }
    }

    private static (List<TaskRow> Rows, int Unparsed) ParseHistory(string path)
    {
        using var document = ReadDocument(path);
        if (document is null) return ([], 1);
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return ([], 1);
            var rows = new List<TaskRow>();
            var unparsed = 0;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return ([], 1);
                var id = OptionalString(item, "id");
                var time = ReadStamp(item, "ts");
                var project = OptionalString(item, "cwdOnTaskInitialization");
                var usage = ReadVscodeUsage(item);
                if (string.IsNullOrEmpty(id))
                {
                    unparsed++;
                    continue;
                }
                if (!usage.Valid)
                {
                    unparsed++;
                    continue;
                }
                rows.Add(new TaskRow(id, time, project, usage.Raw, usage.Any));
            }
            return (rows, unparsed);
        }
        catch (InvalidDocumentException)
        {
            return ([], 1);
        }
    }

    private static (List<ModelUsage> Entries, int Unparsed) ParseMetadata(string path)
    {
        using var document = ReadDocument(path);
        if (document is null) return ([], 1);
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ([], 1);
            if (!root.TryGetProperty("model_usage", out var modelUsage) ||
                modelUsage.ValueKind == JsonValueKind.Null)
            {
                return ([], 0);
            }
            if (modelUsage.ValueKind != JsonValueKind.Array) return ([], 1);

            var entries = new List<ModelUsage>();
            var unparsed = 0;
            foreach (var item in modelUsage.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return ([], 1);
                var time = ReadStamp(item, "ts");
                var model = OptionalString(item, "model_id");
                var usable = !string.IsNullOrEmpty(model);
                if (!usable) unparsed++;
                else entries.Add(new ModelUsage(time, model));
            }
            return (entries, unparsed);
        }
        catch (InvalidDocumentException)
        {
            return ([], 1);
        }
    }

    private static string? SessionModel(IReadOnlyList<ModelUsage> entries)
    {
        ModelUsage? selected = null;
        foreach (var entry in entries)
        {
            if (entry.Model is null) continue;
            if (selected is null ||
                (entry.TimeMs ?? long.MinValue) >= (selected.TimeMs ?? long.MinValue))
            {
                selected = entry;
            }
        }
        return selected?.Model;
    }

    private static string? ModelAt(IReadOnlyList<ModelUsage> entries, long? at, string? fallback)
    {
        if (at is { } time)
        {
            ModelUsage? selected = null;
            foreach (var entry in entries)
            {
                if (entry.Model is null) continue;
                if (entry.TimeMs is { } entryTime && entryTime > time) continue;
                if (selected is null ||
                    (entry.TimeMs ?? long.MinValue) >= (selected.TimeMs ?? long.MinValue))
                {
                    selected = entry;
                }
            }
            if (selected is not null) return selected.Model;
        }
        return fallback;
    }

    private static Requests ParseTaskMessages(string path, string taskId,
        IReadOnlyList<ModelUsage> models, string? sessionModel)
    {
        using var document = ReadDocument(path);
        if (document is null) return Requests.Broken();
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return Requests.Broken();
            var parsed = new Requests();
            var ordinal = 0;
            foreach (var row in root.EnumerateArray())
            {
                var index = ordinal++;
                if (row.ValueKind != JsonValueKind.Object) return Requests.Broken();
                var time = ReadStamp(row, "ts");
                var say = OptionalString(row, "say");
                var text = OptionalString(row, "text");
                if (!string.Equals(say, "api_req_started", StringComparison.Ordinal)) continue;

                var key = time is { } stamp
                    ? $"api_req_started:{stamp}:{index}"
                    : $"api_req_started:-:{index}";
                if (string.IsNullOrWhiteSpace(text))
                {
                    parsed.Unparsed++;
                    continue;
                }
                if (!TryParseVscodeUsage(text, out var usage) || !usage.Valid)
                {
                    parsed.Unparsed++;
                    continue;
                }
                var raw = usage.Raw;
                parsed.Messages.Add(ClineMessage(taskId, key, ModelAt(models, time, sessionModel),
                    time, index, $"api_req_started_index={index}", raw));
                parsed.Counts.Add(raw);
            }
            return parsed;
        }
        catch (InvalidDocumentException)
        {
            return Requests.Broken();
        }
    }

    private static bool TryParseVscodeUsage(string text, out UsageCounts usage)
    {
        usage = default;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            try
            {
                usage = ReadVscodeUsage(document.RootElement);
                return true;
            }
            catch (InvalidDocumentException)
            {
                return false;
            }
        }
    }

    private static MessageRecord ClineMessage(string sessionId, string key, string? model,
        long? timeMs, int ordinal, string sourceRef, RawTokenCounts raw)
    {
        var semantics = TokenSemanticsCatalog.Cline;
        // inputTokens/tokensIn 已是未命中缓存的输入；Cline 不从 output 中拆出 reasoning。
        var buckets = new RawTokenCounts(raw.Input, raw.Output, raw.CacheRead, raw.CacheWrite);
        return new MessageRecord
        {
            Client = Client,
            ClientSessionId = sessionId,
            DedupKey = key,
            Seq = ordinal,
            CreatedAtMs = timeMs,
            CompletedAtMs = timeMs,
            Model = model,
            Agent = null,
            Usage = UsageNormalizer.Normalize(semantics, buckets),
            IsSidechain = false,
            SemanticsId = semantics.Id,
            SourceKind = SourceKind.Measured,
            SourceRef = sourceRef,
            CostSource = CostSource.None,
            ClientCostUsd = null,
        };
    }

    /// <summary>聚合值优先；缺失时退回请求求和，两者绝不相加。</summary>
    private static SessionRecord BuildSession(ClineSessionMeta meta, RawTokenCounts? aggregate,
        IReadOnlyList<MessageRecord> messages, IReadOnlyList<RawTokenCounts> counts, bool partial)
    {
        var semantics = TokenSemanticsCatalog.Cline;
        var summed = UsageNormalizer.Normalize(semantics,
            RequestBuilder.SumCompleteCounts(counts));
        NormalizedUsage? reported = null;
        if (aggregate is { } raw)
        {
            reported = UsageNormalizer.Normalize(semantics,
                new RawTokenCounts(raw.Input, raw.Output, raw.CacheRead, raw.CacheWrite));
        }
        var usage = reported ?? summed;
        var basis = reported is null
            ? SessionTotalBasis.SumOfMessages
            : SessionTotalBasis.ClientReported;
        var coverage = messages.Count == 0
            ? MessageCoverage.NoMessages
            : partial || (reported is not null && usage.TotalTokens != summed.TotalTokens)
                ? MessageCoverage.Partial
                : MessageCoverage.Complete;

        var model = meta.Model;
        if (model is null)
        {
            MessageRecord? latest = null;
            foreach (var message in messages)
            {
                if (message.Model is null) continue;
                if (latest is null ||
                    (message.CreatedAtMs ?? long.MinValue) >= (latest.CreatedAtMs ?? long.MinValue))
                {
                    latest = message;
                }
            }
            model = latest?.Model;
        }

        var ended = meta.EndMs;
        foreach (var message in messages)
        {
            var value = message.CompletedAtMs ?? message.CreatedAtMs;
            if (value is { } candidate && (ended is null || candidate > ended)) ended = candidate;
        }

        return new SessionRecord
        {
            Client = Client,
            ClientSessionId = meta.Id,
            ParentClientSessionId = null,
            ProjectPath = meta.Project,
            Model = model,
            StartedAtMs = meta.StartMs,
            EndedAtMs = ended,
            SemanticsId = semantics.Id,
            TotalBasis = basis,
            Coverage = coverage,
            Usage = usage,
            SourceKind = SourceKind.Measured,
            CostSource = CostSource.None,
            ClientCostUsd = null,
        };
    }

    private static SessionImport Assemble(ClineSessionMeta meta, RawTokenCounts? aggregate,
        Requests requests, int unparsed, string sourcePath, string? messageSource)
    {
        var messagePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (messageSource is not null)
        {
            foreach (var message in requests.Messages) messagePaths[message.DedupKey] = messageSource;
        }
        var session = BuildSession(meta, aggregate, requests.Messages, requests.Counts, unparsed > 0);
        return new SessionImport(session, requests.Messages, sourcePath, messagePaths);
    }

    // ---------------------------------------------------------------- JSON

    private static JsonDocument? ReadDocument(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static UsageCounts ReadCliUsage(JsonElement element) => new(
        OptionalU64(element, "inputTokens"),
        OptionalU64(element, "outputTokens"),
        OptionalU64(element, "cacheReadTokens"),
        OptionalU64(element, "cacheWriteTokens"));

    private static UsageCounts ReadVscodeUsage(JsonElement element) => new(
        OptionalU64(element, "tokensIn"),
        OptionalU64(element, "tokensOut"),
        OptionalU64(element, "cacheReads"),
        OptionalU64(element, "cacheWrites"));

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDocumentException();
        return value.GetString();
    }

    private static JsonElement? OptionalObject(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDocumentException();
        return value;
    }

    private static ulong? OptionalU64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number))
        {
            throw new InvalidDocumentException();
        }
        return number;
    }

    /// <summary>epoch 毫秒或 ISO 8601 UTC（<c>Z</c>）；负值视为缺失。</summary>
    private static long? ReadStamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        long millis;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt64(out millis)) throw new InvalidDocumentException();
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var parsed = ParseIso8601UtcMs(value.GetString());
            return parsed is { } millisValue && millisValue >= 0 ? millisValue : null;
        }
        else
        {
            throw new InvalidDocumentException();
        }
        return millis >= 0 ? millis : null;
    }

    /// <summary>解析 ISO 8601 UTC 时间至毫秒：仅接受 UTC <c>Z</c> 形式，毫秒最多三位。</summary>
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

    private sealed class InvalidDocumentException : Exception
    {
    }
}
