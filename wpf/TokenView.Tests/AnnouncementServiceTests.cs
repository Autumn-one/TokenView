using System.IO;
using TokenView.Wpf.Services;
using Xunit;

namespace TokenView.Tests;

public sealed class AnnouncementServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    [Fact]
    public async Task UnreadStateTracksPersistedHash()
    {
        string? stored = null;
        var fetcher = new StubContentFetcher
        {
            OnFetch = _ => Task.FromResult(Success("hash-1")),
        };
        var service = new AnnouncementService(fetcher, () => stored, value => stored = value);

        await service.RefreshAsync();

        Assert.NotNull(service.Content);
        Assert.True(service.HasUnread);
        service.MarkRead("hash-1");
        Assert.False(service.HasUnread);
        Assert.Equal("hash-1", stored);

        fetcher.OnFetch = _ => Task.FromResult(Success("hash-2"));
        await service.RefreshAsync();
        Assert.True(service.HasUnread);
    }

    [Fact]
    public async Task ExistingHashMeansAlreadyRead()
    {
        var fetcher = new StubContentFetcher
        {
            OnFetch = _ => Task.FromResult(Success("hash-1")),
        };
        var service = new AnnouncementService(fetcher, () => "hash-1", _ => { });

        await service.RefreshAsync();

        Assert.NotNull(service.Content);
        Assert.False(service.HasUnread);
    }

    [Fact]
    public async Task FailureKeepsLastSuccessfulContent()
    {
        var fetcher = new StubContentFetcher
        {
            OnFetch = _ => Task.FromResult(Success("hash-1")),
        };
        var service = new AnnouncementService(fetcher, () => null, _ => { });
        await service.RefreshAsync();

        fetcher.OnFetch = _ => Task.FromResult(ContentFetchResult.Failure("all sources failed", Now));
        await service.RefreshAsync();

        Assert.NotNull(service.Content);
        Assert.Equal("hash-1", service.Content!.ContentHash);
        Assert.Equal("all sources failed", service.Error);
    }

    [Fact]
    public async Task MarkReadPersistenceFailureKeepsUnreadAndReportsError()
    {
        var fetcher = new StubContentFetcher
        {
            OnFetch = _ => Task.FromResult(Success("hash-1")),
        };
        var service = new AnnouncementService(fetcher, () => null, _ => throw new IOException("disk full"));
        await service.RefreshAsync();

        service.MarkRead("hash-1");

        Assert.True(service.HasUnread);
        Assert.NotNull(service.Error);
    }

    [Fact]
    public async Task OverlappingRefreshIsSkipped()
    {
        var gate = new TaskCompletionSource<ContentFetchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new StubContentFetcher { OnFetch = _ => gate.Task };
        var service = new AnnouncementService(fetcher, () => null, _ => { });

        var first = service.RefreshAsync();
        var second = service.RefreshAsync();

        Assert.Same(first, second);
        Assert.Equal(1, fetcher.Calls);
        gate.SetResult(Success("hash-1"));
        await first;
        Assert.Equal(1, fetcher.Calls);
    }

    private static ContentFetchResult Success(string hash) =>
        ContentFetchResult.Ok(new MarkdownContent("# title", hash, "source", "https://source.test/a.md", Now));
}
