using System.IO;
using System.Net;
using System.Net.Sockets;
using TokenView.Wpf.Data;
using TokenView.Wpf.Data.Collectors;
using TokenView.Wpf.Data.Peers;
using TokenView.Wpf.Settings;

namespace TokenView.Wpf.Services;

/// <summary>
/// 局域网自动同步：UDP 广播发现 + TCP 拉取对端聚合快照，不读取对端数据库。
/// 默认开启；用户关闭后不监听、不广播、不访问网络。
/// </summary>
public sealed class LanSyncService
{
    public static LanSyncService Current { get; } = new();

    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FetchInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

    private readonly object _sync = new();
    private readonly Dictionary<string, LanPeerPresence> _presence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);

    private LanDiscovery? _discovery;
    private LanSnapshotServer? _server;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private LanSyncOptions? _options;
    private long? _lastImportAtMs;
    private bool _running;
    private bool _subscribed;
    private string? _error;

    public event Action? PeersChanged;
    public event Action? Changed;
    public event Action? StatusChanged;

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _running;
        }
    }

    public int UdpPort { get; private set; }

    public int TcpPort { get; private set; }

    public string? Error
    {
        get
        {
            lock (_sync)
            {
                return _error ?? _discovery?.Error ?? _server?.Error;
            }
        }
    }

    public IReadOnlyList<LanPeerStatus> Peers => BuildPeerStatus();

    public LanSyncStatus Status => new(
        _options?.Enabled ?? false,
        IsRunning,
        UdpPort,
        TcpPort,
        Error,
        _lastImportAtMs,
        BuildPeerStatus());

    public static LanSyncOptions OptionsFrom(AppSettings settings) => new(
        TokenViewDatabase.DatabasePath,
        settings.DeviceId,
        settings.DeviceName,
        UpdateService.CurrentVersion,
        settings.LanSyncEnabled);

    public void Start(LanSyncOptions options)
    {
        Stop();
        _options = options;
        if (!options.Enabled)
        {
            RaiseStatus();
            return;
        }
        if (!Guid.TryParse(options.DeviceId, out _))
        {
            SetError("设备标识无效，请重新启用局域网同步。");
            return;
        }
        var deviceName = options.DeviceName?.Trim() ?? "";
        if (deviceName.Length is 0 or > 64)
        {
            SetError("设备名称无效。");
            return;
        }

        var cts = new CancellationTokenSource();
        LanSnapshotServer? server = null;
        LanDiscovery? discovery = null;
        try
        {
            server = new LanSnapshotServer(options.DatabasePath, options.DeviceId, deviceName,
                options.AppVersion, options.ListenAddress ?? IPAddress.Any, options.TcpPort,
                options.TimeProvider);
            discovery = new LanDiscovery(options.DeviceId, deviceName, options.AppVersion,
                server.TcpPort, options.ListenAddress ?? IPAddress.Any, options.UdpPort,
                options.DiscoveryTargets, options.TimeProvider, options.BroadcastInterval);
            discovery.PeerSeen += OnPeerSeen;
            server.Start();
            discovery.Start();
            if (!_subscribed)
            {
                SyncService.Current.Completed += OnLocalSyncCompleted;
                _subscribed = true;
            }
            lock (_sync)
            {
                _server = server;
                _discovery = discovery;
                _cts = cts;
                UdpPort = discovery.UdpPort;
                TcpPort = server.TcpPort;
                _running = true;
                _error = null;
            }
            _loopTask = Task.Run(() => LoopAsync(options, cts.Token));
            RaiseStatus();
        }
        catch (Exception error)
        {
            discovery?.PeerSeen -= OnPeerSeen;
            discovery?.Dispose();
            server?.Dispose();
            if (_subscribed)
            {
                SyncService.Current.Completed -= OnLocalSyncCompleted;
                _subscribed = false;
            }
            cts.Cancel();
            cts.Dispose();
            lock (_sync)
            {
                _server = null;
                _discovery = null;
                _cts = null;
                _running = false;
                _error = DescribeStartError(error);
            }
            RaiseStatus();
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        if (!settings.LanSyncEnabled)
        {
            _options = OptionsFrom(settings) with { Enabled = false };
            Stop();
            return;
        }
        DeviceIdentity.Ensure(settings);
        var options = OptionsFrom(settings);
        if (IsRunning && Equals(_options, options))
        {
            return;
        }
        Start(options);
    }

    public void Stop()
    {
        LanDiscovery? discovery;
        LanSnapshotServer? server;
        CancellationTokenSource? cts;
        Task? loop;
        lock (_sync)
        {
            discovery = _discovery;
            server = _server;
            cts = _cts;
            loop = _loopTask;
            _discovery = null;
            _server = null;
            _cts = null;
            _loopTask = null;
            _running = false;
            _presence.Clear();
            _errors.Clear();
        }
        if (_subscribed)
        {
            SyncService.Current.Completed -= OnLocalSyncCompleted;
            _subscribed = false;
        }
        cts?.Cancel();
        if (discovery is not null)
        {
            discovery.PeerSeen -= OnPeerSeen;
            discovery.Dispose();
        }
        server?.Dispose();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // 取消导致的异常不影响停止流程。
        }
        cts?.Dispose();
        RaiseStatus();
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return;
        }
        _server?.Invalidate();
        await FetchAllAsync(cancellationToken);
        RaiseStatus();
    }

    public void NotifyPeersChanged()
    {
        PeersChanged?.Invoke();
        StatusChanged?.Invoke();
    }

    private void OnLocalSyncCompleted(SyncBatch? batch, string? error) => _server?.Invalidate();

    private void OnPeerSeen(LanPeerPresence presence)
    {
        var shouldFetch = false;
        lock (_sync)
        {
            if (_presence.TryGetValue(presence.DeviceId, out var existing))
            {
                shouldFetch = !existing.Address.Equals(presence.Address) ||
                              existing.TcpPort != presence.TcpPort ||
                              !string.Equals(existing.DeviceName, presence.DeviceName,
                                  StringComparison.Ordinal);
            }
            else
            {
                shouldFetch = true;
            }
            _presence[presence.DeviceId] = presence;
        }
        PeersChanged?.Invoke();
        StatusChanged?.Invoke();
        if (shouldFetch)
        {
            var token = _cts?.Token ?? CancellationToken.None;
            _ = FetchPeerAsync(presence.DeviceId, token);
        }
    }

    private async Task LoopAsync(LanSyncOptions options, CancellationToken cancellationToken)
    {
        var clock = options.TimeProvider ?? TimeProvider.System;
        var lastFetch = DateTimeOffset.MinValue;
        var lastHeartbeat = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow();
                if (now - lastFetch >= FetchInterval)
                {
                    lastFetch = now;
                    await FetchAllAsync(cancellationToken);
                }
                if (now - lastHeartbeat >= HeartbeatInterval)
                {
                    lastHeartbeat = now;
                    _server?.Invalidate();
                }
                ExpirePresence(now);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                SetError("局域网同步失败：" + error.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task FetchAllAsync(CancellationToken cancellationToken)
    {
        List<LanPeerPresence> peers;
        lock (_sync)
        {
            peers = _presence.Values.ToList();
        }
        foreach (var peer in peers)
        {
            await FetchPeerAsync(peer, cancellationToken);
        }
    }

    private async Task FetchPeerAsync(string deviceId, CancellationToken cancellationToken)
    {
        LanPeerPresence? peer;
        lock (_sync)
        {
            _presence.TryGetValue(deviceId, out peer);
        }
        if (peer is not null)
        {
            await FetchPeerAsync(peer, cancellationToken);
        }
    }

    private async Task FetchPeerAsync(LanPeerPresence peer, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            if (!await _exchangeGate.WaitAsync(0, cancellationToken))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            var options = _options;
            if (options is null || !IsRunning)
            {
                return;
            }
            var document = await LanSnapshotClient.FetchAsync(peer.Address, peer.TcpPort,
                options.DeviceId, FetchTimeout, cancellationToken);
            if (string.Equals(document.DeviceId, options.DeviceId, StringComparison.Ordinal))
            {
                return;
            }
            var result = PeerSnapshotStore.Import(options.DatabasePath, document);
            lock (_sync)
            {
                _errors.Remove(peer.DeviceId);
                if (result.Imported)
                {
                    _lastImportAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            if (result.Imported)
            {
                Changed?.Invoke();
                PeersChanged?.Invoke();
                StatusChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            SetPeerError(peer.DeviceId, DescribeFetchError(error));
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    private void ExpirePresence(DateTimeOffset now)
    {
        var removed = false;
        lock (_sync)
        {
            var expired = _presence
                .Where(pair => !pair.Value.IsOnline(now, PresenceTimeout))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var key in expired)
            {
                _presence.Remove(key);
                removed = true;
            }
        }
        if (removed)
        {
            PeersChanged?.Invoke();
            StatusChanged?.Invoke();
        }
    }

    private IReadOnlyList<LanPeerStatus> BuildPeerStatus()
    {
        var options = _options;
        var now = (options?.TimeProvider ?? TimeProvider.System).GetUtcNow();
        List<LanPeerPresence> presence;
        Dictionary<string, string> errors;
        lock (_sync)
        {
            presence = _presence.Values.ToList();
            errors = new Dictionary<string, string>(_errors, StringComparer.Ordinal);
        }
        var databasePath = options?.DatabasePath ?? TokenViewDatabase.DatabasePath;
        var devices = PeerSnapshotStore.ListDevices(databasePath);
        var result = new List<LanPeerStatus>();
        foreach (var device in devices)
        {
            var seen = presence.FirstOrDefault(peer =>
                string.Equals(peer.DeviceId, device.DeviceId, StringComparison.Ordinal));
            result.Add(new LanPeerStatus(
                device.DeviceId,
                seen?.DeviceName ?? device.Name,
                seen is not null && seen.IsOnline(now, PresenceTimeout),
                seen?.EndpointText ?? "—",
                seen?.LastSeenMs,
                device.GeneratedAtMs,
                device.ImportedAtMs,
                errors.GetValueOrDefault(device.DeviceId)));
        }
        foreach (var seen in presence)
        {
            if (devices.Any(device =>
                    string.Equals(device.DeviceId, seen.DeviceId, StringComparison.Ordinal)))
            {
                continue;
            }
            result.Add(new LanPeerStatus(seen.DeviceId, seen.DeviceName, true, seen.EndpointText,
                seen.LastSeenMs, null, null, errors.GetValueOrDefault(seen.DeviceId)));
        }
        return result
            .OrderByDescending(peer => peer.Online)
            .ThenBy(peer => peer.DeviceName, StringComparer.CurrentCulture)
            .ToList();
    }

    private void SetPeerError(string deviceId, string message)
    {
        lock (_sync)
        {
            _errors[deviceId] = message;
        }
        PeersChanged?.Invoke();
        StatusChanged?.Invoke();
    }

    private void SetError(string message)
    {
        lock (_sync)
        {
            _error = message;
        }
        RaiseStatus();
    }

    private void RaiseStatus() => StatusChanged?.Invoke();

    private static string DescribeStartError(Exception error) => error switch
    {
        SocketException => "无法启动局域网同步：发现端口被占用。请关闭占用 UDP 17831 的程序，或在设置中关闭局域网同步。",
        _ => "无法启动局域网同步：" + error.Message,
    };

    private static string DescribeFetchError(Exception error) => error switch
    {
        SocketException => "连接失败（设备离线或防火墙阻止）",
        IOException => "连接中断",
        InvalidDataException => error.Message,
        OperationCanceledException => "连接超时",
        _ => "同步失败：" + error.Message,
    };
}
