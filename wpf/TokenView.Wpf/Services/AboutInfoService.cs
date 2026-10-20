namespace TokenView.Wpf.Services;

public sealed class AboutInfoService
{
    private readonly IContentFetcher _fetcher;
    private MirrorsConfig _config = MirrorsConfig.Default;
    private Task? _inflight;

    public static AboutInfoService Current { get; } = new(MirrorContentService.Shared);

    public AboutInfoService(IContentFetcher fetcher)
    {
        _fetcher = fetcher;
    }

    public MarkdownContent? Content { get; private set; }

    public string? Error { get; private set; }

    public bool IsFetching { get; private set; }

    public bool HasAttempted { get; private set; }

    public event Action? Changed;

    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (Content is not null || HasAttempted)
        {
            return _inflight is { IsCompleted: false } ? _inflight : Task.CompletedTask;
        }
        HasAttempted = true;
        return StartAsync(cancellationToken);
    }

    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        HasAttempted = true;
        return StartAsync(cancellationToken);
    }

    private Task StartAsync(CancellationToken cancellationToken)
    {
        if (_inflight is { IsCompleted: false })
        {
            return _inflight;
        }
        _inflight = LoadCoreAsync(cancellationToken);
        return _inflight;
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        IsFetching = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            _config = await _fetcher.ResolveConfigAsync(_config, cancellationToken);
            var result = await _fetcher.FetchAsync(_config, ContentKind.PersonalInfo, cancellationToken);
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
                Error = result.ErrorMessage ?? "获取关于信息失败。";
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
