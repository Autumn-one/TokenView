using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TokenView.Wpf.Services;

public sealed class MirrorContentService : IContentFetcher
{
    public const int MaxContentBytes = 512 * 1024;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    public static MirrorContentService Shared { get; } = new();

    private readonly HttpClient _client;
    private readonly TimeProvider _time;
    private readonly string _userAgent;

    public MirrorContentService(
        HttpMessageHandler? handler = null,
        string? userAgent = null,
        TimeProvider? time = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler, false);
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _userAgent = string.IsNullOrWhiteSpace(userAgent)
            ? $"TokenView/{UpdateService.CurrentVersion}"
            : userAgent;
        _time = time ?? TimeProvider.System;
    }

    public async Task<MirrorsConfig> ResolveConfigAsync(
        MirrorsConfig fallback,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in fallback.ConfigSources
                     .Where(source => source.Enabled)
                     .OrderBy(source => source.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = fallback.ConfigUrl(source);
            if (url is null || !seen.Add(url))
            {
                continue;
            }

            var raw = await GetRawAsync(url, cancellationToken);
            if (raw.Bytes is null)
            {
                continue;
            }

            try
            {
                return MirrorsConfig.Merge(fallback, Encoding.UTF8.GetString(raw.Bytes));
            }
            catch (JsonException)
            {
                // 无效配置继续尝试下一个源，最终回落到内置配置。
            }
        }
        return fallback;
    }

    public async Task<ContentFetchResult> FetchAsync(
        MirrorsConfig config,
        ContentKind kind,
        CancellationToken cancellationToken)
    {
        var fetchedAt = _time.GetUtcNow();
        var timestamp = fetchedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attempted = 0;
        string? lastError = null;

        foreach (var source in config.Sources(kind)
                     .Where(source => source.Enabled)
                     .OrderBy(source => source.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = config.ContentUrl(source, kind);
            if (url is null || !seen.Add(url))
            {
                continue;
            }

            attempted++;
            var requestUrl = AppendTimestamp(url, timestamp);
            var raw = await GetRawAsync(requestUrl, cancellationToken);
            if (raw.Bytes is null)
            {
                lastError = $"{source.Id}: {raw.Error}";
                continue;
            }

            var markdown = DecodeUtf8(raw.Bytes);
            if (StartsWithHtml(markdown))
            {
                lastError = $"{source.Id}: 返回了 HTML 页面";
                continue;
            }

            var content = new MarkdownContent(
                markdown,
                Convert.ToHexString(SHA256.HashData(raw.Bytes)).ToLowerInvariant(),
                source.Id,
                requestUrl,
                _time.GetUtcNow());
            return ContentFetchResult.Ok(content);
        }

        var label = kind == ContentKind.Announcement ? "公告" : "关于信息";
        var message = attempted == 0
            ? $"没有可用的{label}内容源。"
            : $"所有{label}内容源均获取失败：{lastError ?? "未知错误"}";
        return ContentFetchResult.Failure(message, _time.GetUtcNow());
    }

    internal static string AppendTimestamp(string url, string timestamp) =>
        url.Contains('?')
            ? $"{url}&_={timestamp}"
            : $"{url}?_={timestamp}";

    internal static string DecodeUtf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    internal static bool StartsWithHtml(string markdown)
    {
        var trimmed = markdown.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<RawFetch> GetRawAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(_userAgent);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new RawFetch(null, $"HTTP {(int)response.StatusCode}");
            }
            if (response.Content.Headers.ContentLength > MaxContentBytes)
            {
                return new RawFetch(null, "响应超过 512 KiB");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, timeout.Token);
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > MaxContentBytes)
                {
                    return new RawFetch(null, "响应超过 512 KiB");
                }
                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            return bytes.Length == 0
                ? new RawFetch(null, "响应正文为空")
                : new RawFetch(bytes, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new RawFetch(null, "请求超时（12 秒）");
        }
        catch (HttpRequestException error)
        {
            return new RawFetch(null, error.Message);
        }
        catch (IOException error)
        {
            return new RawFetch(null, error.Message);
        }
    }

    private sealed record RawFetch(byte[]? Bytes, string? Error);
}
