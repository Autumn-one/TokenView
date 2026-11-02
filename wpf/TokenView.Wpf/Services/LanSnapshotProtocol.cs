using System.IO;
using System.Text;

namespace TokenView.Wpf.Services;

/// <summary>局域网快照的最小行协议；只支持一种请求，不暴露通用命令通道。</summary>
internal static class LanSnapshotProtocol
{
    public const string Magic = "TOKENVIEW/1";
    public const int MaxLineBytes = 256;

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<string?> ReadLineAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken);
            if (read == 0)
            {
                return length == 0 ? null : Encoding.ASCII.GetString(buffer, 0, length);
            }
            var value = buffer[length];
            if (value == (byte)'\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, length);
            }
            if (value == (byte)'\r')
            {
                continue;
            }
            length++;
        }
        return null;
    }

    public static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken);
            if (read == 0)
            {
                throw new IOException("对端提前关闭连接。");
            }
            offset += read;
        }
    }
}
