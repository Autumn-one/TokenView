using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Collectors;
using TokenView.Wpf.Data.Usage;
using Xunit;

namespace TokenView.Tests;

public sealed class QoderCollectorTests
{
    [Fact]
    public void AllZeroUsageBecomesUnknownButKeepsRequest()
    {
        using var temp = new QoderSessions();
        temp.Session("fixture",
            Assistant("m1", 1792500000000, new
            {
                input_tokens = 100,
                output_tokens = 20,
                cache_read_input_tokens = 5,
                cache_creation_input_tokens = 2,
            }),
            Assistant("m2", 1792500060000, new
            {
                input_tokens = 0,
                output_tokens = 0,
                cache_read_input_tokens = 0,
                cache_creation_input_tokens = 0,
            }),
            Assistant("m3", 1792500120000, new { input_tokens = 7 }),
            Assistant("m4", 1792500180000, includeUsage: false));

        SyncReport report;
        (long Requests, long KnownRequests, long UnknownRequests, long Tokens)? rollup;
        long parserVersion;
        using (var store = UsageStore.Open(temp.Database))
        {
            var collector = new QoderCollector([temp.Root], []);
            report = collector.Sync(store, new SyncOptions());
            store.RebuildRequestRollup();
            rollup = temp.Rollup();
            parserVersion = temp.ParserVersion();
            store.Checkpoint();
        }

        Assert.Equal(1, report.SessionsImported);
        Assert.Equal(3, report.MessagesInserted);
        Assert.Equal(0, report.UnparsedLines);
        Assert.Equal(3, temp.MessageCount());
        Assert.Equal(4, parserVersion);

        var totals = temp.MessageTotals();
        Assert.Equal(120, totals["message:m1"]);
        Assert.Null(totals["message:m2"]);
        Assert.Null(totals["message:m3"]);

        var (total, coverage) = temp.Session();
        Assert.Null(total);
        Assert.Equal("complete", coverage);

        Assert.Equal(3, rollup?.Requests);
        Assert.Equal(1, rollup?.KnownRequests);
        Assert.Equal(2, rollup?.UnknownRequests);
        Assert.Equal(120, rollup?.Tokens);
    }

    [Fact]
    public void PlaceholderUsageWithContextRatioBecomesEstimatedButNotMeasured()
    {
        using var temp = new QoderSessions();
        temp.Session("fixture",
            Assistant("e1", 1792500000000, new
            {
                input_tokens = 0,
                output_tokens = 0,
                cache_read_input_tokens = 0,
                cache_creation_input_tokens = 0,
                context_usage_ratio = 0.25,
            }, content: new object[]
            {
                new { type = "text", text = "12345678" },
            }));

        SyncReport report;
        (long Requests, long KnownRequests, long UnknownRequests, long Tokens)? rollup;
        using (var store = UsageStore.Open(temp.Database))
        {
            var collector = new QoderCollector([temp.Root], []);
            report = collector.Sync(store, new SyncOptions());
            store.RebuildRequestRollup();
            rollup = temp.Rollup();
            store.Checkpoint();
        }

        Assert.Equal(1, report.MessagesInserted);
        var totals = temp.MessageTotals();
        Assert.Equal(50_002, totals["message:e1"]);

        var (total, coverage, sourceKind) = temp.SessionDetail();
        Assert.Equal(50_002, total);
        Assert.Equal("complete", coverage);
        Assert.Equal("estimated", sourceKind);
        Assert.Equal("estimated", temp.MessageSourceKind("message:e1"));

        Assert.Equal(1, rollup?.Requests);
        Assert.Equal(0, rollup?.KnownRequests);
        Assert.Equal(1, rollup?.UnknownRequests);
        Assert.Equal(0, rollup?.Tokens);
    }

    [Fact]
    public void NativeUsageSubtractsCachesAndDeduplicatesRequestAcrossMessageBlocks()
    {
        using var temp = new QoderSessions();
        temp.Session("fixture",
            Assistant("block1", 1792500000000, new
            {
                request_id = "request-fixture", input_tokens = 100, output_tokens = 20,
                cache_read_input_tokens = 80, cache_creation_input_tokens = 10,
                context_usage_ratio = 0.25,
            }),
            Assistant("block2", 1792500001000, new
            {
                request_id = "request-fixture", input_tokens = 100, output_tokens = 25,
                cache_read_input_tokens = 80, cache_creation_input_tokens = 10,
            }),
            Assistant("placeholder", 1792500002000, new
            {
                request_id = "request-fixture", input_tokens = 0, output_tokens = 0,
                cache_read_input_tokens = 0, cache_creation_input_tokens = 0,
                context_usage_ratio = 0.25,
            }));

        using var store = UsageStore.Open(temp.Database);
        var collector = new QoderCollector([temp.Root], []);
        var report = collector.Sync(store, new SyncOptions());
        store.RebuildRequestRollup();

        Assert.Equal(1, report.MessagesInserted);
        Assert.Equal(125, temp.MessageTotals()["request:request-fixture"]);
        Assert.Equal((10L, 25L, 80L, 10L), temp.Buckets("request:request-fixture"));
        Assert.Equal("measured", temp.MessageSourceKind("request:request-fixture"));
        Assert.Equal(125, temp.Session().Total);
        Assert.Equal(1, temp.Rollup().Requests);
        Assert.Equal(125, temp.Rollup().Tokens);
        Assert.Equal(1, collector.Sync(store, new SyncOptions()).FilesSkippedUnchanged);
        Assert.Equal(1, temp.MessageCount());
    }

    [Fact]
    public void EstimatesInExistingSessionNeverEnterMeasuredSessionTotal()
    {
        using var temp = new QoderSessions();
        temp.Session("fixture",
            Assistant("old", 1792500000000, new
            {
                input_tokens = 0, output_tokens = 0,
                cache_read_input_tokens = 0, cache_creation_input_tokens = 0,
                context_usage_ratio = 0.25,
            }),
            Assistant("new", 1792500001000, new
            {
                input_tokens = 100, output_tokens = 20,
                cache_read_input_tokens = 80, cache_creation_input_tokens = 0,
            }));

        using var store = UsageStore.Open(temp.Database);
        new QoderCollector([temp.Root], []).Sync(store, new SyncOptions());
        store.RebuildRequestRollup();

        Assert.Null(temp.Session().Total);
        Assert.Equal("measured", temp.SessionDetail().SourceKind);
        Assert.Equal("estimated", temp.MessageSourceKind("message:old"));
        Assert.Equal(120, temp.MessageTotals()["message:new"]);
        Assert.Equal((2L, 1L, 1L, 120L), temp.Rollup());
    }

    [Fact]
    public void EnablingRecordingReplacesPlaceholderOnRescanWithoutDuplicateRequests()
    {
        using var temp = new QoderSessions();
        temp.Session("fixture", Assistant("same", 1792500000000, new
        {
            request_id = "fixture-id", input_tokens = 0, output_tokens = 0,
            cache_read_input_tokens = 0, cache_creation_input_tokens = 0,
            context_usage_ratio = 0.25,
        }));
        using var store = UsageStore.Open(temp.Database);
        var collector = new QoderCollector([temp.Root], []);
        collector.Sync(store, new SyncOptions());
        temp.Session("fixture", Assistant("same", 1792500000000, new
        {
            request_id = "fixture-id", input_tokens = 240, output_tokens = 30,
            cache_read_input_tokens = 200, cache_creation_input_tokens = 0,
        }));
        collector.Sync(store, new SyncOptions(true));
        store.RebuildRequestRollup();

        Assert.Equal(1, temp.MessageCount());
        Assert.Equal(270, temp.Session().Total);
        Assert.Equal((40L, 30L, 200L, 0L), temp.Buckets("request:fixture-id"));
        Assert.Equal((1L, 1L, 0L, 270L), temp.Rollup());
    }

    [Fact]
    public void CacheCountsExceedingInputRemainUnknownInsteadOfInflatingTotal()
    {
        using var temp = new QoderSessions();
        temp.Session("fixture", Assistant("invalid", 1792500000000, new
        {
            input_tokens = 10, output_tokens = 20,
            cache_read_input_tokens = 80, cache_creation_input_tokens = 0,
        }));
        using var store = UsageStore.Open(temp.Database);
        new QoderCollector([temp.Root], []).Sync(store, new SyncOptions());

        Assert.Null(temp.MessageTotals()["message:invalid"]);
        Assert.Null(temp.Session().Total);
    }

    [Fact]
    public void RootRequestIdDeduplicatesSdkMessagesAndParserUpgradeReimports()
    {
        using var temp = new QoderSessions();
        var usage = new
        {
            input_tokens = 200, output_tokens = 20,
            cache_read_input_tokens = 180, cache_creation_input_tokens = 0,
        };
        temp.Session("fixture",
            Assistant("sdk-block1", 1792500000000, usage, requestId: "sdk-request"),
            Assistant("sdk-block2", 1792500001000, usage, requestId: "sdk-request"));
        using var store = UsageStore.Open(temp.Database);
        var collector = new QoderCollector([temp.Root], []);
        collector.Sync(store, new SyncOptions());
        temp.DowngradeParserVersion();

        var report = collector.Sync(store, new SyncOptions());
        store.RebuildRequestRollup();

        Assert.Equal(1, report.FilesParsed);
        Assert.Equal(4, temp.ParserVersion());
        Assert.Equal(1, temp.MessageCount());
        Assert.Equal(220, temp.MessageTotals()["request:sdk-request"]);
        Assert.Equal((1L, 1L, 0L, 220L), temp.Rollup());
    }

    private static string Assistant(string id, long timestampMs, object? usage = null,
        bool includeUsage = true, object? content = null, string? requestId = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["model"] = "qoder-test-model",
        };
        if (includeUsage)
        {
            message["usage"] = usage ?? new { };
        }
        if (content is not null)
        {
            message["content"] = content;
        }
        var entry = new Dictionary<string, object?>
        {
            ["type"] = "assistant",
            ["sessionId"] = "fixture",
            ["timestamp"] = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["uuid"] = "u-" + id,
            ["isSidechain"] = false,
            ["cwd"] = "C:/fixture",
            ["message"] = message,
        };
        if (requestId is not null)
        {
            entry["request_id"] = requestId;
        }
        return JsonSerializer.Serialize(entry);
    }

    private sealed class QoderSessions : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(),
            $"tokenview-qoder-{Guid.NewGuid():N}");

        public QoderSessions()
        {
            Directory.CreateDirectory(_base);
        }

        public string Root => Path.Combine(_base, "projects");

        public string Database => Path.Combine(_base, "usage.sqlite3");

        public void Session(string project, params string[] lines)
        {
            var directory = Path.Combine(Root, project);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "fixture.jsonl"),
                string.Join("\n", lines) + "\n");
        }

        public (long? Total, string Coverage) Session()
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT total_tokens, message_coverage FROM sessions
                WHERE client = 'qoder' AND client_session_id = 'fixture'
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read(), "fixture session not imported");
            return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.GetString(1));
        }

        public (long? Total, string Coverage, string SourceKind) SessionDetail()
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT total_tokens, message_coverage, source_kind FROM sessions
                WHERE client = 'qoder' AND client_session_id = 'fixture'
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read(), "fixture session not imported");
            return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2));
        }

        public string MessageSourceKind(string dedupKey)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT source_kind FROM messages
                WHERE client = 'qoder' AND dedup_key = $key
                """;
            command.Parameters.AddWithValue("$key", dedupKey);
            return (string)command.ExecuteScalar()!;
        }

        public long MessageCount()
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM messages m JOIN sessions s ON s.id = m.session_id
                WHERE s.client = 'qoder' AND s.client_session_id = 'fixture'
                """;
            return (long)command.ExecuteScalar()!;
        }

        public Dictionary<string, long?> MessageTotals()
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.dedup_key, m.total_tokens FROM messages m JOIN sessions s ON s.id = m.session_id
                WHERE s.client = 'qoder' AND s.client_session_id = 'fixture'
                """;
            var result = new Dictionary<string, long?>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
            return result;
        }

        public (long Input, long Output, long Read, long Write) Buckets(string key)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT input_tokens, output_tokens, cache_read_tokens, cache_write_tokens
                FROM messages WHERE client = 'qoder' AND dedup_key = $key
                """;
            command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }

        public long ParserVersion()
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT parser_version FROM source_files WHERE client = 'qoder'";
            return (long)command.ExecuteScalar()!;
        }

        public void DowngradeParserVersion()
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE source_files SET parser_version = 3 WHERE client = 'qoder'";
            command.ExecuteNonQuery();
        }

        public (long Requests, long KnownRequests, long UnknownRequests, long Tokens) Rollup()
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(SUM(requests), 0), COALESCE(SUM(requests_with_usage), 0),
                       COALESCE(SUM(requests_without_usage), 0), COALESCE(SUM(total_tokens), 0)
                FROM request_hourly WHERE client = 'qoder'
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
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
