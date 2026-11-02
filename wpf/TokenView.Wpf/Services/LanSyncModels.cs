using System.Net;

namespace TokenView.Wpf.Services;

public sealed record LanPeerPresence(
    string DeviceId,
    string DeviceName,
    IPAddress Address,
    int TcpPort,
    string AppVersion,
    long LastSeenMs)
{
    public string EndpointText => $"{Address}:{TcpPort}";

    public bool IsOnline(DateTimeOffset now, TimeSpan timeout) =>
        now.ToUnixTimeMilliseconds() - LastSeenMs <= timeout.TotalMilliseconds;
}

public sealed record LanPeerStatus(
    string DeviceId,
    string DeviceName,
    bool Online,
    string Endpoint,
    long? LastSeenMs,
    long? LastSnapshotAtMs,
    long? ImportedAtMs,
    string? Error)
{
    public string OnlineText => Online ? "在线" : "离线";

    public string StatusText => Online ? "在线" : "离线";

    public string EndpointText => string.IsNullOrEmpty(Endpoint) ? "—" : Endpoint;

    public string LastSeenText => Format(LastSeenMs);

    public string SnapshotText => Format(LastSnapshotAtMs);

    public string ImportedText => Format(ImportedAtMs);

    public string DetailText => Online
        ? $"在线 · {EndpointText}"
        : $"离线 · 数据截至 {SnapshotText}";

    public bool HasError => !string.IsNullOrEmpty(Error);

    public string ErrorText => Error ?? "";

    public string WaitingText => LastSnapshotAtMs is null ? "等待首次同步…" : "";

    private static string Format(long? value) => value is { } ms
        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : "—";
}

public sealed record LanSyncStatus(
    bool Enabled,
    bool Running,
    int UdpPort,
    int TcpPort,
    string? Error,
    long? LastImportAtMs,
    IReadOnlyList<LanPeerStatus> Peers)
{
    public int OnlineCount => Peers.Count(peer => peer.Online);

    public int FoundCount => Peers.Count;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public string ErrorText => Error ?? "";

    public string ListenerText => Running ? $"UDP {UdpPort} · TCP {TcpPort}" : "未运行";

    public string SummaryText => !Enabled
        ? "局域网同步已关闭"
        : !Running
            ? "局域网自动发现未运行"
            : FoundCount == 0
                ? "正在查找局域网设备…"
                : $"已发现 {FoundCount} 台设备，{OnlineCount} 台在线";

    public string LastImportText => LastImportAtMs is { } ms
        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
        : "尚未导入其他设备数据";
}

public sealed record LanSyncOptions(
    string DatabasePath,
    string DeviceId,
    string DeviceName,
    string AppVersion,
    bool Enabled = true,
    int UdpPort = 17831,
    int TcpPort = 17832,
    IPAddress? ListenAddress = null,
    IReadOnlyList<IPEndPoint>? DiscoveryTargets = null,
    TimeProvider? TimeProvider = null,
    TimeSpan? BroadcastInterval = null);
