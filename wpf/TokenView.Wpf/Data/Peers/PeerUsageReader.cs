using Microsoft.Data.Sqlite;

namespace TokenView.Wpf.Data.Peers;

internal sealed record PeerUsageRow(
    string DeviceId,
    string DeviceName,
    long TimeMs,
    string Client,
    string Model,
    int Requests,
    int RequestsWithUsage,
    int RequestsWithoutUsage,
    long Tokens,
    long EstimatedTokens,
    int EstimatedRequests);

internal sealed record PeerSessionUsageRow(
    string DeviceId,
    string DeviceName,
    long TimeMs,
    string Client,
    int Sessions);

/// <summary>只读 peer_* 表；写入仍由 PeerSnapshotStore 统一负责。</summary>
internal static class PeerUsageReader
{
    private const string CalendarDayExpression =
        "((day_ms + utc_offset_minutes * 60000 + 43200000) / 86400000) * 86400000";

    public static long FirstDay(SqliteConnection connection, string deviceId, long fallback)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT MIN({CalendarDayExpression}) FROM peer_request_daily JOIN peer_devices USING (device_id)
            WHERE device_id = $device;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? fallback : CalendarDayToLocal(Convert.ToInt64(result));
    }

    public static List<PeerDeviceRow> ReadDevices(SqliteConnection connection, DeviceSelection selection)
    {
        if (!PeerSnapshotStore.TableExists(connection))
        {
            return [];
        }
        using var command = connection.CreateCommand();
        command.CommandText = selection.Scope == DeviceScope.Peer
            ? """
              SELECT device_id, name, generated_at_ms, imported_at_ms, schema_version,
                     utc_offset_minutes, app_version, sessions_partial
              FROM peer_devices WHERE device_id = $device ORDER BY name COLLATE NOCASE;
              """
            : """
              SELECT device_id, name, generated_at_ms, imported_at_ms, schema_version,
                     utc_offset_minutes, app_version, sessions_partial
              FROM peer_devices ORDER BY name COLLATE NOCASE, device_id;
              """;
        if (selection.Scope == DeviceScope.Peer)
        {
            command.Parameters.AddWithValue("$device", selection.DeviceId ?? "");
        }
        var devices = new List<PeerDeviceRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            devices.Add(new PeerDeviceRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetInt64(7)));
        }
        return devices;
    }

    public static List<PeerUsageRow> ReadRequestRows(
        SqliteConnection connection,
        IReadOnlyList<PeerDeviceRow> devices,
        long fromMs,
        long toMs,
        bool hourly)
    {
        if (devices.Count == 0)
        {
            return [];
        }
        var table = hourly ? "peer_request_hourly" : "peer_request_daily";
        var timeColumn = hourly ? "hour_ms" : "day_ms";
        var timeExpression = hourly ? timeColumn : CalendarDayExpression;
        if (!hourly) (fromMs, toMs) = CalendarBounds(fromMs, toMs);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT device_id, {timeExpression}, client, model, requests, requests_with_usage,
                   requests_without_usage, tokens, estimated_tokens, estimated_requests
            FROM {table} JOIN peer_devices USING (device_id)
            WHERE {DeviceFilter(devices, command)}
              AND {timeExpression} >= $from AND {timeExpression} < $to
            ORDER BY {timeColumn}, device_id, client, model;
            """;
        command.Parameters.AddWithValue("$from", fromMs);
        command.Parameters.AddWithValue("$to", toMs);
        var names = devices.ToDictionary(device => device.DeviceId, device => device.Name,
            StringComparer.Ordinal);
        var rows = new List<PeerUsageRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var deviceId = reader.GetString(0);
            rows.Add(new PeerUsageRow(
                deviceId,
                names.GetValueOrDefault(deviceId, deviceId),
                hourly ? reader.GetInt64(1) : CalendarDayToLocal(reader.GetInt64(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt32(9)));
        }
        return rows;
    }

    public static List<PeerSessionUsageRow> ReadSessionRows(
        SqliteConnection connection,
        IReadOnlyList<PeerDeviceRow> devices,
        long fromMs,
        long toMs,
        bool hourly)
    {
        if (devices.Count == 0)
        {
            return [];
        }
        var table = hourly ? "peer_session_hourly" : "peer_session_daily";
        var timeColumn = hourly ? "hour_ms" : "day_ms";
        var timeExpression = hourly ? timeColumn : CalendarDayExpression;
        if (!hourly) (fromMs, toMs) = CalendarBounds(fromMs, toMs);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT device_id, {timeExpression}, client, sessions
            FROM {table} JOIN peer_devices USING (device_id)
            WHERE {DeviceFilter(devices, command)}
              AND {timeExpression} >= $from AND {timeExpression} < $to
            ORDER BY {timeColumn}, device_id, client;
            """;
        command.Parameters.AddWithValue("$from", fromMs);
        command.Parameters.AddWithValue("$to", toMs);
        var names = devices.ToDictionary(device => device.DeviceId, device => device.Name,
            StringComparer.Ordinal);
        var rows = new List<PeerSessionUsageRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var deviceId = reader.GetString(0);
            rows.Add(new PeerSessionUsageRow(
                deviceId,
                names.GetValueOrDefault(deviceId, deviceId),
                hourly ? reader.GetInt64(1) : CalendarDayToLocal(reader.GetInt64(1)),
                reader.GetString(2),
                reader.GetInt32(3)));
        }
        return rows;
    }

    private static (long From, long To) CalendarBounds(long fromMs, long toMs)
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).LocalDateTime.Date;
        var to = DateTimeOffset.FromUnixTimeMilliseconds(toMs).LocalDateTime;
        var end = to.TimeOfDay == TimeSpan.Zero ? to.Date : to.Date.AddDays(1);
        return (new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Unspecified), TimeSpan.Zero).ToUnixTimeMilliseconds(),
            new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Unspecified), TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    // 快照按对方本地日聚合；用声明偏移还原日历标签并映射到查看方日界。
    // 取最近午夜容忍快照当前偏移与历史夏令时偏移的一小时差。
    private static long CalendarDayToLocal(long dayMs) => new DateTimeOffset(
        DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeMilliseconds(dayMs).UtcDateTime.Date,
            DateTimeKind.Unspecified)).ToUnixTimeMilliseconds();

    private static string DeviceFilter(
        IReadOnlyList<PeerDeviceRow> devices,
        SqliteCommand command)
    {
        var parameters = new List<string>(devices.Count);
        for (var index = 0; index < devices.Count; index++)
        {
            var name = $"$device{index}";
            command.Parameters.AddWithValue(name, devices[index].DeviceId);
            parameters.Add(name);
        }
        return $"device_id IN ({string.Join(", ", parameters)})";
    }
}
