using System.IO;
using TokenView.Wpf.Data.Usage;
using Xunit;

namespace TokenView.Tests;

public sealed class UsageStoreSnapshotTests
{
    private const string Client = "test_client";
    private const string SourcePath = "/tmp/source.jsonl";

    [Fact]
    public void ReplaceSnapshotStreamsSessionsWithoutMaterializingThem()
    {
        using var store = UsageStore.OpenInMemory();

        var summary = store.ReplaceClientSnapshot(Client, [SourceFile()], Entries(("s1", 2)));

        Assert.Equal(UpsertOutcome.Inserted, summary.Session);
        Assert.Equal(2, summary.MessagesInserted);
        Assert.True(store.SessionExists(Client, "s1"));
        Assert.Equal([SourcePath], store.SourcePaths(Client));
    }

    [Fact]
    public void ReplaceSnapshotRollsBackEverythingWhenTheIteratorFailsMidway()
    {
        using var store = UsageStore.OpenInMemory();

        static IEnumerable<SessionImport> Failing()
        {
            yield return Entry("s1", 1);
            throw new InvalidOperationException("boom");
        }

        Assert.Throws<InvalidOperationException>(() =>
            store.ReplaceClientSnapshot(Client, [SourceFile()], Failing()));

        Assert.False(store.SessionExists(Client, "s1"));
        Assert.Empty(store.SourcePaths(Client));
    }

    [Fact]
    public void ReplaceSnapshotRejectsDuplicateSessionIdsWithoutPartialWrites()
    {
        using var store = UsageStore.OpenInMemory();

        static IEnumerable<SessionImport> Duplicate()
        {
            yield return Entry("s1", 1);
            yield return Entry("s1", 1);
        }

        Assert.Throws<ArgumentException>(() =>
            store.ReplaceClientSnapshot(Client, [SourceFile()], Duplicate()));

        Assert.False(store.SessionExists(Client, "s1"));
    }

    [Fact]
    public void ReplaceSnapshotMatchesSourcesAfterNormalizingSeparators()
    {
        using var store = UsageStore.OpenInMemory();
        // 目录枚举保留根路径原样的 '/'，而 Path.Combine 会把分隔符规范成 '\'。
        var forward = Path.Combine(Path.GetTempPath(), "tokenview-mixed", "source.jsonl")
            .Replace('\\', '/');
        var backward = forward.Replace('/', '\\');
        var entry = Entry("s1", 1) with { SourcePath = backward };

        var summary = store.ReplaceClientSnapshot(Client, [SourceFile(forward)], [entry]);

        Assert.Equal(1, summary.MessagesInserted);
        Assert.True(store.SessionExists(Client, "s1"));
    }

    [Fact]
    public void MergeSnapshotKeepsSessionsOutsideTheStream()
    {
        using var store = UsageStore.OpenInMemory();
        store.ReplaceClientSnapshot(Client, [SourceFile()], Entries(("s1", 1), ("s2", 1)));

        store.MergeClientSnapshot(Client, [SourceFile()], Entries(("s1", 3)));

        Assert.True(store.SessionExists(Client, "s1"));
        Assert.True(store.SessionExists(Client, "s2"));
    }

    private static IEnumerable<SessionImport> Entries(params (string Id, int Messages)[] entries)
    {
        foreach (var (id, messages) in entries) yield return Entry(id, messages);
    }

    private static SourceFile SourceFile(string? path = null) =>
        new(Client, path ?? SourcePath, SourceFileKind.Jsonl, 100, 1_000, 100, 1, null, 0);

    private static SessionImport Entry(string sessionId, int messages)
    {
        var usage = new NormalizedUsage(new TokenBuckets(10, 5, null, 0, 0), 15,
            UsageTotalBasis.DerivedFromBuckets, [], false);
        var session = new SessionRecord
        {
            Client = Client,
            ClientSessionId = sessionId,
            ProjectPath = "/project",
            Model = "test-model",
            StartedAtMs = 1_000,
            EndedAtMs = 2_000,
            SemanticsId = "test.v1",
            TotalBasis = SessionTotalBasis.SumOfMessages,
            Coverage = MessageCoverage.Complete,
            Usage = usage,
        };
        var records = new List<MessageRecord>(messages);
        for (var index = 0; index < messages; index++)
        {
            records.Add(new MessageRecord
            {
                Client = Client,
                ClientSessionId = sessionId,
                DedupKey = $"m{index}",
                CreatedAtMs = 1_000 + index,
                Model = "test-model",
                Usage = usage,
                SemanticsId = "test.v1",
            });
        }
        return new SessionImport(session, records, SourcePath, new Dictionary<string, string>());
    }
}
