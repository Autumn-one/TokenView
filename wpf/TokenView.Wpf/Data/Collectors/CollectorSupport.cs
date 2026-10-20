using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>采集器共用的只读发现、指纹与 JSONL 读取工具。</summary>
public static class CollectorSupport
{
    public static string? HomeDir()
    {
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(profile)) return profile;
        var home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(home) ? null : home;
    }

    public static string? EnvPath(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? Combine(string? root, params string[] parts) =>
        root is null ? null : Path.Combine(new[] { root }.Concat(parts).ToArray());

    public static List<string> JsonlFiles(string root, SyncReport report) =>
        DiscoverFiles(root, report, path =>
            string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase));

    public static List<string> DiscoverFiles(string root, SyncReport report, Func<string, bool> accept)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        var files = new List<string>();
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw SyncException.Directory(error);
            }
            foreach (var entry in entries)
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        directories.Push(entry);
                    }
                    else if (File.Exists(entry) && accept(entry))
                    {
                        files.Add(entry);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw SyncException.Directory(error);
                }
            }
        }
        files.Sort(StringComparer.Ordinal);
        report.FilesSeen = files.Count;
        return files;
    }

    public static (long Size, long MtimeMs) Fingerprint(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("source file disappeared", path);
        return (info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
    }

    /// <summary>按字节读取 JSONL；回调第二个参数表示该行是否有换行符结尾。</summary>
    /// <remarks>
    /// 行缓冲从 <see cref="ArrayPool{T}"/> 租用并跨行复用：客户端转录单行常超过 85 KB
    /// （LOH 阈值），逐行 List/ToArray 会把每次同步变成数百 MB 的大对象分配。
    /// </remarks>
    public static void ReadJsonLines(string path, Action<string, bool> onLine)
    {
        const int BufferSize = 64 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var line = ArrayPool<byte>.Shared.Rent(BufferSize);
        var lineLength = 0;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, BufferSize)) > 0)
            {
                var offset = 0;
                while (offset < read)
                {
                    var remaining = buffer.AsSpan(offset, read - offset);
                    var newline = remaining.IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        Append(remaining);
                        break;
                    }
                    Append(remaining[..newline]);
                    Emit(true);
                    offset += newline + 1;
                }
            }
            if (lineLength > 0) Emit(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(line);
        }

        void Append(ReadOnlySpan<byte> chunk)
        {
            if (chunk.IsEmpty) return;
            var required = lineLength + chunk.Length;
            if (required > line.Length)
            {
                var grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, line.Length * 2));
                line.AsSpan(0, lineLength).CopyTo(grown);
                ArrayPool<byte>.Shared.Return(line);
                line = grown;
            }
            chunk.CopyTo(line.AsSpan(lineLength));
            lineLength += chunk.Length;
        }

        void Emit(bool complete)
        {
            var length = lineLength > 0 && line[lineLength - 1] == (byte)'\r' ? lineLength - 1 : lineLength;
            var text = Encoding.UTF8.GetString(line.AsSpan(0, length));
            lineLength = 0;
            onLine(text, complete);
        }
    }

    public static string? String(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long? Long(this JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number >= 0 ? number : null,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) && parsed >= 0 => parsed,
            _ => null,
        };
    }

    public static bool Bool(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        (value.ValueKind is JsonValueKind.True or JsonValueKind.False) && value.GetBoolean();

    public static JsonElement? Element(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    public static bool IsEmptyLine(string line) => string.IsNullOrWhiteSpace(line);

    public static bool ValidCounts(RawTokenCounts counts)
    {
        long total = 0;
        foreach (var value in new[] { counts.Input, counts.Output, counts.CacheRead, counts.CacheWrite, counts.Reasoning })
        {
            if (value is not { } number) continue;
            if (number < 0 || total > long.MaxValue - number) return false;
            total += number;
        }
        return counts.ClientTotal is not { } clientTotal || (clientTotal >= 0 && clientTotal <= long.MaxValue);
    }

    public static SourceFile NewSourceFile(string client, string path, SourceFileKind kind,
        long size, long mtime, long contentOffset, long parserVersion, long unparsed, long? scannedAt = null) =>
        new(client, path, kind, size, mtime, contentOffset, parserVersion, scannedAt ?? UsageTime.NowMs(), unparsed);

    public static bool IsUnchanged(SourceFileRow? existing, long size, long mtime, long parserVersion) =>
        existing is not null && existing.SizeBytes == size && existing.MtimeMs == mtime &&
        existing.ParserVersion == parserVersion;

    public static long StartOfHour(long value) => value / 3_600_000 * 3_600_000;

    public static void CountReason(SyncReport report, ScanReason reason)
    {
        report.Reasons[reason] = report.Reasons.GetValueOrDefault(reason) + 1;
    }
}
