using System.IO;
using System.Net;
using System.Net.Sockets;
using TokenView.Wpf.Data.Peers;

namespace TokenView.Wpf.Services;

/// <summary>只读地提供本机聚合快照；不提供任何命令、文件或数据库访问。</summary>
public sealed class LanSnapshotServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _databasePath;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly string _appVersion;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly SemaphoreSlim _clients = new(4, 4);
    private Task? _acceptTask;
    private byte[]? _cache;
    private volatile bool _dirty = true;
    private string? _error;

    public LanSnapshotServer(
        string databasePath,
        string deviceId,
        string deviceName,
        string appVersion,
        IPAddress listenAddress,
        int tcpPort,
        TimeProvider? clock = null)
    {
        _databasePath = databasePath;
        _deviceId = deviceId;
        _deviceName = deviceName;
        _appVersion = appVersion;
        _clock = clock ?? TimeProvider.System;
        _listener = StartListener(listenAddress, tcpPort);
        TcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int TcpPort { get; }

    public string? Error => _error;

    public void Start()
    {
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    public void Invalidate() => _dirty = true;

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 停止监听会让 Accept 以 SocketException/ObjectDisposedException 结束。
        }
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (remote is null || !LanAddress.IsPrivateOrLoopback(remote))
                {
                    client.Dispose();
                    continue;
                }
                _ = HandleAsync(client);
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
                _error = "局域网快照端口异常：" + error.Message;
                break;
            }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        if (!await _clients.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            client.Dispose();
            return;
        }
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                client.NoDelay = true;
                using var stream = client.GetStream();
                var line = await LanSnapshotProtocol.ReadLineAsync(stream,
                    LanSnapshotProtocol.MaxLineBytes, timeout.Token);
                if (line is null ||
                    !line.StartsWith(LanSnapshotProtocol.Magic, StringComparison.Ordinal))
                {
                    return;
                }
                var snapshot = await GetSnapshotAsync(timeout.Token);
                if (snapshot is null)
                {
                    return;
                }
                await LanSnapshotProtocol.WriteLineAsync(stream,
                    $"{LanSnapshotProtocol.Magic} {snapshot.Length}", timeout.Token);
                await stream.WriteAsync(snapshot, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // 单连接超时或服务停止；不影响其他请求。
            }
            catch (IOException)
            {
                // 对端提前断开是正常情况。
            }
            catch (SocketException)
            {
                // 同上。
            }
            finally
            {
                _clients.Release();
            }
        }
    }

    private async Task<byte[]?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_dirty && _cache is not null)
        {
            return _cache;
        }
        await _buildGate.WaitAsync(cancellationToken);
        try
        {
            if (!_dirty && _cache is not null)
            {
                return _cache;
            }
            var document = PeerSnapshotBuilder.Build(_databasePath, _deviceId, _deviceName,
                _appVersion, _clock);
            using var memory = new MemoryStream();
            PeerSnapshotSerializer.Write(memory, document);
            if (memory.Length > PeerSnapshotSerializer.MaxCompressedBytes)
            {
                throw new InvalidDataException("聚合快照超过局域网传输上限。");
            }
            _cache = memory.ToArray();
            _dirty = false;
            _error = null;
            return _cache;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _error = "本机聚合快照生成失败：" + error.Message;
            return null;
        }
        finally
        {
            _buildGate.Release();
        }
    }

    private static TcpListener StartListener(IPAddress address, int requestedPort)
    {
        if (requestedPort == 0)
        {
            var dynamic = new TcpListener(address, 0);
            dynamic.Start();
            return dynamic;
        }
        Exception? last = null;
        for (var offset = 0; offset < 10 && requestedPort + offset <= 65535; offset++)
        {
            try
            {
                var listener = new TcpListener(address, requestedPort + offset);
                listener.Start();
                return listener;
            }
            catch (SocketException error)
            {
                last = error;
            }
        }
        throw new InvalidOperationException("无法监听局域网快照端口。", last);
    }
}
