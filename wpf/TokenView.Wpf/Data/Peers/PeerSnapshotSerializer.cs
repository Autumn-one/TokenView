using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenView.Wpf.Data.Peers;

/// <summary>多设备聚合快照的 GZip JSON 读写与严格校验。</summary>
public static class PeerSnapshotSerializer
{
    public const long MaxCompressedBytes = 32 * 1024 * 1024;
    public const long MaxDecompressedBytes = 256 * 1024 * 1024;

    private const int MaxRequestRows = 1_000_000;
    private const int MaxSessionRows = 500_000;
    private const long MinTimeMs = 946_684_800_000;      // 2000-01-01
    private const long MaxTimeMs = 4_102_444_800_000;    // 2100-01-01
    private const int MaxClientLength = 64;
    private const int MaxModelLength = 256;
    private const int MaxDeviceNameLength = 64;
    private const int MaxAppVersionLength = 32;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        MaxDepth = 16,
    };

    /// <summary>把快照写成 GZip 字节流；调用方负责流的生命周期。</summary>
    public static void Write(Stream destination, PeerSnapshotDocument document)
    {
        Validate(document);
        using var gzip = new GZipStream(destination, CompressionLevel.Fastest, leaveOpen: true);
        JsonSerializer.Serialize(gzip, document, Options);
    }

    public static void WriteFile(string path, PeerSnapshotDocument document)
    {
        Validate(document);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("快照文件路径无效。");
        }
        Directory.CreateDirectory(directory);

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Write(file, document);
            }
            File.Move(temporary, path, true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static PeerSnapshotDocument ReadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("未找到多设备快照文件。", path);
        }
        if (info.Length > MaxCompressedBytes)
        {
            throw new InvalidDataException("多设备快照文件过大，已拒绝导入。");
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Read(stream);
    }

    public static PeerSnapshotDocument Read(Stream source)
    {
        using var compressed = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            if (compressed.Length + read > MaxCompressedBytes)
            {
                throw new InvalidDataException("多设备快照文件过大，已拒绝导入。");
            }
            compressed.Write(buffer, 0, read);
        }

        compressed.Position = 0;
        var isGzip = compressed.Length >= 2 && compressed.ReadByte() == 0x1F && compressed.ReadByte() == 0x8B;
        compressed.Position = 0;
        Stream payload = isGzip
            ? new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: false)
            : compressed;
        using var limited = new LimitedReadStream(payload, MaxDecompressedBytes, ownsInner: true);
        PeerSnapshotDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<PeerSnapshotDocument>(limited, Options);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("多设备快照文件格式无效。", error);
        }
        if (document is null)
        {
            throw new InvalidDataException("多设备快照文件为空。");
        }
        Validate(document);
        return document;
    }

    public static void Validate(PeerSnapshotDocument document)
    {
        if (document.SchemaVersion != PeerSnapshotDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"另一台设备的 TokenView 版本较新（快照版本 {document.SchemaVersion}），请先升级本机 TokenView。");
        }
        if (!Guid.TryParse(document.DeviceId, out _))
        {
            throw new InvalidDataException("多设备快照的设备标识无效。");
        }
        document.DeviceName = NormalizeText(document.DeviceName, MaxDeviceNameLength, "设备名称");
        document.AppVersion = NormalizeOptionalText(document.AppVersion, MaxAppVersionLength, "应用版本");
        if (document.GeneratedAtMs < MinTimeMs || document.GeneratedAtMs >= MaxTimeMs)
        {
            throw new InvalidDataException("多设备快照的时间无效。");
        }
        if (document.UtcOffsetMinutes is < -840 or > 840)
        {
            throw new InvalidDataException("多设备快照的时区偏移无效。");
        }
        if (document.SessionsPartial < 0)
        {
            throw new InvalidDataException("多设备快照的会话计数无效。");
        }

        document.RequestDaily ??= [];
        document.RequestHourly ??= [];
        document.SessionDaily ??= [];
        document.SessionHourly ??= [];
        if (document.RequestDaily.Count + document.RequestHourly.Count > MaxRequestRows ||
            document.SessionDaily.Count + document.SessionHourly.Count > MaxSessionRows)
        {
            throw new InvalidDataException("多设备快照的数据行数过多，已拒绝导入。");
        }

        ValidateRequestRows(document.RequestDaily, hourly: false);
        ValidateRequestRows(document.RequestHourly, hourly: true);
        ValidateSessionRows(document.SessionDaily, hourly: false);
        ValidateSessionRows(document.SessionHourly, hourly: true);
    }

    private static void ValidateRequestRows(List<PeerRequestRow> rows, bool hourly)
    {
        var keys = new HashSet<(long, string, string)>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index] ?? throw new InvalidDataException($"多设备快照第 {index + 1} 行请求数据为空。");
            row.Client = NormalizeText(row.Client, MaxClientLength, "客户端");
            row.Model = NormalizeOptionalText(row.Model, MaxModelLength, "模型");
            if (row.TimeMs < MinTimeMs || row.TimeMs >= MaxTimeMs ||
                (hourly && row.TimeMs % 3_600_000 != 0))
            {
                throw new InvalidDataException($"多设备快照第 {index + 1} 行请求时间无效。");
            }
            if (row.Tokens < 0 || row.EstimatedTokens < 0 || row.Requests < 0 ||
                row.RequestsWithUsage < 0 || row.RequestsWithoutUsage < 0 ||
                row.EstimatedRequests < 0 ||
                (long)row.RequestsWithUsage + row.RequestsWithoutUsage != row.Requests)
            {
                throw new InvalidDataException($"多设备快照第 {index + 1} 行请求计数无效。");
            }
            if (!keys.Add((row.TimeMs, row.Client, row.Model)))
            {
                throw new InvalidDataException("多设备快照包含重复的请求行。");
            }
        }
    }

    private static void ValidateSessionRows(List<PeerSessionRow> rows, bool hourly)
    {
        var keys = new HashSet<(long, string)>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index] ?? throw new InvalidDataException($"多设备快照第 {index + 1} 行会话数据为空。");
            row.Client = NormalizeText(row.Client, MaxClientLength, "客户端");
            if (row.TimeMs < MinTimeMs || row.TimeMs >= MaxTimeMs ||
                (hourly && row.TimeMs % 3_600_000 != 0))
            {
                throw new InvalidDataException($"多设备快照第 {index + 1} 行会话时间无效。");
            }
            if (row.Sessions < 0 || row.EstimatedSessions < 0 || row.EstimatedSessions > row.Sessions)
            {
                throw new InvalidDataException($"多设备快照第 {index + 1} 行会话计数无效。");
            }
            if (!keys.Add((row.TimeMs, row.Client)))
            {
                throw new InvalidDataException("多设备快照包含重复的会话行。");
            }
        }
    }

    private static string NormalizeText(string? value, int maxLength, string field)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Length > maxLength || text.Any(char.IsControl))
        {
            throw new InvalidDataException($"多设备快照的{field}无效。");
        }
        return text;
    }

    private static string NormalizeOptionalText(string? value, int maxLength, string field)
    {
        var text = value?.Trim() ?? "";
        if (text.Length > maxLength || text.Any(char.IsControl))
        {
            throw new InvalidDataException($"多设备快照的{field}无效。");
        }
        return text;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件清理失败不影响主流程；导入只读取 *.tokenview.json.gz。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private readonly bool _ownsInner;
        private long _total;

        public LimitedReadStream(Stream inner, long limit, bool ownsInner)
        {
            _inner = inner;
            _limit = limit;
            _ownsInner = ownsInner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Track(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            Track(read);
            return read;
        }

        private void Track(int read)
        {
            _total += read;
            if (_total > _limit)
            {
                throw new InvalidDataException("多设备快照解压后过大，已拒绝导入。");
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsInner)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
