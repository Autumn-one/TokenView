using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TokenView.Wpf.Services;

public sealed record UpdateInfo(string Version, string? Notes, string? DownloadUrl, string? Sha256);

/// <summary>只访问固定 GitHub Releases 端点；更新包同样只接受该仓库的 HTTPS 地址。</summary>
public static class UpdateService
{
    public const string ReleasePage = "https://github.com/Autumn-one/TokenView/releases/latest";

    private const string Endpoint =
        "https://github.com/Autumn-one/TokenView/releases/latest/download/latest.json";

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";

    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        return await CheckAsync(client);
    }

    internal static async Task<UpdateInfo?> CheckAsync(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TokenView/{CurrentVersion}");
        var json = await client.GetStringAsync(Endpoint);
        return Parse(json);
    }

    internal static UpdateInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var latest = root.TryGetProperty("version", out var version) ? version.GetString() : null;
        if (string.IsNullOrWhiteSpace(latest)) return null;
        if (!System.Version.TryParse(latest, out var remote) ||
            !System.Version.TryParse(CurrentVersion, out var current) ||
            remote <= current)
        {
            return null;
        }

        string? download = null;
        string? sha256 = null;
        if (root.TryGetProperty("platforms", out var platforms) &&
            platforms.TryGetProperty("windows-x86_64", out var windows))
        {
            if (windows.TryGetProperty("url", out var url))
            {
                download = url.GetString();
            }
            if (windows.TryGetProperty("sha256", out var hash))
            {
                sha256 = hash.GetString();
            }
        }
        var notes = root.TryGetProperty("notes", out var notesValue) ? notesValue.GetString() : null;
        return new UpdateInfo(latest, notes, download, sha256);
    }

    internal static async Task DownloadAsync(UpdateInfo info, string targetPath,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await DownloadAsync(info, targetPath, client, progress, cancellationToken);
    }

    internal static async Task DownloadAsync(UpdateInfo info, string targetPath, HttpClient client,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!IsAllowedDownloadUrl(info.DownloadUrl))
        {
            throw new InvalidOperationException("更新地址不在允许的 GitHub Release 范围内");
        }
        if (string.IsNullOrWhiteSpace(info.Sha256))
        {
            throw new InvalidOperationException("更新包缺少 SHA-256 校验值");
        }

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TokenView/{CurrentVersion}");
        using var response = await client.GetAsync(info.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        await using (var target = File.Create(targetPath))
        {
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (total > 0)
                {
                    progress?.Report((double)received / total);
                }
            }
            await target.FlushAsync(cancellationToken);
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(targetPath);
            throw new InvalidDataException("更新包 SHA-256 校验失败");
        }
    }

    private static bool IsAllowedDownloadUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal);
    }
}
