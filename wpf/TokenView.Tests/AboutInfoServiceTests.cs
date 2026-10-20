using TokenView.Wpf.Services;
using Xunit;

namespace TokenView.Tests;

public sealed class AboutInfoServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    [Fact]
    public async Task AutoLoadAttemptsOnlyOncePerProcess()
    {
        var fetcher = new StubContentFetcher
        {
            OnFetch = _ => Task.FromResult(Success("hash-1")),
        };
        var service = new AboutInfoService(fetcher);

        await service.EnsureLoadedAsync();
        await service.EnsureLoadedAsync();

        Assert.Equal(1, fetcher.Calls);
        Assert.True(service.HasAttempted);
        Assert.NotNull(service.Content);
    }

    [Fact]
    public async Task FailureDoesNotAutoRetryButManualRetryDoes()
    {
        var fetcher = new StubContentFetcher
        {
            OnFetch = _ => Task.FromResult(ContentFetchResult.Failure("offline", Now)),
        };
        var service = new AboutInfoService(fetcher);

        await service.EnsureLoadedAsync();
        Assert.Null(service.Content);
        Assert.Equal("offline", service.Error);

        await service.EnsureLoadedAsync();
        Assert.Equal(1, fetcher.Calls);

        fetcher.OnFetch = _ => Task.FromResult(Success("hash-1"));
        await service.RetryAsync();

        Assert.Equal(2, fetcher.Calls);
        Assert.NotNull(service.Content);
        Assert.Null(service.Error);
    }

    [Fact]
    public async Task OverlappingAutoLoadIsSkipped()
    {
        var gate = new TaskCompletionSource<ContentFetchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new StubContentFetcher { OnFetch = _ => gate.Task };
        var service = new AboutInfoService(fetcher);

        var first = service.EnsureLoadedAsync();
        var second = service.EnsureLoadedAsync();

        Assert.Same(first, second);
        Assert.Equal(1, fetcher.Calls);
        gate.SetResult(Success("hash-1"));
        await first;
    }

    [Fact]
    public async Task LaterFailureKeepsSuccessContent()
    {
        var fetcher = new StubContentFetcher
        {
            OnFetch = _ => Task.FromResult(Success("hash-1")),
        };
        var service = new AboutInfoService(fetcher);
        await service.EnsureLoadedAsync();

        fetcher.OnFetch = _ => Task.FromResult(ContentFetchResult.Failure("offline", Now));
        await service.RetryAsync();

        Assert.NotNull(service.Content);
        Assert.Equal("offline", service.Error);
    }

    private static ContentFetchResult Success(string hash) =>
        ContentFetchResult.Ok(new MarkdownContent("# info", hash, "source", "https://source.test/info.md", Now));
}
