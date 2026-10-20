namespace TokenView.Wpf.Data.Usage;

public enum SourceKind
{
    Measured,
    Derived,
    Estimated,
}

public enum SessionTotalBasis
{
    ClientCumulative,
    ClientReported,
    SumOfMessages,
}

public enum MessageCoverage
{
    Complete,
    Partial,
    NoMessages,
}

public enum CostSource
{
    ClientReported,
    None,
}

public enum SourceFileKind
{
    Jsonl,
    Sqlite,
}

public enum UpsertOutcome
{
    Inserted,
    Updated,
    SkippedStale,
}

public sealed record SourceFile(
    string Client,
    string Path,
    SourceFileKind Kind,
    long? SizeBytes,
    long? MtimeMs,
    long ContentOffset,
    long ParserVersion,
    long? LastScanMs,
    long UnparsedRecords);

public sealed record SourceFileRow(
    long Id,
    string Client,
    string Path,
    string Kind,
    long? SizeBytes,
    long? MtimeMs,
    long ContentOffset,
    long ParserVersion,
    long? LastScanMs,
    long UnparsedRecords);

public sealed record SessionRecord
{
    public required string Client { get; init; }
    public required string ClientSessionId { get; init; }
    public string? ParentClientSessionId { get; init; }
    public string? ProjectPath { get; init; }
    public string? Model { get; init; }
    public long? StartedAtMs { get; init; }
    public long? EndedAtMs { get; init; }
    public required string SemanticsId { get; init; }
    public SessionTotalBasis TotalBasis { get; init; }
    public MessageCoverage Coverage { get; init; }
    public required NormalizedUsage Usage { get; init; }
    public SourceKind SourceKind { get; init; } = SourceKind.Measured;
    public CostSource CostSource { get; init; } = CostSource.None;
    public double? ClientCostUsd { get; init; }
}

public sealed record MessageRecord
{
    public required string Client { get; init; }
    public required string ClientSessionId { get; init; }
    public required string DedupKey { get; init; }
    public long? Seq { get; init; }
    public long? CreatedAtMs { get; init; }
    public long? CompletedAtMs { get; init; }
    public string? Model { get; init; }
    public string? Agent { get; init; }
    public required NormalizedUsage Usage { get; init; }
    public bool IsSidechain { get; init; }
    public required string SemanticsId { get; init; }
    public SourceKind SourceKind { get; init; } = SourceKind.Measured;
    public string? SourceRef { get; init; }
    public CostSource CostSource { get; init; } = CostSource.None;
    public double? ClientCostUsd { get; init; }
}

public sealed record ImportSummary
{
    public UpsertOutcome? Session { get; init; }
    public int MessagesInserted { get; set; }
    public int MessagesUpdated { get; set; }
    public int MessagesSkippedStale { get; set; }
    public int MessagesPruned { get; set; }
}

public sealed record SessionImport(
    SessionRecord Session,
    List<MessageRecord> Messages,
    string SourcePath,
    Dictionary<string, string> MessageSourcePaths)
{
    public static SessionImport Create(SessionRecord session, List<MessageRecord> messages, string sourcePath) =>
        new(session, messages, sourcePath, new Dictionary<string, string>());
}

public static class UsageIds
{
    public static string SessionId(string client, string clientSessionId) => $"{client}:{clientSessionId}";

    public static string MessageId(string client, string clientSessionId, string dedupKey) =>
        $"{client}:{clientSessionId}:{dedupKey}";

    public static string? EncodeUnknownRelations(IReadOnlyList<string> relations) =>
        relations.Count == 0 ? null : string.Join(',', relations);

    public static string SourceKindValue(SourceKind kind) => kind switch
    {
        SourceKind.Measured => "measured",
        SourceKind.Derived => "derived",
        _ => "estimated",
    };

    public static string TotalBasisValue(SessionTotalBasis basis) => basis switch
    {
        SessionTotalBasis.ClientCumulative => "client_cumulative",
        SessionTotalBasis.ClientReported => "client_reported",
        _ => "sum_of_messages",
    };

    public static string CoverageValue(MessageCoverage coverage) => coverage switch
    {
        MessageCoverage.Complete => "complete",
        MessageCoverage.Partial => "partial",
        _ => "none",
    };

    public static string SourceFileKindValue(SourceFileKind kind) => kind == SourceFileKind.Jsonl ? "jsonl" : "sqlite";

    public static string CostSourceValue(CostSource source) =>
        source == CostSource.ClientReported ? "client_reported" : "none";
}

public static class UsageTime
{
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static long? ParseIso8601UtcMs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }
}
