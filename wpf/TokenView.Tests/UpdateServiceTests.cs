using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TokenView.Wpf.Services;
using Xunit;

namespace TokenView.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("https://github.com/other/TokenView/releases/download/v99/a.zip")]
    [InlineData("https://github.com/Autumn-one/other/releases/download/v99/a.zip")]
    [InlineData("https://github.com/Autumn-one/TokenView/other/releases/download/v99/a.zip")]
    [InlineData("https://github.com/Autumn-one/TokenView/releases/download/")]
    [InlineData("https://github.com:444/Autumn-one/TokenView/releases/download/v99/a.zip")]
    [InlineData("https://user@github.com/Autumn-one/TokenView/releases/download/v99/a.zip")]
    [InlineData("http://github.com/Autumn-one/TokenView/releases/download/v99/a.zip")]
    public async Task RejectsUntrustedRepositoryBeforeAnyRequest(string url)
    {
        var calls = 0;
        using var client = new HttpClient(new StubHttpHandler(_ =>
        {
            calls++;
            return StubHttpHandler.Bytes([1]);
        }));
        var info = new UpdateInfo("99.0.0", null, url, new string('a', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.DownloadAsync(info,
            "unused.zip", client, null, CancellationToken.None));
        Assert.Equal(0, calls);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = "99.0.0",
            platforms = new Dictionary<string, object> { ["windows-x86_64"] = new { url, sha256 = info.Sha256 } },
        });
        Assert.Throws<InvalidDataException>(() => UpdateService.Parse(json));
    }

    [Theory]
    [InlineData("abc123")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task RejectsMalformedHashBeforeAnyRequest(string hash)
    {
        using var client = new HttpClient(new StubHttpHandler(_ => throw new Exception("unexpected HTTP")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.DownloadAsync(
            new UpdateInfo("99.0.0", null, DownloadUrl, hash), "unused.zip", client, null, CancellationToken.None));
    }

    private const string DownloadUrl =
        "https://github.com/Autumn-one/TokenView/releases/download/v99.0.0/TokenView-99.0.0-win-x64.zip";

    [Fact]
    public void ParseReadsVersionNotesUrlAndHash()
    {
        var json = $$"""
            {
              "version": "99.0.0",
              "notes": "fixes",
              "platforms": {
                "windows-x86_64": {
                  "url": "{{DownloadUrl}}",
                  "sha256": "{{new string('a', 64)}}"
                }
              }
            }
            """;

        var info = UpdateService.Parse(json);

        Assert.NotNull(info);
        Assert.Equal("99.0.0", info!.Version);
        Assert.Equal("fixes", info.Notes);
        Assert.Equal(DownloadUrl, info.DownloadUrl);
        Assert.Equal(new string('a', 64), info.Sha256);
    }

    [Fact]
    public void ParseIgnoresOlderOrCurrentVersions()
    {
        var json = $$"""
            {
              "version": "{{UpdateService.CurrentVersion}}",
              "platforms": { "windows-x86_64": { "url": "{{DownloadUrl}}", "sha256": "abc" } }
            }
            """;

        Assert.Null(UpdateService.Parse(json));
    }

    [Fact]
    public async Task DownloadWritesFileWhenHashMatches()
    {
        var payload = Encoding.UTF8.GetBytes("update-bytes");
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using var client = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Bytes(payload)));
        var info = new UpdateInfo("99.0.0", null, DownloadUrl, hash);
        var path = Path.Combine(Path.GetTempPath(), $"tokenview-update-{Guid.NewGuid():N}.zip");
        try
        {
            await UpdateService.DownloadAsync(info, path, client, null, CancellationToken.None);

            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DownloadDeletesFileWhenHashDiffers()
    {
        var payload = Encoding.UTF8.GetBytes("update-bytes");
        using var client = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Bytes(payload)));
        var info = new UpdateInfo("99.0.0", null, DownloadUrl, new string('0', 64));
        var path = Path.Combine(Path.GetTempPath(), $"tokenview-update-{Guid.NewGuid():N}.zip");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                UpdateService.DownloadAsync(info, path, client, null, CancellationToken.None));

            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DownloadRejectsNonGitHubUrl()
    {
        using var client = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Bytes([1])));
        var info = new UpdateInfo("99.0.0", null, "https://example.test/update.zip", new string('0', 64));
        var path = Path.Combine(Path.GetTempPath(), $"tokenview-update-{Guid.NewGuid():N}.zip");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                UpdateService.DownloadAsync(info, path, client, null, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
