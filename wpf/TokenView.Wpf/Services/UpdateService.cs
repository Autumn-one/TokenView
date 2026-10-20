using System.Net.Http;
using System.Text.Json;

namespace TokenView.Wpf.Services;

public sealed record UpdateInfo(string Version, string? Notes, string? DownloadUrl);

/// <summary>仅访问固定 GitHub Releases 端点；由用户或自动检查显式触发。</summary>
public static class UpdateService
{
    private const string Endpoint =
        "https://github.com/MY-Final/TokLens/releases/latest/download/latest.json";

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";

    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TokenView/{CurrentVersion}");
        var json = await client.GetStringAsync(Endpoint);
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
        if (root.TryGetProperty("platforms", out var platforms) &&
            platforms.TryGetProperty("windows-x86_64", out var windows) &&
            windows.TryGetProperty("url", out var url))
        {
            download = url.GetString();
        }
        var notes = root.TryGetProperty("notes", out var notesValue) ? notesValue.GetString() : null;
        return new UpdateInfo(latest, notes, download ??
            "https://github.com/MY-Final/TokLens/releases/latest");
    }
}
