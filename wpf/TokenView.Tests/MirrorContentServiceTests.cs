using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TokenView.Wpf.Services;
using Xunit;

namespace TokenView.Tests;

public sealed class MirrorContentServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    [Fact]
    public async Task FetchesSourcesInPriorityOrderAndStopsAtFirstSuccess()
    {
        var handler = new StubHttpHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("first.test", StringComparison.Ordinal))
            {
                Assert.Equal("Test/1.0", request.Headers.UserAgent.ToString());
                return StubHttpHandler.Status(HttpStatusCode.InternalServerError);
            }
            if (url.Contains("second.test", StringComparison.Ordinal))
            {
                return StubHttpHandler.Ok("# second");
            }
            throw new InvalidOperationException("third source must not be requested");
        });
        var service = Service(handler);
        var config = Config(
            new SourceConfig("third", true, 30, "https://third.test/a.md"),
            new SourceConfig("second", true, 20, "https://second.test/a.md"),
            new SourceConfig("first", true, 10, "https://first.test/a.md"));

        var result = await service.FetchAsync(config, ContentKind.Announcement, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("second", result.SourceId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("first.test", handler.Requests[0]);
        Assert.Contains("second.test", handler.Requests[1]);
    }

    [Fact]
    public async Task Http200HtmlErrorPageFallsThroughToNextSource()
    {
        var handler = new StubHttpHandler(request =>
            request.RequestUri!.ToString().Contains("html.test", StringComparison.Ordinal)
                ? StubHttpHandler.Ok("\uFEFF  \r\n<!doctype html><html><body>proxy error</body></html>")
                : StubHttpHandler.Ok("# real"));
        var service = Service(handler);
        var config = Config(
            new SourceConfig("html", true, 10, "https://html.test/a.md"),
            new SourceConfig("real", true, 20, "https://real.test/a.md"));

        var result = await service.FetchAsync(config, ContentKind.Announcement, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("real", result.SourceId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DisabledSourceIsNotRequested()
    {
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.ToString().Contains("disabled.test", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("disabled source must not be requested");
            }
            return StubHttpHandler.Ok("# enabled");
        });
        var service = Service(handler);
        var config = Config(
            new SourceConfig("disabled", false, 10, "https://disabled.test/a.md"),
            new SourceConfig("enabled", true, 20, "https://enabled.test/a.md"));

        var result = await service.FetchAsync(config, ContentKind.Announcement, CancellationToken.None);

        Assert.True(result.Success);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("enabled.test", request);
    }

    [Fact]
    public async Task InvalidTemplatesAndDuplicateUrlsAreSkipped()
    {
        var handler = new StubHttpHandler(request =>
            request.RequestUri!.ToString().Contains("dup.test", StringComparison.Ordinal)
                ? StubHttpHandler.Status(HttpStatusCode.BadGateway)
                : StubHttpHandler.Ok("# ok"));
        var service = Service(handler);
        var config = Config(
            new SourceConfig("template", true, 5, "https://example.test/{unknown}"),
            new SourceConfig("duplicate", true, 10, "https://dup.test/a.md"),
            new SourceConfig("duplicate-again", true, 20, "https://dup.test/a.md"),
            new SourceConfig("valid", true, 30, "https://valid.test/a.md"));

        var result = await service.FetchAsync(config, ContentKind.Announcement, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("valid", result.SourceId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("dup.test", handler.Requests[0]);
        Assert.Contains("valid.test", handler.Requests[1]);
    }

    [Fact]
    public async Task MarkdownRequestGetsTimestampQuery()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Ok("# ok"));
        var service = Service(handler);
        var config = Config(new SourceConfig("plain", true, 10, "https://plain.test/a.md"));

        await service.FetchAsync(config, ContentKind.Announcement, CancellationToken.None);

        Assert.Equal($"https://plain.test/a.md?_={Now.ToUnixTimeMilliseconds()}", handler.Requests[0]);
    }

    [Fact]
    public async Task TimestampUsesAmpersandWhenQueryAlreadyExists()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Ok("# ok"));
        var service = Service(handler);
        var config = Config(new SourceConfig("query", true, 10, "https://query.test/a.md?x=1"));

        await service.FetchAsync(config, ContentKind.Announcement, CancellationToken.None);

        Assert.Equal($"https://query.test/a.md?x=1&_={Now.ToUnixTimeMilliseconds()}", handler.Requests[0]);
    }

    [Fact]
    public async Task ContentHashUsesRawResponseBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("line1\r\nline2\nno trailing newline");
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var handler = new StubHttpHandler(_ => StubHttpHandler.Bytes(bytes));
        var service = Service(handler);
        var config = Config(new SourceConfig("raw", true, 10, "https://raw.test/a.md"));

        var result = await service.FetchAsync(config, ContentKind.Announcement, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(expected, result.ContentHash);
        Assert.Equal(Encoding.UTF8.GetString(bytes), result.Markdown);
    }

    [Fact]
    public async Task EmptyAndOversizedResponsesFallThrough()
    {
        var handler = new StubHttpHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("empty.test", StringComparison.Ordinal))
            {
                return StubHttpHandler.Bytes([]);
            }
            if (url.Contains("huge.test", StringComparison.Ordinal))
            {
                return StubHttpHandler.Bytes(new byte[MirrorContentService.MaxContentBytes + 1]);
            }
            return StubHttpHandler.Ok("# ok");
        });
        var service = Service(handler);
        var config = Config(
            new SourceConfig("empty", true, 10, "https://empty.test/a.md"),
            new SourceConfig("huge", true, 20, "https://huge.test/a.md"),
            new SourceConfig("ok", true, 30, "https://ok.test/a.md"));

        var result = await service.FetchAsync(config, ContentKind.Announcement, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ok", result.SourceId);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task AllConfigSourcesFailingStillFetchesWithBuiltinConfig()
    {
        var handler = new StubHttpHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("config.test", StringComparison.Ordinal))
            {
                return StubHttpHandler.Status(HttpStatusCode.BadGateway);
            }
            if (url.Contains("content.test", StringComparison.Ordinal))
            {
                return StubHttpHandler.Ok("# content");
            }
            throw new InvalidOperationException($"unexpected request {url}");
        });
        var service = Service(handler);
        var config = new MirrorsConfig
        {
            Announcement = new RepoConfig("owner", "repo", "main", "README.md"),
            ConfigSources = [new SourceConfig("config", true, 1, "https://config.test/mirrors.json")],
            AnnouncementSources = [new SourceConfig("content", true, 10, "https://content.test/README.md")],
        };

        var resolved = await service.ResolveConfigAsync(config, CancellationToken.None);
        var result = await service.FetchAsync(resolved, ContentKind.Announcement, CancellationToken.None);

        Assert.Same(config, resolved);
        Assert.True(result.Success);
        Assert.Equal("content", result.SourceId);
        Assert.DoesNotContain("_=", handler.Requests[0]);
        Assert.Contains("_=", handler.Requests[1]);
    }

    [Fact]
    public async Task RemoteConfigOverridesRepoAndSourceListForNextFetch()
    {
        var handler = new StubHttpHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("config.test", StringComparison.Ordinal))
            {
                return StubHttpHandler.Ok("""
                    {
                      "announcement": { "file": "next.md" },
                      "announcementSources": [
                        {
                          "id": "remote",
                          "enabled": true,
                          "priority": 5,
                          "url": "https://content.test/{announcementOwner}/{announcementRepo}/{announcementBranch}/{announcementFile}"
                        }
                      ]
                    }
                    """);
            }
            return StubHttpHandler.Ok("# remote");
        });
        var service = Service(handler);
        var config = new MirrorsConfig
        {
            Announcement = new RepoConfig("owner", "repo", "main", "README.md"),
            ConfigSources = [new SourceConfig("config", true, 1, "https://config.test/mirrors.json")],
            AnnouncementSources = [new SourceConfig("builtin", true, 10, "https://builtin.test/a.md")],
        };

        var resolved = await service.ResolveConfigAsync(config, CancellationToken.None);
        var result = await service.FetchAsync(resolved, ContentKind.Announcement, CancellationToken.None);

        Assert.Equal("next.md", resolved.Announcement.File);
        Assert.Equal("remote", result.SourceId);
        Assert.Contains("https://content.test/owner/repo/main/next.md?_=", result.SourceUrl);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Ok("# ok"));
        var service = Service(handler);
        var config = Config(new SourceConfig("source", true, 10, "https://source.test/a.md"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.FetchAsync(config, ContentKind.Announcement, cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    private static MirrorContentService Service(StubHttpHandler handler) =>
        new(handler, "Test/1.0", new FixedTimeProvider(Now));

    private static MirrorsConfig Config(params SourceConfig[] sources) => new()
    {
        Announcement = new RepoConfig("owner", "repo", "main", "README.md"),
        PersonalInfo = new RepoConfig("owner", "repo", "main", "info.md"),
        ConfigSources = [],
        AnnouncementSources = sources,
        PersonalInfoSources = [],
    };
}
