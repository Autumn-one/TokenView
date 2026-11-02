using System.IO;
using Microsoft.Data.Sqlite;

namespace TokenView.Wpf.Data.Peers;

/// <summary>只读本机表生成本机聚合快照；绝不读取 peer_* 导入缓存。</summary>
public static class PeerSnapshotBuilder
{
    private const long HourMs = 3_600_000;
    private const long HourlyWindowMs = 48 * HourMs;

    public static PeerSnapshotDocument Build(
        string databasePath,
        string deviceId,
        string deviceName,
        string appVersion,
        TimeProvider? timeProvider = null)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("未找到 TokenView 用量数据库。", databasePath);
        }

        var clock = timeProvider ?? TimeProvider.System;
        var now = clock.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var windowStart = nowMs - HourlyWindowMs;
        var offsetMinutes = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes);

        using var connection = SqliteReadConnection.Open(databasePath);
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN;";
        begin.ExecuteNonQuery();
        try
        {
            var requestDaily = ReadRequestDaily(connection);
            var requestHourly = ReadRequestHourly(connection, windowStart, nowMs);
            var estimated = ReadEstimated(connection, windowStart, nowMs);
            foreach (var row in estimated.Daily)
            {
                AddRequest(requestDaily, row.TimeMs, row.Client, row.Model,
                    estimatedTokens: row.EstimatedTokens, estimatedRequests: row.EstimatedRequests);
            }
            foreach (var row in estimated.Hourly)
            {
                AddRequest(requestHourly, row.TimeMs, row.Client, row.Model,
                    estimatedTokens: row.EstimatedTokens, estimatedRequests: row.EstimatedRequests);
            }

            var sessionDaily = ReadSessionDaily(connection);
            var sessionHourly = ReadSessionHourly(connection, windowStart, nowMs);

            var document = new PeerSnapshotDocument
            {
                SchemaVersion = PeerSnapshotDocument.CurrentSchemaVersion,
                DeviceId = deviceId,
                DeviceName = deviceName.Trim(),
                GeneratedAtMs = nowMs,
                UtcOffsetMinutes = offsetMinutes,
                AppVersion = appVersion,
                SessionsPartial = ReadSessionsPartial(connection),
                RequestDaily = requestDaily.Values
                    .OrderBy(row => row.TimeMs).ThenBy(row => row.Client, StringComparer.Ordinal)
                    .ThenBy(row => row.Model, StringComparer.Ordinal).ToList(),
                RequestHourly = requestHourly.Values
                    .OrderBy(row => row.TimeMs).ThenBy(row => row.Client, StringComparer.Ordinal)
                    .ThenBy(row => row.Model, StringComparer.Ordinal).ToList(),
                SessionDaily = sessionDaily.Values
                    .OrderBy(row => row.TimeMs).ThenBy(row => row.Client, StringComparer.Ordinal).ToList(),
                SessionHourly = sessionHourly.Values
                    .OrderBy(row => row.TimeMs).ThenBy(row => row.Client, StringComparer.Ordinal).ToList(),
            };
            using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
            return document;
        }
        catch
        {
            try
            {
                using var rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // 原始异常更有价值；回滚失败不覆盖它。
            }
            throw;
        }
    }

    private static Dictionary<(long DayMs, string Client, string Model), PeerRequestRow> ReadRequestDaily(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hour_ms, client, COALESCE(model, ''),
                   SUM(requests), SUM(requests_with_usage), SUM(requests_without_usage),
                   SUM(total_tokens)
            FROM request_hourly
            GROUP BY hour_ms, client, COALESCE(model, '');
            """;
        var rows = new Dictionary<(long, string, string), PeerRequestRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var dayMs = LocalDayStart(reader.GetInt64(0));
            AddRequest(rows, dayMs, reader.GetString(1), reader.GetString(2),
                tokens: reader.GetInt64(6), requests: reader.GetInt32(3),
                withUsage: reader.GetInt32(4), withoutUsage: reader.GetInt32(5));
        }
        return rows;
    }

    private static Dictionary<(long HourMs, string Client, string Model), PeerRequestRow> ReadRequestHourly(
        SqliteConnection connection, long fromMs, long toMs)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hour_ms, client, COALESCE(model, ''),
                   SUM(requests), SUM(requests_with_usage), SUM(requests_without_usage),
                   SUM(total_tokens)
            FROM request_hourly
            WHERE hour_ms >= $from AND hour_ms < $to
            GROUP BY hour_ms, client, COALESCE(model, '');
            """;
        command.Parameters.AddWithValue("$from", fromMs);
        command.Parameters.AddWithValue("$to", toMs);
        var rows = new Dictionary<(long, string, string), PeerRequestRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            AddRequest(rows, reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                tokens: reader.GetInt64(6), requests: reader.GetInt32(3),
                withUsage: reader.GetInt32(4), withoutUsage: reader.GetInt32(5));
        }
        return rows;
    }

    private sealed record EstimatedRows(
        List<PeerRequestRow> Daily,
        List<PeerRequestRow> Hourly);

    private static EstimatedRows ReadEstimated(SqliteConnection connection, long fromMs, long toMs)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (m.created_at_ms / 3600000) * 3600000 AS hour_ms,
                   m.client, COALESCE(m.model, s.model, ''),
                   COUNT(*), COALESCE(SUM(m.total_tokens), 0)
            FROM messages m JOIN sessions s ON s.id = m.session_id
            WHERE m.source_kind = 'estimated' AND m.created_at_ms IS NOT NULL
            GROUP BY hour_ms, m.client, COALESCE(m.model, s.model, '');
            """;
        var daily = new Dictionary<(long, string, string), PeerRequestRow>();
        var hourly = new Dictionary<(long, string, string), PeerRequestRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var hourMs = reader.GetInt64(0);
            var client = reader.GetString(1);
            var model = reader.GetString(2);
            var requests = reader.GetInt32(3);
            var tokens = reader.GetInt64(4);
            AddRequest(daily, LocalDayStart(hourMs), client, model,
                estimatedTokens: tokens, estimatedRequests: requests);
            if (hourMs >= fromMs && hourMs < toMs)
            {
                AddRequest(hourly, hourMs, client, model,
                    estimatedTokens: tokens, estimatedRequests: requests);
            }
        }
        return new EstimatedRows(
            daily.Values.OrderBy(row => row.TimeMs).ToList(),
            hourly.Values.OrderBy(row => row.TimeMs).ToList());
    }

    private static Dictionary<(long DayMs, string Client), PeerSessionRow> ReadSessionDaily(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT started_at_ms, client, source_kind
            FROM sessions WHERE started_at_ms IS NOT NULL;
            """;
        var rows = new Dictionary<(long, string), PeerSessionRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            AddSession(rows, LocalDayStart(reader.GetInt64(0)), reader.GetString(1),
                reader.GetString(2) == "estimated");
        }
        return rows;
    }

    private static Dictionary<(long HourMs, string Client), PeerSessionRow> ReadSessionHourly(
        SqliteConnection connection, long fromMs, long toMs)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT started_at_ms, client, source_kind
            FROM sessions
            WHERE started_at_ms >= $from AND started_at_ms < $to;
            """;
        command.Parameters.AddWithValue("$from", fromMs);
        command.Parameters.AddWithValue("$to", toMs);
        var rows = new Dictionary<(long, string), PeerSessionRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            AddSession(rows, LocalHourStart(reader.GetInt64(0)), reader.GetString(1),
                reader.GetString(2) == "estimated");
        }
        return rows;
    }

    private static long ReadSessionsPartial(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE message_coverage = 'partial';";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddRequest(
        Dictionary<(long, string, string), PeerRequestRow> rows,
        long timeMs,
        string client,
        string model,
        long tokens = 0,
        int requests = 0,
        int withUsage = 0,
        int withoutUsage = 0,
        long estimatedTokens = 0,
        int estimatedRequests = 0)
    {
        var key = (timeMs, client, model);
        if (!rows.TryGetValue(key, out var row))
        {
            row = new PeerRequestRow { TimeMs = timeMs, Client = client, Model = model };
            rows[key] = row;
        }
        row.Tokens += tokens;
        row.Requests += requests;
        row.RequestsWithUsage += withUsage;
        row.RequestsWithoutUsage += withoutUsage;
        row.EstimatedTokens += estimatedTokens;
        row.EstimatedRequests += estimatedRequests;
    }

    private static void AddSession(
        Dictionary<(long, string), PeerSessionRow> rows,
        long timeMs,
        string client,
        bool estimated)
    {
        var key = (timeMs, client);
        if (!rows.TryGetValue(key, out var row))
        {
            row = new PeerSessionRow { TimeMs = timeMs, Client = client };
            rows[key] = row;
        }
        row.Sessions++;
        if (estimated)
        {
            row.EstimatedSessions++;
        }
    }

    private static long LocalDayStart(long value)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime.Date;
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    private static long LocalHourStart(long value)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime;
        return new DateTimeOffset(local.Date.AddHours(local.Hour)).ToUnixTimeMilliseconds();
    }
}
