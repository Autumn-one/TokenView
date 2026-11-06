using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data;
using TokenView.Wpf.Data.Collectors;
using TokenView.Wpf.Data.Peers;
using TokenView.Wpf.Data.Usage;
using TokenView.Wpf.Services;
using Xunit;

namespace TokenView.Tests;

public sealed class AuditRegressionTests
{
    [Fact]
    public void EmptySnapshotPreservesHistoryWatermarkAndRollup()
    {
        using var fixture = new Database();
        using var store = UsageStore.Open(fixture.Path);
        store.ReplaceClientSnapshot("codex", [fixture.Source], [fixture.Entry(1_000)]);
        store.RebuildRequestRollup();
        var previous = store.SourceFile("codex", fixture.Source.Path);

        Assert.Throws<InvalidDataException>(() => store.ReplaceClientSnapshot("codex",
            [fixture.Source with { SizeBytes = 200, MtimeMs = 3_000 }], []));

        Assert.Equal(previous, store.SourceFile("codex", fixture.Source.Path));
        Assert.True(store.SessionExists("codex", "synthetic-session"));
        Assert.True(store.RequestRollupIsFresh());
        Assert.Equal(15, fixture.Scalar("SELECT SUM(total_tokens) FROM messages"));
        Assert.Equal(15, fixture.Scalar("SELECT SUM(total_tokens) FROM request_hourly"));
    }

    [Fact]
    public void InitiallyEmptySnapshotIsAllowed()
    {
        using var store = UsageStore.OpenInMemory();
        Assert.Null(store.ReplaceClientSnapshot("codex", [], []).Session);
    }

    [Theory]
    [InlineData("unknown", true)]
    [InlineData("unknown", false)]
    [InlineData("older", true)]
    [InlineData("decreased", true)]
    public void MergeRejectsStaleSessionBeforeChangingMessages(string reason, bool withMessages)
    {
        using var fixture = new Database();
        using var store = UsageStore.Open(fixture.Path);
        var old = fixture.Entry(1_000);
        old = old with { Session = old.Session with { TotalBasis = SessionTotalBasis.ClientCumulative } };
        store.ReplaceClientSnapshot("codex", [fixture.Source], [old]);
        var incoming = old with
        {
            Session = old.Session with
            {
                EndedAtMs = reason == "older" ? 500 : 3_000,
                Usage = old.Session.Usage with { TotalTokens = reason == "unknown" ? null : 10 },
            },
            Messages = withMessages ? old.Messages : [],
        };
        var previous = store.SourceFile("codex", fixture.Source.Path);

        var result = store.MergeClientSnapshot("codex",
            [fixture.Source with { SizeBytes = 200 }], [incoming]);

        Assert.Equal(1, result.SessionsSkippedStale);
        Assert.Equal(0, result.MessagesPruned);
        Assert.Equal(15, fixture.Scalar("SELECT total_tokens FROM sessions"));
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM messages"));
        Assert.Equal(previous, store.SourceFile("codex", fixture.Source.Path));
    }

    [Fact]
    public void AcceptedMergeReplacesMessagesAndAdvancesWatermark()
    {
        using var fixture = new Database();
        using var store = UsageStore.Open(fixture.Path);
        store.ReplaceClientSnapshot("codex", [fixture.Source], [fixture.Entry(1_000, 2_000)]);
        var result = store.MergeClientSnapshot("codex", [fixture.Source with { SizeBytes = 200 }],
            [fixture.Entry(3_000)]);
        Assert.Equal(2, result.MessagesPruned);
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM messages"));
        Assert.Equal(200, store.SourceFile("codex", fixture.Source.Path)!.SizeBytes);
    }

    [Theory]
    [InlineData(9, 30, 17, 30, 3)]
    [InlineData(9, 30, 9, 50, 1)]
    [InlineData(9, 30, 10, 0, 1)]
    [InlineData(9, 0, 18, 0, 5)]
    public void ExactRequestRangeClipsBothEdgesAndAllSlices(int fromHour, int fromMinute,
        int toHour, int toMinute, int expected)
    {
        using var fixture = new Database();
        static long At(int h, int m) => new DateTimeOffset(2026, 11, 1, h, m, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        using (var store = UsageStore.Open(fixture.Path))
        {
            var entry = fixture.Entry(At(9, 0), At(9, 30), At(12, 0), At(17, 0), At(17, 30));
            store.ReplaceClientSnapshot("codex", [fixture.Source], [entry]);
            store.RebuildRequestRollup();
        }
        var range = TokenViewDatabase.CreateCustomRange(At(fromHour, fromMinute), At(toHour, toMinute));
        var snapshot = TokenViewDatabase.Load(fixture.Path, range);
        Assert.Equal(expected, snapshot.TotalRequests);
        Assert.Equal(expected * 15, snapshot.TotalTokens);
        Assert.Equal(1, snapshot.Sessions);
        Assert.Equal(expected, Assert.Single(snapshot.Clients).Requests);
        Assert.Equal(expected * 15, Assert.Single(snapshot.Clients[0].Models).Tokens);
        Assert.Equal(expected, snapshot.ChartPoints.Sum(point => point.Requests));
        var all = TokenViewDatabase.Load(fixture.Path, range, DeviceSelection.All);
        Assert.Equal(snapshot.TotalTokens, all.TotalTokens);
    }

    [Theory]
    [InlineData(-480)]
    [InlineData(330)]
    [InlineData(840)]
    public void PeerCustomRangeIncludesBoundaryDaysUsingPeerCalendarLabels(int offset)
    {
        using var fixture = new Database();
        using (var store = UsageStore.Open(fixture.Path)) { }
        var peer = new PeerSnapshotDocument
        {
            DeviceId = Guid.NewGuid().ToString("N"), DeviceName = "Synthetic peer", AppVersion = "test",
            GeneratedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), UtcOffsetMinutes = offset,
        };
        for (var day = 1; day <= 4; day++)
        {
            var at = new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.FromMinutes(offset)).ToUnixTimeMilliseconds();
            peer.RequestDaily.Add(new PeerRequestRow
            {
                TimeMs = at, Client = "codex", Model = "synthetic", Tokens = 15, Requests = 1, RequestsWithUsage = 1,
            });
            peer.SessionDaily.Add(new PeerSessionRow { TimeMs = at, Client = "codex", Sessions = 1 });
        }
        PeerSnapshotStore.Import(fixture.Path, peer);
        static long Local(int day, int h) => new DateTimeOffset(new DateTime(2026, 11, day, h, 0, 0)).ToUnixTimeMilliseconds();
        var snapshot = TokenViewDatabase.Load(fixture.Path,
            TokenViewDatabase.CreateCustomRange(Local(1, 10), Local(3, 12)), DeviceSelection.All);
        Assert.Equal(45, snapshot.TotalTokens);
        Assert.Equal(3, snapshot.Sessions);
        Assert.True(snapshot.PeerGranularityDaily);
        Assert.Equal(3, snapshot.ChartPoints.Sum(point => point.Requests));
        Assert.Equal(1, snapshot.ChartPoints[0].Requests);
        var midnightEnd = TokenViewDatabase.Load(fixture.Path,
            TokenViewDatabase.CreateCustomRange(Local(1, 10), Local(3, 0)), DeviceSelection.All);
        Assert.Equal(30, midnightEnd.TotalTokens);
        var all = TokenViewDatabase.Load(fixture.Path,
            new RangeSelection("all", "全部", 0, Local(5, 0), false), DeviceSelection.All);
        Assert.Equal(60, all.TotalTokens);
        Assert.Equal(4, all.ChartPoints.Sum(point => point.Requests));
    }

    [Fact]
    public void ExactEdgesPreserveEstimatedAndUnknownUsageSeparation()
    {
        using var fixture = new Database();
        const long from = 1_800_000;
        using (var store = UsageStore.Open(fixture.Path))
        {
            var entry = fixture.Entry(from, from + 1, from + 2, 3_600_000);
            entry.Messages[1] = entry.Messages[1] with { SourceKind = SourceKind.Estimated };
            entry.Messages[2] = entry.Messages[2] with
            {
                Usage = entry.Messages[2].Usage with { TotalTokens = null, TotalBasis = UsageTotalBasis.Unavailable },
            };
            store.ReplaceClientSnapshot("codex", [fixture.Source], [entry]);
            store.RebuildRequestRollup();
        }
        var result = TokenViewDatabase.Load(fixture.Path, TokenViewDatabase.CreateCustomRange(from, 3_600_000));
        Assert.Equal(3, result.TotalRequests);
        Assert.Equal(15, result.TotalTokens);
        Assert.Equal(1, result.RequestsWithUsage);
        Assert.Equal(2, result.RequestsWithoutUsage);
        Assert.Equal(15, result.EstimatedTokens);
        Assert.Equal(1, result.EstimatedRequests);
    }

    [Fact]
    public void IncludedCacheAndReasoningCanBeUnknownWithoutLosingKnownTotal()
    {
        var usage = UsageNormalizer.Normalize(TokenSemanticsCatalog.CodeArts, new RawTokenCounts(Input: 100, Output: 20));
        Assert.Equal(120, usage.TotalTokens);
        Assert.Null(usage.Buckets.CacheRead);
        Assert.Null(usage.Buckets.Reasoning);
        Assert.Contains(TokenSemanticsCatalog.UnknownCacheReadMissing, usage.UnknownRelations);
        Assert.Contains(TokenSemanticsCatalog.UnknownReasoningMissing, usage.UnknownRelations);
        Assert.Equal(UsageTotalBasis.DerivedFromBuckets, usage.TotalBasis);
        Assert.Equal(120, UsageNormalizer.Normalize(TokenSemanticsCatalog.QoderIde,
            new RawTokenCounts(Input: 100, Output: 20)).TotalTokens);
    }

    [Fact]
    public void MissingSeparateCacheRemainsUnknownUnlessClientReportsTotal()
    {
        var raw = new RawTokenCounts(Input: 100, Output: 20, CacheWrite: 0);
        Assert.Null(UsageNormalizer.Normalize(TokenSemanticsCatalog.KimiCode, raw).TotalTokens);
        Assert.Equal(150, UsageNormalizer.Normalize(TokenSemanticsCatalog.KimiCode,
            raw with { ClientTotal = 150 }).TotalTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodexNewWindowIgnoresMismatchedMirrorsAndKeepsLegacyPrefix(bool ordinal)
    {
        using var fixture = new Database();
        var lines = new List<string> { """{"type":"session_meta","payload":{"id":"synthetic-session"}}""" };
        object Counts(long total) => new { input_tokens = total - 5, output_tokens = 5, cached_input_tokens = 0, reasoning_output_tokens = 0, total_tokens = total };
        void Legacy(long total, long cumulative) => lines.Add(JsonSerializer.Serialize(new
        {
            type = "event_msg", ordinal = ordinal ? (int?)lines.Count : null, timestamp = "2026-11-01T01:00:00Z",
            payload = new { type = "token_count", info = new { last_token_usage = Counts(total), total_token_usage = Counts(cumulative) } },
        }));
        void New(string id, long total, long cumulative) => lines.Add(JsonSerializer.Serialize(new
        {
            type = "token_usage_record", ordinal = ordinal ? (int?)lines.Count : null, timestamp = "2026-11-01T01:00:00Z",
            payload = new { thread_id = "synthetic-session", response_id = id, usage = Counts(total), thread_token_usage = Counts(cumulative) },
        }));
        Legacy(15, 15);
        New("request-1", 20, 35); Legacy(20, 35);
        lines.Add("""{"type":"compacted","payload":{}}""");
        New("request-2", 100, 135); Legacy(5, 40); Legacy(5, 40);
        New("request-3", 30, 165); Legacy(30, 70);
        New("request-4", 25, 25); Legacy(25, 25);
        New("request-4", 25, 25);
        File.WriteAllLines(System.IO.Path.Combine(fixture.Folder, "rollout-synthetic.jsonl"), lines);
        using var store = UsageStore.Open(fixture.Path);
        var collector = new CodexCollector(fixture.Folder);
        collector.Sync(store, new SyncOptions());
        collector.Sync(store, new SyncOptions(true));
        Assert.Equal(5, fixture.Scalar("SELECT COUNT(*) FROM messages"));
        Assert.Equal(190, fixture.Scalar("SELECT SUM(total_tokens) FROM messages"));
    }

    [Fact]
    public void DiagnosticCopyNeverIncludesErrorTextOrArbitraryIdentifiers()
    {
        var secret = @"C:\Users\private-user\project\session-secret.json";
        var text = CollectionDiagnostics.Build("codex", true, false, 2, 1, null,
            new ClientSyncResult("codex", new SyncReport { FilesFailed = 1 }, secret, false));
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("private-user", text);
        Assert.DoesNotContain("session-secret", text);
        Assert.Contains("失败 1", text);
        Assert.Contains("同步失败", text);
    }

    [Fact]
    public async Task ClearPersistsPauseBeforeDeletingAndIncludesPeerTables()
    {
        using var fixture = new Database();
        using (var store = UsageStore.Open(fixture.Path))
        {
            store.ReplaceClientSnapshot("codex", [fixture.Source],
                [fixture.Entry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);
            store.RebuildRequestRollup();
        }
        var peer = PeerSnapshotBuilder.Build(fixture.Path, Guid.NewGuid().ToString("N"), "Synthetic peer", "test");
        PeerSnapshotStore.Import(fixture.Path, peer);
        var service = new SyncService();
        var saved = false;
        await service.ClearStatisticsAsync(fixture.Path, () =>
        {
            Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM sessions"));
            Assert.False(service.TryStart([], false));
            saved = true;
        }, new LanSyncService());
        Assert.True(saved);
        foreach (var table in new[] { "sessions", "messages", "source_files", "request_hourly", "request_hourly_sessions",
                     "peer_devices", "peer_request_daily", "peer_request_hourly", "peer_session_daily", "peer_session_hourly" })
            Assert.Equal(0, fixture.Scalar($"SELECT COUNT(*) FROM {table}"));
    }

    [Fact]
    public async Task ClearDoesNotDeleteWhenPauseCannotBeSaved()
    {
        using var fixture = new Database();
        using (var store = UsageStore.Open(fixture.Path))
            store.ReplaceClientSnapshot("codex", [fixture.Source], [fixture.Entry(1_000)]);
        await Assert.ThrowsAsync<IOException>(() => new SyncService().ClearStatisticsAsync(fixture.Path,
            () => throw new IOException("synthetic failure"), new LanSyncService()));
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM sessions"));
    }

    private sealed class Database : IDisposable
    {
        public string Folder { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tokenview-audit-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Folder, "usage.sqlite3");
        public SourceFile Source => new("codex", System.IO.Path.Combine(Folder, "synthetic.jsonl"),
            SourceFileKind.Jsonl, 100, 1_000, 100, 1, null, 0);
        public Database() => Directory.CreateDirectory(Folder);
        public SessionImport Entry(params long[] times)
        {
            var usage = new NormalizedUsage(new TokenBuckets(10, 5, 0, 0, 0), 15,
                UsageTotalBasis.DerivedFromBuckets, [], false);
            var session = new SessionRecord
            {
                Client = "codex", ClientSessionId = "synthetic-session", Model = "synthetic-model",
                StartedAtMs = times.Min(), EndedAtMs = times.Max(), SemanticsId = "synthetic.v1",
                TotalBasis = SessionTotalBasis.SumOfMessages, Coverage = MessageCoverage.Complete,
                Usage = usage with { TotalTokens = 15 * times.Length },
            };
            return SessionImport.Create(session, times.Select((time, index) => new MessageRecord
            {
                Client = "codex", ClientSessionId = session.ClientSessionId, DedupKey = $"request-{index}",
                CreatedAtMs = time, Model = session.Model, Usage = usage, SemanticsId = session.SemanticsId,
            }).ToList(), Source.Path);
        }
        public long Scalar(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        public void Dispose() => Directory.Delete(Folder, true);
    }
}
