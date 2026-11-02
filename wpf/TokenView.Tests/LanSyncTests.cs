using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TokenView.Wpf.Data;
using TokenView.Wpf.Data.Peers;
using TokenView.Wpf.Data.Usage;
using TokenView.Wpf.Services;
using Xunit;

namespace TokenView.Tests;

public sealed class LanSyncTests
{
    [Fact]
    public void SnapshotRoundTripKeepsAggregatesAndOmitsSensitiveFields()
    {
        using var source = new TempDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        source.Seed("codex", "secret-session-id", now, 1_234);
        var deviceId = Guid.NewGuid().ToString("N");

        var document = PeerSnapshotBuilder.Build(source.Path, deviceId, "PC-A", "test");
        using var memory = new MemoryStream();
        PeerSnapshotSerializer.Write(memory, document);
        var bytes = memory.ToArray();

        using (var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip, Encoding.UTF8))
        {
            var json = reader.ReadToEnd();
            Assert.DoesNotContain("secret-session-id", json);
            Assert.DoesNotContain("/project", json);
            Assert.Contains("\"deviceName\":\"PC-A\"", json);
        }

        using var readBack = new MemoryStream(bytes);
        var parsed = PeerSnapshotSerializer.Read(readBack);
        Assert.Equal(deviceId, parsed.DeviceId);
        Assert.Equal("PC-A", parsed.DeviceName);
        var daily = Assert.Single(parsed.RequestDaily);
        Assert.Equal("codex", daily.Client);
        Assert.Equal(1_234, daily.Tokens);
        Assert.Equal(1, daily.Requests);
        Assert.Single(parsed.SessionDaily);
    }

    [Fact]
    public void ImportedPeerAppearsAsPseudoClientAtTheSameLevel()
    {
        using var local = new TempDatabase();
        using var peer = new TempDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        local.Seed("codex", "local-session", now, 1_000);
        peer.Seed("claude_code", "peer-session", now, 2_000);
        var peerId = Guid.NewGuid().ToString("N");

        var document = PeerSnapshotBuilder.Build(peer.Path, peerId, "PC-B", "test");
        var result = PeerSnapshotStore.Import(local.Path, document);

        Assert.True(result.Imported);
        var snapshot = TokenViewDatabase.Load(local.Path, TokenViewDatabase.CreateRange("7d"),
            DeviceSelection.All);
        Assert.Equal(3_000, snapshot.TotalTokens);
        Assert.Equal(2, snapshot.Sessions);
        Assert.True(snapshot.SessionsStartBased);
        Assert.True(snapshot.DetailsLocalOnly);
        Assert.Contains(snapshot.Clients, row =>
            row.Client.Key == TokenViewDatabase.PeerClientPrefix + peerId &&
            row.Client.Label == "PC-B" &&
            row.IconKey == "device" &&
            row.Tokens == 2_000);
        Assert.DoesNotContain(snapshot.Clients, row => row.Client.Label == "claude_code");
    }

    [Fact]
    public void StaleSnapshotIsSkippedUnlessForced()
    {
        using var local = new TempDatabase();
        using var peer = new TempDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        local.Seed("codex", "local-session", now, 1_000);
        peer.Seed("codex", "peer-session", now, 500);
        var peerId = Guid.NewGuid().ToString("N");
        var document = PeerSnapshotBuilder.Build(peer.Path, peerId, "PC-B", "test");

        Assert.True(PeerSnapshotStore.Import(local.Path, document).Imported);
        var stale = PeerSnapshotBuilder.Build(peer.Path, peerId, "PC-B", "test");
        stale.GeneratedAtMs = document.GeneratedAtMs - 1_000;
        var skipped = PeerSnapshotStore.Import(local.Path, stale);
        Assert.True(skipped.StaleSkipped);
        Assert.False(skipped.Imported);

        var forced = PeerSnapshotStore.Import(local.Path, stale, force: true);
        Assert.True(forced.Imported);
    }

    [Fact]
    public async Task TwoServicesDiscoverAndExchangeOverLoopback()
    {
        using var first = new TempDatabase();
        using var second = new TempDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        first.Seed("codex", "first-session", now, 1_000);
        second.Seed("claude_code", "second-session", now, 2_000);
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        var firstUdp = FreeUdpPort();
        var secondUdp = FreeUdpPort();
        var serviceA = new LanSyncService();
        var serviceB = new LanSyncService();
        try
        {
            serviceA.Start(new LanSyncOptions(first.Path, firstId, "PC-A", "test", true,
                firstUdp, 0, IPAddress.Loopback,
                [new IPEndPoint(IPAddress.Loopback, secondUdp)], null,
                TimeSpan.FromMilliseconds(150)));
            serviceB.Start(new LanSyncOptions(second.Path, secondId, "PC-B", "test", true,
                secondUdp, 0, IPAddress.Loopback,
                [new IPEndPoint(IPAddress.Loopback, firstUdp)], null,
                TimeSpan.FromMilliseconds(150)));

            await WaitUntilAsync(() =>
                PeerSnapshotStore.ListDevices(first.Path).Any(device => device.DeviceId == secondId));
            await WaitUntilAsync(() =>
                PeerSnapshotStore.ListDevices(second.Path).Any(device => device.DeviceId == firstId));

            var snapshot = TokenViewDatabase.Load(first.Path,
                TokenViewDatabase.CreateRange("7d"), DeviceSelection.All);
            Assert.Equal(3_000, snapshot.TotalTokens);
            Assert.Contains(snapshot.Clients, row =>
                row.Client.Key == TokenViewDatabase.PeerClientPrefix + secondId &&
                row.Tokens == 2_000);
        }
        finally
        {
            serviceA.Stop();
            serviceB.Stop();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(12))
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.True(condition(), "局域网回环同步在 12 秒内未完成");
    }

    private static int FreeUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "tokenview-lan-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        }

        public string Path { get; }

        public void Seed(string client, string sessionId, long nowMs, long tokens)
        {
            using var store = UsageStore.Open(Path);
            var usage = new NormalizedUsage(new TokenBuckets(tokens, 0, null, 0, 0), tokens,
                UsageTotalBasis.DerivedFromBuckets, [], false);
            var session = new SessionRecord
            {
                Client = client,
                ClientSessionId = sessionId,
                ProjectPath = "/project/secret-path",
                Model = "test-model",
                StartedAtMs = nowMs - 1_000,
                EndedAtMs = nowMs,
                SemanticsId = "test.v1",
                TotalBasis = SessionTotalBasis.SumOfMessages,
                Coverage = MessageCoverage.Complete,
                Usage = usage,
            };
            var message = new MessageRecord
            {
                Client = client,
                ClientSessionId = sessionId,
                DedupKey = "m1",
                CreatedAtMs = nowMs - 500,
                Model = "test-model",
                Usage = usage,
                SemanticsId = "test.v1",
            };
            store.ImportSession(new SourceFile(client, Path + "." + client + ".source",
                SourceFileKind.Jsonl, 100, nowMs, 100, 1, nowMs, 0), session, [message]);
            store.RebuildRequestRollup();
        }

        public void Dispose()
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                try
                {
                    File.Delete(Path + suffix);
                }
                catch (IOException)
                {
                    // 测试清理失败不覆盖断言结果。
                }
            }
        }
    }
}
