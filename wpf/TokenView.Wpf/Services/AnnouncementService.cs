using System.IO;
using TokenView.Wpf.Settings;

namespace TokenView.Wpf.Services;

public sealed class AnnouncementService
{
    private readonly IContentFetcher _fetcher;
    private readonly Action<string?> _writeHash;
    private MirrorsConfig _config = MirrorsConfig.Default;
    private Task? _inflight;
    private string? _lastReadHash;

    public static AnnouncementService Current { get; } = new(
        MirrorContentService.Shared,
        SettingsStore.LastReadAnnouncementHash,
        SettingsStore.SaveLastReadAnnouncementHash);

    public AnnouncementService(
        IContentFetcher fetcher,
        Func<string?> readHash,
        Action<string?> writeHash)
    {
        _fetcher = fetcher;
        _writeHash = writeHash;
        _lastReadHash = readHash();
    }

    public MarkdownContent? Content { get; private set; }

    public string? Error { get; private set; }

    public bool IsFetching { get; private set; }

    public bool HasUnread => Content is { } content && !IsRead(content.ContentHash);

    public event Action? Changed;

    public bool IsRead(string hash) =>
        string.Equals(_lastReadHash, hash, StringComparison.OrdinalIgnoreCase);

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_inflight is { IsCompleted: false })
        {
            return _inflight;
        }
        _inflight = RefreshCoreAsync(cancellationToken);
        return _inflight;
    }

    public void MarkRead(string hash)
    {
        if (string.IsNullOrEmpty(hash) || IsRead(hash))
        {
            return;
        }
        try
        {
            _writeHash(hash);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Error = $"无法保存已读状态：{error.Message}";
            Changed?.Invoke();
            return;
        }
        _lastReadHash = hash;
        Changed?.Invoke();
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        IsFetching = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            _config = await _fetcher.ResolveConfigAsync(_config, cancellationToken);
            var result = await _fetcher.FetchAsync(_config, ContentKind.Announcement, cancellationToken);
            if (result.Success && result.Markdown is { } markdown && result.ContentHash is { } hash)
            {
                Content = new MarkdownContent(
                    markdown,
                    hash,
                    result.SourceId ?? "",
                    result.SourceUrl ?? "",
                    result.FetchedAtUtc);
            }
            else
            {
                Error = result.ErrorMessage ?? "获取公告失败。";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Error = error.Message;
        }
        finally
        {
            IsFetching = false;
            Changed?.Invoke();
        }
    }
}
