using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Collectors;
using TokenView.Wpf.Data.Usage;
using Xunit;

namespace TokenView.Tests;

public sealed class KimiCodeCollectorTests
{
    [Fact]
    public void ImportsCompleteUsageRecords()
    {
        using var temp = new KimiSessions();
        temp.Session("ws", "session_0001",
            Metadata(),
            Usage("kimi-code/k3.1-pro", input: 100, output: 10),
            Usage("kimi-code/k3.1-pro", input: 200, output: 20, cacheRead: 5, cacheWrite: 7));

        SyncReport report;
        using (var store = UsageStore.Open(temp.Database))
        {
            report = new KimiCodeCollector(temp.Root).Sync(store, new SyncOptions());
            store.Checkpoint();
        }

        Assert.Equal(1, report.SessionsImported);
        Assert.Equal(2, report.MessagesInserted);
        Assert.Equal(0, report.UnparsedLines);
        Assert.Equal(0, report.IgnoredLines);

        var session = temp.Session("session_0001");
        Assert.Equal("kimi-code/k3.1-pro", session.Model);
        Assert.Equal(342, session.Total);
        Assert.Equal("complete", session.Coverage);
        Assert.Equal(2, temp.MessageCount("session_0001"));
    }

    [Fact]
    public void ToleratesIncompleteTrailingLine()
    {
        using var temp = new KimiSessions();
        temp.Session("ws", "session_0001", false,
            Metadata(),
            Usage("kimi-code/k3.1-pro", input: 100, output: 10),
            """{"type":"usage.record","model":"kimi-code/k3.1-pro","uso""");

        SyncReport report;
        using (var store = UsageStore.Open(temp.Database))
        {
            report = new KimiCodeCollector(temp.Root).Sync(store, new SyncOptions());
            store.Checkpoint();
        }

        Assert.Equal(1, report.SessionsImported);
        Assert.Equal(1, report.MessagesInserted);
        Assert.Equal(1, report.UnparsedLines);

        var session = temp.Session("session_0001");
        Assert.Equal(110, session.Total);
        Assert.Equal("partial", session.Coverage);
        Assert.Equal(1, temp.MessageCount("session_0001"));
    }

    [Fact]
    public void ToleratesNewSessionWithEmptyWire()
    {
        using var temp = new KimiSessions();
        temp.Session("ws", "session_0001", trailingNewline: false);
        temp.Session("ws", "session_0002",
            Metadata(),
            Usage("kimi-code/k3.1-pro", input: 100, output: 10));

        SyncReport report;
        using (var store = UsageStore.Open(temp.Database))
        {
            report = new KimiCodeCollector(temp.Root).Sync(store, new SyncOptions());
            store.Checkpoint();
        }

        Assert.Equal(2, report.SessionsImported);
        Assert.Equal(1, report.MessagesInserted);

        var empty = temp.Session("session_0001");
        Assert.Null(empty.Total);
        Assert.Equal("partial", empty.Coverage);
        Assert.Equal("complete", temp.Session("session_0002").Coverage);
    }

    [Fact]
    public void RejectsUnsupportedProtocolWithoutWriting()
    {
        using var temp = new KimiSessions();
        temp.Session("ws", "session_0001",
            Metadata("1.6"),
            Usage("kimi-code/k3.1-pro", input: 100, output: 10));

        using var store = UsageStore.Open(temp.Database);
        var error = Assert.Throws<SyncException>(() =>
            new KimiCodeCollector(temp.Root).Sync(store, new SyncOptions()));

        Assert.False(error.Retryable);
        Assert.False(store.SessionExists("kimi_code", "session_0001"));
    }

    private static string Metadata(string protocol = "1.5") =>
        JsonSerializer.Serialize(new { type = "metadata", protocol_version = protocol });

    private static string Usage(string model, long input, long output, long cacheRead = 0,
        long cacheWrite = 0, long time = 1792815196499) =>
        JsonSerializer.Serialize(new
        {
            type = "usage.record",
            model,
            usageScope = "turn",
            time,
            usage = new
            {
                inputOther = input,
                output,
                inputCacheRead = cacheRead,
                inputCacheCreation = cacheWrite,
            },
        });

    private sealed class KimiSessions : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(),
            $"tokenview-kimi-{Guid.NewGuid():N}");

        public KimiSessions()
        {
            Directory.CreateDirectory(_base);
        }

        public string Root => Path.Combine(_base, "sessions");

        public string Database => Path.Combine(_base, "usage.sqlite3");

        public void Session(string workspace, string id, params string[] lines) =>
            Session(workspace, id, true, lines);

        public void Session(string workspace, string id, bool trailingNewline, params string[] lines)
        {
            var directory = Path.Combine(Root, workspace, id);
            Directory.CreateDirectory(Path.Combine(directory, "agents", "main"));
            File.WriteAllText(Path.Combine(directory, "state.json"),
                JsonSerializer.Serialize(new
                {
                    id,
                    version = 2,
                    createdAt = 1792815190650,
                    updatedAt = 1792816893799,
                    cwd = "C:/projects/demo",
                }));
            var content = string.Join("\n", lines) + (trailingNewline && lines.Length > 0 ? "\n" : "");
            File.WriteAllText(Path.Combine(directory, "agents", "main", "wire.jsonl"), content);
        }

        public (string? Model, long? Total, string Coverage) Session(string sessionId)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT model, total_tokens, message_coverage FROM sessions
                WHERE client = 'kimi_code' AND client_session_id = $id
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read(), $"session {sessionId} not imported");
            return (reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2));
        }

        public long MessageCount(string sessionId)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM messages
                WHERE client = 'kimi_code' AND session_id = (
                    SELECT id FROM sessions WHERE client = 'kimi_code' AND client_session_id = $id)
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            return (long)command.ExecuteScalar()!;
        }

        private SqliteConnection Connect()
        {
            var connection = new SqliteConnection($"Data Source={Database}");
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_base, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响断言结果。
            }
        }
    }
}
