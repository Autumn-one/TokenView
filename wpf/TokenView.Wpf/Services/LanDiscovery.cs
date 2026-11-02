using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace TokenView.Wpf.Services;

/// <summary>UDP 广播本机身份；收到同协议广播后把对端交给 LanSyncService 拉取快照。</summary>
public sealed class LanDiscovery : IDisposable
{
    private const string Magic = "TokenView.Lan/1";
    private const int Protocol = 1;
    private const int MaxDatagramBytes = 2048;
    private const int MaxAnnouncementBytes = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly UdpClient _udp;
    private readonly IPEndPoint[] _targets;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly string _appVersion;
    private readonly int _tcpPort;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _broadcastInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveTask;
    private Task? _broadcastTask;
    private long _lastBroadcastAtMs;
    private string? _error;

    public LanDiscovery(
        string deviceId,
        string deviceName,
        string appVersion,
        int tcpPort,
        IPAddress listenAddress,
        int udpPort,
        IReadOnlyList<IPEndPoint>? targets,
        TimeProvider? clock = null,
        TimeSpan? broadcastInterval = null)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _appVersion = appVersion;
        _tcpPort = tcpPort;
        _clock = clock ?? TimeProvider.System;
        _broadcastInterval = broadcastInterval ?? TimeSpan.FromSeconds(5);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(listenAddress, udpPort));
        socket.EnableBroadcast = true;
        _udp = new UdpClient { Client = socket };
        UdpPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _targets = (targets ?? BroadcastTargets(UdpPort)).ToArray();
    }

    public event Action<LanPeerPresence>? PeerSeen;

    public int UdpPort { get; }

    public long? LastBroadcastAtMs => _lastBroadcastAtMs == 0 ? null : _lastBroadcastAtMs;

    public string? Error => _error;

    public void Start()
    {
        _receiveTask = Task.Run(ReceiveLoopAsync);
        _broadcastTask = Task.Run(BroadcastLoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        try
        {
            Task.WaitAll(new[] { _receiveTask, _broadcastTask }
                .Where(task => task is not null).Cast<Task>().ToArray(), TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 关闭套接字会让等待中的接收任务以 SocketException 结束，不影响退出。
        }
        _cts.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync().WaitAsync(_cts.Token);
                if (result.Buffer.Length is 0 or > MaxDatagramBytes ||
                    !LanAddress.IsPrivateOrLoopback(result.RemoteEndPoint.Address))
                {
                    continue;
                }
                var announcement = JsonSerializer.Deserialize<Announcement>(result.Buffer, JsonOptions);
                if (announcement is null ||
                    !string.Equals(announcement.Magic, Magic, StringComparison.Ordinal) ||
                    announcement.Protocol != Protocol ||
                    !Guid.TryParse(announcement.DeviceId, out _) ||
                    string.Equals(announcement.DeviceId, _deviceId, StringComparison.Ordinal))
                {
                    continue;
                }
                var name = announcement.DeviceName?.Trim() ?? "";
                if (name.Length is 0 or > 64 || name.Any(char.IsControl) ||
                    announcement.TcpPort is < 1 or > 65535)
                {
                    continue;
                }
                PeerSeen?.Invoke(new LanPeerPresence(
                    announcement.DeviceId,
                    name,
                    result.RemoteEndPoint.Address,
                    announcement.TcpPort,
                    announcement.AppVersion ?? "",
                    _clock.GetUtcNow().ToUnixTimeMilliseconds()));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException error)
            {
                _error = "局域网发现端口不可用：" + error.Message;
                break;
            }
            catch (JsonException)
            {
                // 非本协议的广播包直接忽略。
            }
        }
    }

    private async Task BroadcastLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var sentAtMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                var payload = JsonSerializer.SerializeToUtf8Bytes(new Announcement
                {
                    Magic = Magic,
                    Protocol = Protocol,
                    DeviceId = _deviceId,
                    DeviceName = _deviceName,
                    TcpPort = _tcpPort,
                    AppVersion = _appVersion,
                    SentAtMs = sentAtMs,
                }, JsonOptions);
                if (payload.Length > MaxAnnouncementBytes)
                {
                    _error = "局域网设备名称过长，无法广播。";
                    return;
                }
                foreach (var target in _targets)
                {
                    try
                    {
                        await _udp.SendAsync(payload, payload.Length, target).WaitAsync(_cts.Token);
                    }
                    catch (SocketException)
                    {
                        // 单个网卡的广播失败不影响其他网卡。
                    }
                }
                _lastBroadcastAtMs = sentAtMs;
                await Task.Delay(_broadcastInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException error)
            {
                _error = "局域网广播失败：" + error.Message;
                break;
            }
        }
    }

    private static List<IPEndPoint> BroadcastTargets(int port)
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, port) };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        unicast.IPv4Mask is null)
                    {
                        continue;
                    }
                    var address = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    if (address.Length != 4 || mask.Length != 4)
                    {
                        continue;
                    }
                    var broadcast = new byte[4];
                    for (var index = 0; index < 4; index++)
                    {
                        broadcast[index] = (byte)(address[index] | ~mask[index]);
                    }
                    targets.Add(new IPEndPoint(new IPAddress(broadcast), port));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 枚举网卡失败时仍保留 255.255.255.255。
        }
        return targets.Distinct().ToList();
    }

    private sealed class Announcement
    {
        public string Magic { get; set; } = "";
        public int Protocol { get; set; }
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public int TcpPort { get; set; }
        public string AppVersion { get; set; } = "";
        public long SentAtMs { get; set; }
    }
}
