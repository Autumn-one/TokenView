using TokenView.Wpf.Data;

namespace TokenView.Wpf;

public enum ChartMetric
{
    Tokens,
    Requests,
}

public sealed record ClientInfo(string Key, string Label, string ColorHex);

internal static class UsageDisplay
{
    public static bool IsUnknown(long tokens, int count, int countWithoutUsage) =>
        tokens == 0 && count > 0 && countWithoutUsage >= count;

    public static string Tokens(long tokens, int count, int countWithoutUsage) =>
        IsUnknown(tokens, count, countWithoutUsage)
            ? "—"
            : TokenViewDatabase.FormatTokens(tokens);
}

public sealed record ChartClientSlice(
    string ClientKey,
    string Label,
    string ColorHex,
    long Tokens,
    int Requests,
    int RequestsWithoutUsage)
{
    public string TokenText => UsageDisplay.Tokens(Tokens, Requests, RequestsWithoutUsage);
    public string RequestText => Requests.ToString("N0");
}

public sealed record ChartModelSlice(
    string Model,
    long Tokens,
    int Requests,
    int RequestsWithoutUsage)
{
    public string TokenText => UsageDisplay.Tokens(Tokens, Requests, RequestsWithoutUsage);
    public string RequestText => Requests.ToString("N0");
}

public sealed record ChartPoint(
    long StartMs,
    string Label,
    long Tokens,
    int Requests,
    int RequestsWithUsage,
    int RequestsWithoutUsage,
    string Title,
    int Sessions = 0,
    IReadOnlyList<ChartClientSlice>? Clients = null,
    IReadOnlyList<ChartModelSlice>? Models = null)
{
    public string TokenText => UsageDisplay.Tokens(Tokens, Requests, RequestsWithoutUsage);
}

public sealed record ClientModelRow(string Name, int Requests, long Tokens, double Share,
    int RequestsWithoutUsage = 0)
{
    public string TokenText => UsageDisplay.Tokens(Tokens, Requests, RequestsWithoutUsage);
    public string RequestText => $"{Requests:N0} 条请求";
    public string ShareText => UsageDisplay.IsUnknown(Tokens, Requests, RequestsWithoutUsage)
        ? "—"
        : Share.ToString("P1");
}

public sealed record ClientUsageRow(
    ClientInfo Client,
    int Sessions,
    int Requests,
    int RequestsWithoutUsage,
    long Tokens,
    double Share,
    IReadOnlyList<ClientModelRow> Models,
    long EstimatedTokens = 0,
    int EstimatedRequests = 0,
    bool IsExpanded = false)
{
    public string TokenText => UsageDisplay.Tokens(Tokens, Requests, RequestsWithoutUsage);
    public string SessionText => $"{Sessions:N0} 个会话 · {Requests:N0} 条请求";
    public string ShareText => UsageDisplay.IsUnknown(Tokens, Requests, RequestsWithoutUsage)
        ? "—"
        : Share.ToString("P1");
    public bool HasKnownTokens => !UsageDisplay.IsUnknown(Tokens, Requests, RequestsWithoutUsage);
    public bool HasEstimate => EstimatedTokens > 0;
    public string EstimateText => HasEstimate
        ? $"估算 ≈ {TokenViewDatabase.FormatTokens(EstimatedTokens)} · 未计入实测"
        : "";
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";
}

public sealed record SessionRow(
    string ClientKey,
    string ClientLabel,
    string ColorHex,
    string SessionId,
    string Project,
    string Model,
    string Started,
    int Requests,
    long? Tokens,
    string Coverage,
    bool IsEstimated = false)
{
    public string TokenText => Tokens is { } value
        ? (IsEstimated ? "≈ " : "") + TokenViewDatabase.FormatTokens(value)
        : "—";
    public string RequestsText => Requests.ToString("N0");
}

public sealed record RankingRow(string Name, long Tokens, double Share,
    int Sessions = 0, int SessionsWithoutUsage = 0)
{
    public string TokenText => UsageDisplay.Tokens(Tokens, Sessions, SessionsWithoutUsage);
}

public sealed record SessionMessageRow(
    string Seq,
    string Time,
    string Completed,
    string Model,
    string Agent,
    long? Input,
    long? Output,
    long? Reasoning,
    long? CacheRead,
    long? CacheWrite,
    long? Total,
    string SourceKind,
    string SourceRef,
    string Status)
{
    private static string Format(long? value) =>
        value is { } number ? TokenViewDatabase.FormatTokens(number) : "—";

    public string InputText => Format(Input);
    public string OutputText => Format(Output);
    public string ReasoningText => Format(Reasoning);
    public string CacheReadText => Format(CacheRead);
    public string CacheWriteText => Format(CacheWrite);
    public string TotalText => Format(Total);
}

public sealed record SessionDetailSnapshot(
    string ClientLabel,
    string ColorHex,
    string SessionId,
    string Project,
    string Model,
    string Started,
    string Ended,
    long? Tokens,
    long? Input,
    long? Output,
    long? Reasoning,
    long? CacheRead,
    long? CacheWrite,
    string TotalBasis,
    string Coverage,
    IReadOnlyList<string> UnknownRelations,
    int Requests,
    int Page,
    int TotalMessages,
    int Timed,
    int Untimed,
    int WithUsage,
    int WithoutUsage,
    int Estimated,
    IReadOnlyList<SessionMessageRow> Messages,
    bool IsEstimated = false)
{
    public string TokenText => Tokens is { } value
        ? (IsEstimated ? "≈ " : "") + TokenViewDatabase.FormatTokens(value)
        : "—";
}

public sealed record LedgerClientRow(ClientInfo Client, int Sessions, long Tokens, double Share,
    int SessionsWithoutUsage = 0, long EstimatedTokens = 0)
{
    public string TokenText => UsageDisplay.Tokens(Tokens, Sessions, SessionsWithoutUsage);
    public string SessionText => $"{Sessions:N0} 个会话";
    public bool HasEstimate => EstimatedTokens > 0;
    public string EstimateText => HasEstimate
        ? $"估算 ≈ {TokenViewDatabase.FormatTokens(EstimatedTokens)}"
        : "";
}

public sealed record LedgerSnapshot(
    string RangeLabel,
    string RangeText,
    int Sessions,
    long Tokens,
    long TodayTokens,
    int TodayRequests,
    int TodayRequestsWithoutUsage,
    int SessionsWithoutUsage,
    int Messages,
    int? CacheHitBps,
    IReadOnlyList<ChartPoint> Daily,
    IReadOnlyList<LedgerClientRow> Clients,
    IReadOnlyList<SessionRow> SessionRows,
    int MeasuredSessions = 0,
    int MeasuredSessionsWithoutUsage = 0,
    long EstimatedTokens = 0)
{
    public string TokenText => Tokens == 0 && EstimatedTokens > 0 && MeasuredSessions == 0
        ? "—"
        : UsageDisplay.Tokens(Tokens, MeasuredSessions, MeasuredSessionsWithoutUsage);
    public string TodayTokenText =>
        UsageDisplay.Tokens(TodayTokens, TodayRequests, TodayRequestsWithoutUsage);
    public bool HasEstimate => EstimatedTokens > 0;
    public string EstimateText => HasEstimate
        ? $"估算 ≈ {TokenViewDatabase.FormatTokens(EstimatedTokens)} · 未计入区间合计"
        : "";
}

public sealed record DailyStripRow(string Label, string TokenText);

public sealed record ClientStatsRow(
    string Client,
    long Sessions,
    long SessionsWithoutUsage,
    long Messages,
    long Files,
    long Unparsed,
    long? LastScanMs,
    long EstimatedSessions = 0);

public sealed class OverviewSnapshot
{
    public string RangeText { get; init; } = "";
    public string RangeDaysText { get; init; } = "";
    public long RangeFromMs { get; init; }
    public long RangeToMs { get; init; }
    public bool IsHourly { get; init; }
    public int TotalRequests { get; init; }
    public int RequestsWithUsage { get; init; }
    public int RequestsWithoutUsage { get; init; }
    public int Sessions { get; init; }
    public int SessionsPartial { get; init; }
    public long TotalTokens { get; init; }
    public long EstimatedTokens { get; init; }
    public int EstimatedRequests { get; init; }
    public string TokenText => UsageDisplay.Tokens(TotalTokens, TotalRequests, RequestsWithoutUsage);
    public bool HasEstimate => EstimatedTokens > 0;
    public IReadOnlyList<ChartPoint> ChartPoints { get; init; } = [];
    public IReadOnlyList<DailyStripRow> DailyStrip { get; init; } = [];
    public IReadOnlyList<ClientUsageRow> Clients { get; init; } = [];
    public IReadOnlyList<RankingRow> Models { get; init; } = [];
    public IReadOnlyList<RankingRow> Projects { get; init; } = [];
    public IReadOnlyList<SessionRow> RecentSessions { get; init; } = [];
}
