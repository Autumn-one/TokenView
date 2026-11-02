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
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT device_id, {timeColumn}, client, model, requests, requests_with_usage,
                   requests_without_usage, tokens, estimated_tokens, estimated_requests
            FROM {table}
            WHERE {DeviceFilter(devices, command)}
              AND {timeColumn} >= $from AND {timeColumn} < $to
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
                reader.GetInt64(1),
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
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT device_id, {timeColumn}, client, sessions
            FROM {table}
            WHERE {DeviceFilter(devices, command)}
              AND {timeColumn} >= $from AND {timeColumn} < $to
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
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }
        return rows;
    }

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
