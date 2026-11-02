namespace TokenView.Wpf.Data.Peers;

public enum DeviceScope
{
    Local,
    All,
    Peer,
}

public sealed record DeviceSelection(DeviceScope Scope, string? DeviceId = null)
{
    public static readonly DeviceSelection Local = new(DeviceScope.Local);
    public static readonly DeviceSelection All = new(DeviceScope.All);

    public static DeviceSelection Peer(string deviceId) => new(DeviceScope.Peer, deviceId);
}

/// <summary>一台设备的聚合快照；只含统计元数据，不含会话标识、项目路径或正文。</summary>
public sealed class PeerSnapshotDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public long GeneratedAtMs { get; set; }
    public int UtcOffsetMinutes { get; set; }
    public string AppVersion { get; set; } = "";
    public long SessionsPartial { get; set; }

    /// <summary>按设备本地日聚合的请求/Token；timeMs 为本地零点。</summary>
    public List<PeerRequestRow> RequestDaily { get; set; } = [];

    /// <summary>最近 48 小时的请求/Token；timeMs 为原始 UTC 小时起点。</summary>
    public List<PeerRequestRow> RequestHourly { get; set; } = [];

    /// <summary>按设备本地日聚合的「开始的会话数」；timeMs 为本地零点。</summary>
    public List<PeerSessionRow> SessionDaily { get; set; } = [];

    /// <summary>最近 48 小时的「开始的会话数」；timeMs 为原始 UTC 小时起点。</summary>
    public List<PeerSessionRow> SessionHourly { get; set; } = [];
}

/// <summary>请求桶。tokens 不含估算；estimatedTokens 单独存放。</summary>
public sealed class PeerRequestRow
{
    public long TimeMs { get; set; }
    public string Client { get; set; } = "";
    public string Model { get; set; } = "";
    public long Tokens { get; set; }
    public int Requests { get; set; }
    public int RequestsWithUsage { get; set; }
    public int RequestsWithoutUsage { get; set; }
    public long EstimatedTokens { get; set; }
    public int EstimatedRequests { get; set; }
}

/// <summary>会话开始桶；只保留计数，不导出会话标识。</summary>
public sealed class PeerSessionRow
{
    public long TimeMs { get; set; }
    public string Client { get; set; } = "";
    public int Sessions { get; set; }
    public int EstimatedSessions { get; set; }
}

public sealed record PeerDeviceRow(
    string DeviceId,
    string Name,
    long GeneratedAtMs,
    long ImportedAtMs,
    int SchemaVersion,
    int UtcOffsetMinutes,
    string AppVersion,
    long SessionsPartial)
{
    public bool IsStale(DateTimeOffset now, TimeSpan threshold) =>
        now.ToUnixTimeMilliseconds() - GeneratedAtMs > threshold.TotalMilliseconds;
}

public sealed record PeerImportResult(
    bool Imported,
    bool StaleSkipped,
    string DeviceId,
    string DeviceName,
    int RequestDailyRows,
    int RequestHourlyRows,
    int SessionDailyRows,
    int SessionHourlyRows);

public sealed record PeerFileResult(
    string Path,
    bool Imported,
    bool StaleSkipped,
    string? Error);
