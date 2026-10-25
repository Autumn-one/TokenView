using System.IO;
using Microsoft.Data.Sqlite;

namespace TokenView.Wpf.Data;

public sealed record RangeSelection(string Key, string Label, long FromMs, long ToMs, bool IsHourly);

public static class TokenViewDatabase
{
    private const long HourMs = 3_600_000;
    private const long DayMs = 86_400_000;

    public static string DatabasePath => StoragePaths.DatabasePath;

    public static readonly IReadOnlyDictionary<string, ClientInfo> Clients =
        new Dictionary<string, ClientInfo>
        {
            ["codex"] = new("codex", "Codex", "#099f83"),
            ["claude_code"] = new("claude_code", "Claude Code", "#d97706"),
            ["opencode"] = new("opencode", "OpenCode", "#6366f1"),
            ["dsh"] = new("dsh", "DSH", "#6f7bf7"),
            ["kimi_code"] = new("kimi_code", "Kimi Code", "#4f8ff7"),
            ["mimocode"] = new("mimocode", "MiMoCode", "#f08a3c"),
            ["pi_desktop"] = new("pi_desktop", "Pi Desktop", "#b06fe0"),
            ["pi_agent"] = new("pi_agent", "Pi Agent", "#0e7490"),
            ["workbuddy"] = new("workbuddy", "WorkBuddy", "#3b82c4"),
            ["copilot"] = new("copilot", "GitHub Copilot", "#6b5bd0"),
            ["gemini_cli"] = new("gemini_cli", "Gemini CLI", "#5b7fe0"),
            ["grok"] = new("grok", "Grok CLI", "#6b7280"),
            ["mcode"] = new("mcode", "MCode", "#d9534f"),
            ["devin"] = new("devin", "Devin", "#3969ca"),
            ["command_code"] = new("command_code", "Command Code", "#313844"),
            ["cline"] = new("cline", "Cline", "#323b43"),
            ["qoder"] = new("qoder", "Qoder", "#c026d3"),
            ["codearts"] = new("codearts", "CodeArts", "#c7000b"),
        };

    public static RangeSelection CreateRange(string key)
    {
        var today = DateTime.Today;
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds() + 60_000;
        return key switch
        {
            "7d" => new(key, "最近 7 天", StartOfDay(today.AddDays(-6)), now, false),
            "30d" => new(key, "最近 30 天", StartOfDay(today.AddDays(-29)), now, false),
            "90d" => new(key, "最近 90 天", StartOfDay(today.AddDays(-89)), now, false),
            "week" => new(key, "本周", StartOfDay(today.AddDays(-(((int)today.DayOfWeek + 6) % 7))), now, false),
            "month" => new(key, "本月", StartOfDay(new DateTime(today.Year, today.Month, 1)), now, false),
            "all" => new(key, "全部", 0, now, false),
            _ => new("today", "今天", StartOfDay(today), now, true),
        };
    }

    public static RangeSelection CreateCustomRange(long fromMs, long toMs) =>
        new("custom", "自定义", fromMs, toMs, false);

    public static OverviewSnapshot Load(RangeSelection range)
    {
        if (!File.Exists(DatabasePath))
        {
            throw new FileNotFoundException("未找到 TokenView 用量数据库。", DatabasePath);
        }

        using var connection = OpenReadOnly();
        var from = range.FromMs;
        var to = range.ToMs;

        if (range.Key == "all")
        {
            var min = ScalarLong(connection, "SELECT COALESCE(MIN(hour_ms), $1) FROM request_hourly", to);
            from = min;
        }

        var hours = ReadHourRows(connection, from, to);
        var estimates = ReadEstimatedUsage(connection, from, to);
        var totalTokens = hours.Sum(row => row.Tokens);
        var totalRequests = hours.Sum(row => row.Requests);
        var withUsage = hours.Sum(row => row.RequestsWithUsage);
        var withoutUsage = hours.Sum(row => row.RequestsWithoutUsage);
        var sessions = ScalarLong(connection,
            "SELECT COUNT(DISTINCT session_id) FROM request_hourly_sessions WHERE hour_ms >= $from AND hour_ms < $to",
            from, to);
        var sessionsPartial = ScalarLong(connection,
            "SELECT COUNT(*) FROM sessions WHERE message_coverage = 'partial'");

        var sessionBuckets = ReadSessionBuckets(connection, from, to, range.IsHourly);
        var points = BuildChartPoints(range, from, to, hours, sessionBuckets);
        var dailyStrip = points
            .Select(point => new DailyStripRow(point.Label, point.TokenText))
            .ToList();

        var clients = ReadClientUsage(connection, from, to, hours, totalTokens, estimates);
        var models = ReadRanking(connection, "model", from, to);
        var projects = ReadRanking(connection, "project_path", from, to);
        var recent = ReadRecentSessions(connection, from, to);

        return new OverviewSnapshot
        {
            RangeText = BuildRangeText(from, to, range.IsHourly, includeDayCount: true),
            RangeDaysText = $"共 {RangeDayCount(from, to)} 天",
            RangeFromMs = from,
            RangeToMs = to,
            IsHourly = range.IsHourly,
            TotalRequests = totalRequests,
            RequestsWithUsage = withUsage,
            RequestsWithoutUsage = withoutUsage,
            Sessions = (int)sessions,
            SessionsPartial = (int)sessionsPartial,
            TotalTokens = totalTokens,
            EstimatedTokens = estimates.Sum(row => row.Tokens),
            EstimatedRequests = estimates.Sum(row => row.Requests),
            ChartPoints = points,
            DailyStrip = dailyStrip,
            Clients = clients,
            Models = models,
            Projects = projects,
            RecentSessions = recent,
        };
    }

    public static SessionDetailSnapshot LoadSessionDetail(string clientKey, string clientSessionId,
        int offset = 0, int limit = 100)
    {
        if (!File.Exists(DatabasePath))
        {
            throw new FileNotFoundException("未找到 TokenView 用量数据库。", DatabasePath);
        }
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 500);
        using var connection = OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, client, COALESCE(project_path, '—'), COALESCE(model, '—'),
                   started_at_ms, ended_at_ms, total_tokens,
                   input_tokens, output_tokens, reasoning_tokens, cache_read_tokens, cache_write_tokens,
                   total_basis, message_coverage, unknown_relations, source_kind
            FROM sessions WHERE client = $client AND client_session_id = $sid LIMIT 1;
            """;
        command.Parameters.AddWithValue("$client", clientKey);
        command.Parameters.AddWithValue("$sid", clientSessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("未找到该会话。");
        }
        var id = reader.GetString(0);
        var client = Clients.GetValueOrDefault(reader.GetString(1)) ??
            new ClientInfo(reader.GetString(1), reader.GetString(1), "#6b7280");
        var project = reader.GetString(2);
        var model = reader.GetString(3);
        var started = reader.IsDBNull(4) ? "—" :
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        var ended = reader.IsDBNull(5) ? "—" :
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        long? tokens = reader.IsDBNull(6) ? null : reader.GetInt64(6);
        long? input = reader.IsDBNull(7) ? null : reader.GetInt64(7);
        long? output = reader.IsDBNull(8) ? null : reader.GetInt64(8);
        long? reasoning = reader.IsDBNull(9) ? null : reader.GetInt64(9);
        long? cacheRead = reader.IsDBNull(10) ? null : reader.GetInt64(10);
        long? cacheWrite = reader.IsDBNull(11) ? null : reader.GetInt64(11);
        var basis = reader.GetString(12) switch
        {
            "client_cumulative" => "客户端累计值",
            "client_reported" => "客户端自报值",
            _ => "请求明细求和",
        };
        var coverage = reader.GetString(13) switch
        {
            "complete" => "明细完整",
            "partial" => "明细不完整",
            _ => "无请求明细",
        };
        var unknownRelations = reader.IsDBNull(14)
            ? []
            : reader.GetString(14).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var isEstimated = reader.GetString(15) == "estimated";
        reader.Close();

        var (totalMessages, timed, withUsage, estimated) = ReadMessageCounts(connection, id);
        using var messages = connection.CreateCommand();
        messages.CommandText = """
            SELECT seq, created_at_ms, completed_at_ms, model, agent,
                   input_tokens, output_tokens, reasoning_tokens, cache_read_tokens, cache_write_tokens,
                   total_tokens, source_kind, source_ref, is_sidechain, unknown_relations
            FROM messages WHERE session_id = $id
            ORDER BY created_at_ms, dedup_key LIMIT $limit OFFSET $offset;
            """;
        messages.Parameters.AddWithValue("$id", id);
        messages.Parameters.AddWithValue("$limit", limit);
        messages.Parameters.AddWithValue("$offset", offset);
        var rows = new List<SessionMessageRow>();
        using var messageReader = messages.ExecuteReader();
        while (messageReader.Read())
        {
            var created = messageReader.IsDBNull(1) ? "—" :
                DateTimeOffset.FromUnixTimeMilliseconds(messageReader.GetInt64(1)).LocalDateTime
                    .ToString("yyyy-MM-dd HH:mm:ss");
            var completed = messageReader.IsDBNull(2) ? "—" :
                DateTimeOffset.FromUnixTimeMilliseconds(messageReader.GetInt64(2)).LocalDateTime
                    .ToString("yyyy-MM-dd HH:mm:ss");
            long? ReadToken(int index) => messageReader.IsDBNull(index) ? null : messageReader.GetInt64(index);
            var messageInput = ReadToken(5);
            var messageOutput = ReadToken(6);
            var messageReasoning = ReadToken(7);
            var messageCacheRead = ReadToken(8);
            var messageCacheWrite = ReadToken(9);
            var messageTotal = ReadToken(10);
            var kind = messageReader.GetString(11) switch
            {
                "measured" => "实测",
                "derived" => "换算",
                _ => "估算",
            };
            var missing = new List<string>();
            if (messageInput is null) missing.Add("输入");
            if (messageOutput is null) missing.Add("输出");
            if (messageReasoning is null) missing.Add("推理");
            if (messageCacheRead is null) missing.Add("缓存读取");
            if (messageCacheWrite is null) missing.Add("缓存写入");
            if (messageTotal is null) missing.Add("总 Token");
            var status = new List<string>();
            if (messageReader.GetInt64(13) != 0) status.Add("子代理 / 旁链");
            if (messageReader.IsDBNull(1)) status.Add("缺失请求时间");
            if (missing.Count > 0) status.Add($"缺失：{string.Join("、", missing)}");
            var relations = messageReader.IsDBNull(14)
                ? []
                : messageReader.GetString(14).Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (relations.Length > 0) status.Add($"未知关系：{string.Join("、", relations)}");
            rows.Add(new SessionMessageRow(
                messageReader.IsDBNull(0) ? "—" : messageReader.GetInt64(0).ToString(),
                created, completed,
                messageReader.IsDBNull(3) ? "—" : messageReader.GetString(3),
                messageReader.IsDBNull(4) ? "—" : messageReader.GetString(4),
                messageInput, messageOutput, messageReasoning, messageCacheRead, messageCacheWrite,
                messageTotal, kind,
                messageReader.IsDBNull(12) ? "来源引用未记录" : messageReader.GetString(12),
                string.Join(" · ", status)));
        }
        return new SessionDetailSnapshot(client.Label, client.ColorHex, clientSessionId, project, model,
            started, ended, tokens, input, output, reasoning, cacheRead, cacheWrite,
            basis, coverage, unknownRelations, rows.Count, offset / limit + 1,
            (int)totalMessages, (int)timed, (int)(totalMessages - timed), (int)withUsage,
            (int)(totalMessages - withUsage), (int)estimated, rows, isEstimated);
    }

    private static (long Total, long Timed, long WithUsage, long Estimated) ReadMessageCounts(
        SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(created_at_ms IS NOT NULL), 0),
                   COALESCE(SUM(total_tokens IS NOT NULL), 0),
                   COALESCE(SUM(source_kind = 'estimated'), 0)
            FROM messages WHERE session_id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3))
            : (0, 0, 0, 0);
    }

    public static LedgerSnapshot LoadLedger(RangeSelection range)
    {
        if (!File.Exists(DatabasePath))
        {
            throw new FileNotFoundException("未找到 TokenView 用量数据库。", DatabasePath);
        }
        using var connection = OpenReadOnly();
        var from = range.FromMs;
        var to = range.ToMs;
        if (range.Key == "all")
        {
            from = ScalarLong(connection,
                "SELECT COALESCE(MIN(started_at_ms), $1) FROM sessions WHERE started_at_ms IS NOT NULL", to);
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.client, s.client_session_id, COALESCE(s.project_path, '—'),
                   COALESCE(s.model, '—'), s.started_at_ms, s.ended_at_ms,
                   s.total_tokens, s.message_coverage, s.source_kind,
                   (SELECT COUNT(*) FROM messages m WHERE m.session_id = s.id)
            FROM sessions s
            WHERE s.started_at_ms >= $from AND s.started_at_ms < $to
            ORDER BY s.started_at_ms DESC;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var sessions = new List<SessionRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var client = Clients.GetValueOrDefault(key) ?? new ClientInfo(key, key, "#6b7280");
                var started = reader.IsDBNull(4) ? "—" :
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                var coverage = reader.GetString(7) switch
                {
                    "complete" => "完整",
                    "partial" => "部分",
                    _ => "无明细",
                };
                sessions.Add(new SessionRow(key, client.Label, client.ColorHex, reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), started, reader.GetInt32(9),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6), coverage,
                    reader.GetString(8) == "estimated"));
            }
        }

        var measuredSessions = sessions.Where(session => !session.IsEstimated).ToList();
        var totalTokens = measuredSessions.Sum(session => session.Tokens ?? 0);
        var estimatedTokens = sessions.Where(session => session.IsEstimated)
            .Sum(session => session.Tokens ?? 0);
        var measuredSessionsWithoutUsage = measuredSessions.Count(session => session.Tokens is null);
        var byDay = measuredSessions
            .Where(session => DateTimeOffset.TryParse(session.Started, out _))
            .GroupBy(session => session.Started[..10])
            .OrderBy(group => group.Key)
            .ToList();
        var daily = new List<ChartPoint>();
        if (byDay.Count > 0)
        {
            var first = DateTime.Parse(byDay[0].Key);
            var last = DateTime.Parse(byDay[^1].Key);
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                var key = day.ToString("yyyy-MM-dd");
                var group = byDay.FirstOrDefault(entry => entry.Key == key);
                var count = group?.Count() ?? 0;
                var known = group?.Count(session => session.Tokens is not null) ?? 0;
                var tokens = group?.Sum(session => session.Tokens ?? 0) ?? 0;
                var slices = group is null
                    ? new List<ChartClientSlice>()
                    : group.GroupBy(session => session.ClientKey)
                        .Select(clientGroup =>
                        {
                            var info = Clients.GetValueOrDefault(clientGroup.Key) ??
                                new ClientInfo(clientGroup.Key, clientGroup.Key, "#6b7280");
                            var clientKnown = clientGroup.Count(session => session.Tokens is not null);
                            return new ChartClientSlice(clientGroup.Key, info.Label, info.ColorHex,
                                clientGroup.Sum(session => session.Tokens ?? 0), clientGroup.Count(),
                                clientGroup.Count() - clientKnown);
                        })
                        .OrderByDescending(slice => slice.Tokens)
                        .ToList();
                daily.Add(new ChartPoint(new DateTimeOffset(day).ToUnixTimeMilliseconds(),
                    day.ToString("MM-dd"), tokens, count, known, count - known,
                    $"{day:yyyy-MM-dd} · 会话开始日", count, slices));
            }
        }

        var clients = sessions
            .GroupBy(session => session.ClientKey)
            .Select(group =>
            {
                var client = Clients.GetValueOrDefault(group.Key) ??
                    new ClientInfo(group.Key, group.Key, "#6b7280");
                var measured = group.Where(session => !session.IsEstimated).ToList();
                var tokens = measured.Sum(session => session.Tokens ?? 0);
                var unknown = measured.Count(session => session.Tokens is null);
                var estimated = group.Where(session => session.IsEstimated)
                    .Sum(session => session.Tokens ?? 0);
                return new LedgerClientRow(client, group.Count(), tokens,
                    totalTokens == 0 ? 0 : (double)tokens / totalTokens, unknown, estimated);
            })
            .OrderByDescending(row => row.Tokens)
            .ThenByDescending(row => row.EstimatedTokens)
            .ToList();

        var todayStart = StartOfDay(DateTime.Today);
        long todayTokens = 0;
        int todayRequests = 0, todayRequestsWithoutUsage = 0;
        using (var todayCommand = connection.CreateCommand())
        {
            todayCommand.CommandText = """
                SELECT COALESCE(SUM(total_tokens), 0), COALESCE(SUM(requests), 0),
                       COALESCE(SUM(requests_without_usage), 0)
                FROM request_hourly WHERE hour_ms >= $from AND hour_ms < $to;
                """;
            todayCommand.Parameters.AddWithValue("$from", todayStart);
            todayCommand.Parameters.AddWithValue("$to", to);
            using var todayReader = todayCommand.ExecuteReader();
            if (todayReader.Read())
            {
                todayTokens = todayReader.GetInt64(0);
                todayRequests = todayReader.GetInt32(1);
                todayRequestsWithoutUsage = todayReader.GetInt32(2);
            }
        }
        var sessionsWithoutUsage = ScalarLong(connection,
            "SELECT COUNT(*) FROM sessions WHERE started_at_ms >= $from AND started_at_ms < $to " +
            "AND source_kind <> 'estimated' AND total_tokens IS NULL", from, to);
        var messages = ScalarLong(connection,
            "SELECT COUNT(*) FROM messages m JOIN sessions s ON s.id = m.session_id " +
            "WHERE s.started_at_ms >= $from AND s.started_at_ms < $to", from, to);
        long cacheInput = 0, cacheRead = 0;
        using (var cacheCommand = connection.CreateCommand())
        {
            cacheCommand.CommandText = """
                SELECT COALESCE(SUM(input_tokens), 0), COALESCE(SUM(cache_read_tokens), 0)
                FROM sessions
                WHERE started_at_ms >= $from AND started_at_ms < $to
                  AND source_kind <> 'estimated'
                  AND input_tokens IS NOT NULL AND cache_read_tokens IS NOT NULL;
                """;
            cacheCommand.Parameters.AddWithValue("$from", from);
            cacheCommand.Parameters.AddWithValue("$to", to);
            using var cacheReader = cacheCommand.ExecuteReader();
            if (cacheReader.Read())
            {
                cacheInput = cacheReader.GetInt64(0);
                cacheRead = cacheReader.GetInt64(1);
            }
        }
        int? cacheHitBps = cacheInput + cacheRead > 0
            ? (int)(cacheRead * 10_000.0 / (cacheInput + cacheRead))
            : null;

        return new LedgerSnapshot(range.Label, BuildRangeText(from, to, false),
            sessions.Count, totalTokens, todayTokens, todayRequests,
            todayRequestsWithoutUsage, (int)sessionsWithoutUsage,
            (int)messages, cacheHitBps, daily, clients, sessions,
            measuredSessions.Count, measuredSessionsWithoutUsage, estimatedTokens);
    }

    private static SqliteConnection OpenReadOnly()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            // Pooling=false 下共享缓存没有收益，只让页缓存的生命周期跨越连接。
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private sealed record HourRow(long HourMs, string Client, string Model, int Requests,
        int RequestsWithUsage, int RequestsWithoutUsage, long Tokens);

    private static List<HourRow> ReadHourRows(SqliteConnection connection, long from, long to)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hour_ms, client, COALESCE(model, ''),
                   SUM(requests), SUM(requests_with_usage), SUM(requests_without_usage),
                   SUM(total_tokens)
            FROM request_hourly
            WHERE hour_ms >= $from AND hour_ms < $to
            GROUP BY hour_ms, client, COALESCE(model, '')
            ORDER BY hour_ms;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var rows = new List<HourRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new HourRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt64(6)));
        }
        return rows;
    }

    private static List<ChartPoint> BuildChartPoints(RangeSelection range, long from, long to,
        IReadOnlyList<HourRow> hours, IReadOnlyDictionary<long, int> sessionBuckets)
    {
        if (range.IsHourly)
        {
            var start = new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime
                .Date.AddHours(DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime.Hour));
            var end = DateTimeOffset.FromUnixTimeMilliseconds(to).LocalDateTime;
            var buckets = hours
                .GroupBy(row => new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(row.HourMs).LocalDateTime
                    .Date.AddHours(DateTimeOffset.FromUnixTimeMilliseconds(row.HourMs).LocalDateTime.Hour)))
                .ToDictionary(group => group.Key, group => group.ToList());
            var points = new List<ChartPoint>();
            for (var cursor = start; cursor.LocalDateTime <= end; cursor = cursor.AddHours(1))
            {
                var list = buckets.GetValueOrDefault(cursor) ?? [];
                var startMs = cursor.ToUnixTimeMilliseconds();
                points.Add(Point(startMs, cursor.LocalDateTime.ToString("HH:mm"),
                    cursor.LocalDateTime.ToString("yyyy-MM-dd HH:00"), list,
                    sessionBuckets.GetValueOrDefault(startMs)));
            }
            return points;
        }

        var firstDay = DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime.Date;
        var lastDay = DateTimeOffset.FromUnixTimeMilliseconds(to).LocalDateTime.Date;
        var cappedFirst = lastDay.AddDays(-399);
        if (firstDay < cappedFirst)
        {
            firstDay = cappedFirst;
        }
        var byDay = hours
            .GroupBy(row => DateTimeOffset.FromUnixTimeMilliseconds(row.HourMs).LocalDateTime.Date)
            .ToDictionary(group => group.Key, group => group.ToList());
        var days = new List<ChartPoint>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            var list = byDay.GetValueOrDefault(day) ?? [];
            var startMs = StartOfDay(day);
            days.Add(Point(startMs, day.ToString("MM-dd"),
                day.ToString("yyyy-MM-dd"), list,
                sessionBuckets.GetValueOrDefault(startMs)));
        }
        return days;
    }

    private static Dictionary<long, int> ReadSessionBuckets(SqliteConnection connection, long from,
        long to, bool hourly)
    {
        var sets = new Dictionary<long, HashSet<string>>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hour_ms, session_id FROM request_hourly_sessions
            WHERE hour_ms >= $from AND hour_ms < $to;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var hourMs = reader.GetInt64(0);
            var key = hourly ? LocalHourStart(hourMs) : StartOfDay(
                DateTimeOffset.FromUnixTimeMilliseconds(hourMs).LocalDateTime.Date);
            if (!sets.TryGetValue(key, out var bucket))
            {
                bucket = new HashSet<string>(StringComparer.Ordinal);
                sets[key] = bucket;
            }
            bucket.Add(reader.GetString(1));
        }
        return sets.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
    }

    private static long LocalHourStart(long value)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime;
        return new DateTimeOffset(local.Date.AddHours(local.Hour)).ToUnixTimeMilliseconds();
    }

    private static ChartPoint Point(long startMs, string label, string title,
        IReadOnlyList<HourRow> rows, int sessions)
    {
        var clients = rows
            .GroupBy(row => row.Client)
            .Select(group =>
            {
                var client = Clients.GetValueOrDefault(group.Key) ??
                    new ClientInfo(group.Key, group.Key, "#6b7280");
                return new ChartClientSlice(group.Key, client.Label, client.ColorHex,
                    group.Sum(row => row.Tokens),
                    group.Sum(row => row.Requests),
                    group.Sum(row => row.RequestsWithoutUsage));
            })
            .OrderByDescending(slice => slice.Tokens)
            .ThenByDescending(slice => slice.Requests)
            .ToList();
        var models = rows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.Model) ? "未标注模型" : row.Model)
            .Select(group => new ChartModelSlice(group.Key,
                group.Sum(row => row.Tokens),
                group.Sum(row => row.Requests),
                group.Sum(row => row.RequestsWithoutUsage)))
            .OrderBy(slice => slice.Model == "未标注模型")
            .ThenByDescending(slice => slice.Tokens)
            .ThenByDescending(slice => slice.Requests)
            .ToList();
        return new ChartPoint(
            startMs,
            label,
            rows.Sum(row => row.Tokens),
            rows.Sum(row => row.Requests),
            rows.Sum(row => row.RequestsWithUsage),
            rows.Sum(row => row.RequestsWithoutUsage),
            title,
            sessions,
            clients,
            models);
    }

    private static List<ClientUsageRow> ReadClientUsage(SqliteConnection connection, long from,
        long to, IReadOnlyList<HourRow> hours, long totalTokens,
        IReadOnlyList<EstimatedUsageRow> estimates)
    {
        using var sessionsCommand = connection.CreateCommand();
        sessionsCommand.CommandText = """
            SELECT client, COUNT(DISTINCT session_id)
            FROM request_hourly_sessions
            WHERE hour_ms >= $from AND hour_ms < $to
            GROUP BY client;
            """;
        sessionsCommand.Parameters.AddWithValue("$from", from);
        sessionsCommand.Parameters.AddWithValue("$to", to);
        var sessions = new Dictionary<string, int>();
        using (var reader = sessionsCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                sessions[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        var estimatesByClient = estimates
            .GroupBy(row => row.Client)
            .ToDictionary(group => group.Key,
                group => (Tokens: group.Sum(row => row.Tokens),
                    Requests: group.Sum(row => row.Requests)));
        var models = ReadClientModels(connection, from, to);
        return hours
            .GroupBy(row => row.Client)
            .Select(group =>
            {
                var client = Clients.GetValueOrDefault(group.Key) ?? new ClientInfo(group.Key, group.Key, "#6b7280");
                var tokens = group.Sum(row => row.Tokens);
                var requests = group.Sum(row => row.Requests);
                var withoutUsage = group.Sum(row => row.RequestsWithoutUsage);
                var estimate = estimatesByClient.GetValueOrDefault(group.Key);
                return new ClientUsageRow(client, sessions.GetValueOrDefault(group.Key), requests,
                    withoutUsage, tokens, totalTokens == 0 ? 0 : (double)tokens / totalTokens,
                    models.GetValueOrDefault(group.Key) ?? [], estimate.Tokens, estimate.Requests);
            })
            .OrderByDescending(row => row.Tokens)
            .ThenByDescending(row => row.EstimatedTokens)
            .ThenByDescending(row => row.Requests)
            .ToList();
    }

    private sealed record EstimatedUsageRow(string Client, string Model, int Requests, long Tokens);

    private static List<EstimatedUsageRow> ReadEstimatedUsage(SqliteConnection connection,
        long from, long to)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.client, COALESCE(m.model, s.model, '—'), COUNT(*),
                   COALESCE(SUM(m.total_tokens), 0)
            FROM messages m JOIN sessions s ON s.id = m.session_id
            WHERE m.source_kind = 'estimated' AND m.created_at_ms >= $from AND m.created_at_ms < $to
            GROUP BY s.client, COALESCE(m.model, s.model, '—');
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var rows = new List<EstimatedUsageRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new EstimatedUsageRow(reader.GetString(0), reader.GetString(1),
                reader.GetInt32(2), reader.GetInt64(3)));
        }
        return rows;
    }

    private static Dictionary<string, List<ClientModelRow>> ReadClientModels(SqliteConnection connection,
        long from, long to)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT client, COALESCE(model, '—') AS model, SUM(requests) AS requests,
                   SUM(total_tokens) AS tokens, SUM(requests_without_usage) AS requests_without_usage
            FROM request_hourly
            WHERE hour_ms >= $from AND hour_ms < $to
            GROUP BY client, COALESCE(model, '—')
            ORDER BY client, tokens DESC, requests DESC;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var grouped = new Dictionary<string, List<(string Name, int Requests, long Tokens,
            int RequestsWithoutUsage)>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var client = reader.GetString(0);
            if (!grouped.TryGetValue(client, out var rows))
            {
                rows = [];
                grouped[client] = rows;
            }
            rows.Add((reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt32(4)));
        }
        return grouped.ToDictionary(pair => pair.Key, pair =>
        {
            var total = pair.Value.Sum(row => row.Tokens);
            return pair.Value
                .Select(row => new ClientModelRow(row.Name, row.Requests, row.Tokens,
                    total == 0 ? 0 : (double)row.Tokens / total, row.RequestsWithoutUsage))
                .ToList();
        });
    }

    private static List<RankingRow> ReadRanking(SqliteConnection connection, string column, long from, long to)
    {
        var safeColumn = column == "model" ? "model" : "project_path";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE({safeColumn}, '—') AS name, COALESCE(SUM(total_tokens), 0) AS tokens,
                   COUNT(*) AS sessions, COALESCE(SUM(total_tokens IS NULL), 0) AS unknown_sessions
            FROM sessions
            WHERE started_at_ms >= $from AND started_at_ms < $to
              AND source_kind <> 'estimated'
            GROUP BY COALESCE({safeColumn}, '—')
            ORDER BY tokens DESC
            LIMIT 5;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var values = new List<(string Name, long Tokens, int Sessions, int UnknownSessions)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                values.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2),
                    reader.GetInt32(3)));
            }
        }
        var total = values.Sum(value => value.Tokens);
        return values.Select(value => new RankingRow(value.Name, value.Tokens,
            total == 0 ? 0 : (double)value.Tokens / total, value.Sessions,
            value.UnknownSessions)).ToList();
    }

    private static List<SessionRow> ReadRecentSessions(SqliteConnection connection, long from, long to)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.client, s.client_session_id, COALESCE(s.project_path, '—'),
                   COALESCE(s.model, '—'), s.started_at_ms, s.total_tokens,
                   s.message_coverage, s.source_kind,
                   (SELECT COUNT(*) FROM messages m WHERE m.session_id = s.id)
            FROM sessions s
            WHERE s.started_at_ms >= $from AND s.started_at_ms < $to
            ORDER BY s.started_at_ms DESC
            LIMIT 8;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var rows = new List<SessionRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var client = Clients.GetValueOrDefault(key) ?? new ClientInfo(key, key, "#6b7280");
            var started = reader.IsDBNull(4)
                ? "—"
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            var coverage = reader.GetString(6) switch
            {
                "complete" => "完整",
                "partial" => "部分",
                _ => "无明细",
            };
            rows.Add(new SessionRow(key, client.Label, client.ColorHex, reader.GetString(1),
                reader.GetString(2), reader.GetString(3), started, reader.GetInt32(8),
                reader.IsDBNull(5) ? null : reader.GetInt64(5), coverage,
                reader.GetString(7) == "estimated"));
        }
        return rows;
    }

    private static long ScalarLong(SqliteConnection connection, string sql, params long[] values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var index = 0; index < values.Length; index++)
        {
            command.Parameters.AddWithValue($"${index + 1}", values[index]);
        }
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static long ScalarLong(SqliteConnection connection, string sql, long from, long to)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static long StartOfDay(DateTime day) =>
        new DateTimeOffset(day.Date).ToUnixTimeMilliseconds();

    private static int RangeDayCount(long from, long to)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime;
        var end = DateTimeOffset.FromUnixTimeMilliseconds(to - 1).LocalDateTime;
        return Math.Max(1, (end.Date - start.Date).Days + 1);
    }

    private static string BuildRangeText(long from, long to, bool hourly, bool includeDayCount = false)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime;
        var end = DateTimeOffset.FromUnixTimeMilliseconds(to - 1).LocalDateTime;
        var daySuffix = includeDayCount ? $" · 共 {RangeDayCount(from, to)} 天" : "";
        return hourly
            ? $"{start:yyyy-MM-dd HH:mm} — {end:HH:mm} · 本地时区{daySuffix}"
            : $"{start:yyyy-MM-dd} — {end:yyyy-MM-dd} · 本地时区{daySuffix}";
    }

    public static string FormatTokens(long value) => TokenFormatting.Format(value);

    public static (long Tokens, int Requests, int RequestsWithoutUsage) LoadTodaySummary()
    {
        if (!File.Exists(DatabasePath)) return (0, 0, 0);
        var start = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
        var end = DateTimeOffset.Now.ToUnixTimeMilliseconds() + 60_000;
        using var connection = OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(total_tokens), 0), COALESCE(SUM(requests), 0),
                   COALESCE(SUM(requests_without_usage), 0)
            FROM request_hourly WHERE hour_ms >= $from AND hour_ms < $to;
            """;
        command.Parameters.AddWithValue("$from", start);
        command.Parameters.AddWithValue("$to", end);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (0, 0, 0);
        return (reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public static Dictionary<string, ClientStatsRow> LoadClientStats()
    {
        var result = new Dictionary<string, ClientStatsRow>(StringComparer.Ordinal);
        if (!File.Exists(DatabasePath)) return result;
        using var connection = OpenReadOnly();

        var sessions = new Dictionary<string, (long Count, long WithoutUsage, long Estimated,
            long? Updated)>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT client, COUNT(*),
                       SUM(CASE WHEN total_tokens IS NULL AND source_kind <> 'estimated'
                                THEN 1 ELSE 0 END),
                       SUM(CASE WHEN source_kind = 'estimated' THEN 1 ELSE 0 END),
                       MAX(updated_at_ms)
                FROM sessions GROUP BY client;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sessions[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4));
            }
        }

        var messages = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT client, COUNT(*) FROM messages GROUP BY client;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) messages[reader.GetString(0)] = reader.GetInt64(1);
        }

        var files = new Dictionary<string, (long Count, long Unparsed, long? Scan)>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT client, COUNT(*), COALESCE(SUM(unparsed_records), 0), MAX(last_scan_ms)
                FROM source_files GROUP BY client;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                files[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3));
            }
        }

        foreach (var key in Clients.Keys)
        {
            var session = sessions.GetValueOrDefault(key);
            var file = files.GetValueOrDefault(key);
            result[key] = new ClientStatsRow(key, session.Count, session.WithoutUsage,
                messages.GetValueOrDefault(key), file.Count, file.Unparsed, file.Scan,
                session.Estimated);
        }
        return result;
    }
}
