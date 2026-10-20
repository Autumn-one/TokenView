using TokenView.Wpf.Data;

namespace TokenView.Wpf;

public enum ChartMetric
{
    Tokens,
    Requests,
}

public sealed record ClientInfo(string Key, string Label, string ColorHex);

public sealed record ChartClientSlice(
    string ClientKey,
    string Label,
    string ColorHex,
    long Tokens,
    int Requests,
    int RequestsWithoutUsage)
{
    public string TokenText => TokenViewDatabase.FormatTokens(Tokens);
    public string RequestText => Requests.ToString("N0");
}

public sealed record ChartModelSlice(
    string Model,
    long Tokens,
    int Requests,
    int RequestsWithoutUsage)
{
    public string TokenText => TokenViewDatabase.FormatTokens(Tokens);
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
    IReadOnlyList<ChartModelSlice>? Models = null);

public sealed record ClientModelRow(string Name, int Requests, long Tokens, double Share)
{
    public string TokenText => TokenViewDatabase.FormatTokens(Tokens);
    public string RequestText => $"{Requests:N0} 条请求";
    public string ShareText => Share.ToString("P1");
}

public sealed record ClientUsageRow(
    ClientInfo Client,
    int Sessions,
    int Requests,
    long Tokens,
    double Share,
    IReadOnlyList<ClientModelRow> Models,
    bool IsExpanded = false)
{
    public string TokenText => TokenViewDatabase.FormatTokens(Tokens);
    public string SessionText => $"{Sessions:N0} 个会话 · {Requests:N0} 条请求";
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
    long Tokens,
    string Coverage)
{
    public string TokenText => TokenViewDatabase.FormatTokens(Tokens);
    public string RequestsText => Requests.ToString("N0");
}

public sealed record RankingRow(string Name, long Tokens, double Share)
{
    public string TokenText => TokenViewDatabase.FormatTokens(Tokens);
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
    long Tokens,
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
    IReadOnlyList<SessionMessageRow> Messages);

public sealed record LedgerClientRow(ClientInfo Client, int Sessions, long Tokens, double Share)
{
    public string TokenText => TokenViewDatabase.FormatTokens(Tokens);
    public string SessionText => $"{Sessions:N0} 个会话";
}

public sealed record LedgerSnapshot(
    string RangeLabel,
    string RangeText,
    int Sessions,
    long Tokens,
    long TodayTokens,
    int SessionsWithoutUsage,
    int Messages,
    int? CacheHitBps,
    IReadOnlyList<ChartPoint> Daily,
    IReadOnlyList<LedgerClientRow> Clients,
    IReadOnlyList<SessionRow> SessionRows);

public sealed record DailyStripRow(string Label, string TokenText);

public sealed record ClientStatsRow(
    string Client,
    long Sessions,
    long SessionsWithoutUsage,
    long Messages,
    long Files,
    long Unparsed,
    long? LastScanMs);

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
    public IReadOnlyList<ChartPoint> ChartPoints { get; init; } = [];
    public IReadOnlyList<DailyStripRow> DailyStrip { get; init; } = [];
    public IReadOnlyList<ClientUsageRow> Clients { get; init; } = [];
    public IReadOnlyList<RankingRow> Models { get; init; } = [];
    public IReadOnlyList<RankingRow> Projects { get; init; } = [];
    public IReadOnlyList<SessionRow> RecentSessions { get; init; } = [];
}
