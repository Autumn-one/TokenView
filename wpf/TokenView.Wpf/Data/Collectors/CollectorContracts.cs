using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

public sealed record SyncOptions(bool ForceRescan = false);

public enum ScanReason
{
    New,
    Appended,
    TruncatedOrReplaced,
    ParserVersionChanged,
    Forced,
    Unchanged,
}

public sealed class SyncReport
{
    public bool RootMissing { get; set; }
    public int FilesSeen { get; set; }
    public int FilesParsed { get; set; }
    public int FilesSkippedUnchanged { get; set; }
    public int FilesWithoutIdentity { get; set; }
    public int FilesFailed { get; set; }
    public int DirectoriesFailed { get; set; }
    public int UnparsedLines { get; set; }
    public int IgnoredLines { get; set; }
    public int SessionsImported { get; set; }
    public int SessionsSkippedStale { get; set; }
    public int MessagesInserted { get; set; }
    public int MessagesUpdated { get; set; }
    public int MessagesSkippedStale { get; set; }
    public int MessagesPruned { get; set; }
    public Dictionary<ScanReason, int> Reasons { get; } = new();
}

/// <summary>采集器错误；<see cref="Retryable"/> 为真时不把整次同步标红。</summary>
public sealed class SyncException : Exception
{
    public SyncException(string message, bool retryable = false, Exception? inner = null)
        : base(message, inner)
    {
        Retryable = retryable;
    }

    public bool Retryable { get; }

    public static SyncException Directory(Exception inner) =>
        new("无法读取客户端数据目录", false, inner);

    public static SyncException FromSource(Exception inner) =>
        new("无法完整读取客户端转录，下次同步重试", false, inner);
    public static SyncException Database(Exception inner) =>
        new("无法读取客户端数据库，可能被占用或格式不受支持", false, inner);

    public static SyncException Store(Exception inner) =>
        new("无法写入 TokenView 用量库", false, inner);

    public static SyncException ChangedDuringScan() =>
        new("源数据在扫描期间发生变化，下次同步重试", true);
}

public interface IClientCollector
{
    string ClientKey { get; }

    /// <summary>已配置的扫描位置（含不存在的），供设置页诊断展示。</summary>
    IReadOnlyList<string> SourcePaths { get; }

    SyncReport Sync(UsageStore store, SyncOptions options);
}

public sealed record ClientSyncResult(string Client, SyncReport? Report, string? Error, bool Retryable);

public sealed class SyncBatch
{
    public List<ClientSyncResult> Clients { get; } = new();

    public bool HasBlockingErrors() => Clients.Any(client =>
        (client.Error is not null && !client.Retryable) ||
        (client.Report is not null && (client.Report.FilesFailed > 0 || client.Report.DirectoriesFailed > 0)));

    public bool HasRetryableErrors() => Clients.Any(client => client.Retryable);
}

public sealed record SyncProgress(int CompletedClients, int TotalClients, string? CurrentClient);
