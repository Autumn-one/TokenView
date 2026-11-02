using System.IO;
using System.Net;
using System.Net.Sockets;
using TokenView.Wpf.Data.Peers;

namespace TokenView.Wpf.Services;

/// <summary>从对端拉取一份聚合快照；协议只允许读取快照。</summary>
public static class LanSnapshotClient
{
    public static async Task<PeerSnapshotDocument> FetchAsync(
        IPAddress address,
        int port,
        string requesterDeviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        await client.ConnectAsync(address, port, linked.Token);
        client.NoDelay = true;
        using var stream = client.GetStream();
        await LanSnapshotProtocol.WriteLineAsync(stream,
            $"{LanSnapshotProtocol.Magic} {requesterDeviceId}", linked.Token);
        var line = await LanSnapshotProtocol.ReadLineAsync(stream,
            LanSnapshotProtocol.MaxLineBytes, linked.Token)
            ?? throw new InvalidDataException("对端未返回快照响应头。");
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !string.Equals(parts[0], LanSnapshotProtocol.Magic, StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out var length) ||
            length <= 0 ||
            length > PeerSnapshotSerializer.MaxCompressedBytes)
        {
            throw new InvalidDataException("对端快照响应无效。");
        }
        var payload = new byte[length];
        await LanSnapshotProtocol.ReadExactlyAsync(stream, payload, linked.Token);
        using var memory = new MemoryStream(payload);
        return PeerSnapshotSerializer.Read(memory);
    }
}
