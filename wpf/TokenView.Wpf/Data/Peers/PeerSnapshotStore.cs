using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Peers;

/// <summary>把局域网收到的聚合快照写入独立的 peer_* 表；不触碰本机统计表。</summary>
public static class PeerSnapshotStore
{
    public static PeerImportResult Import(
        string databasePath,
        PeerSnapshotDocument document,
        bool force = false,
        TimeProvider? clock = null)
    {
        PeerSnapshotSerializer.Validate(document);
        var importedAtMs = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        using var store = UsageStore.Open(databasePath);
        return store.ExecuteInTransaction((connection, transaction) =>
        {
            var existing = ScalarLong(connection, transaction,
                "SELECT generated_at_ms FROM peer_devices WHERE device_id = $device",
                ("$device", document.DeviceId));
            if (!force && existing is { } previous && previous >= document.GeneratedAtMs)
            {
                return new PeerImportResult(false, true, document.DeviceId, document.DeviceName,
                    document.RequestDaily.Count, document.RequestHourly.Count,
                    document.SessionDaily.Count, document.SessionHourly.Count);
            }

            Execute(connection, transaction,
                "DELETE FROM peer_devices WHERE device_id = $device",
                ("$device", document.DeviceId));
            Execute(connection, transaction, """
                INSERT INTO peer_devices
                    (device_id, name, generated_at_ms, imported_at_ms, schema_version,
                     utc_offset_minutes, app_version, sessions_partial)
                VALUES ($device, $name, $generated, $imported, $schema, $offset, $version, $partial);
                """,
                ("$device", document.DeviceId),
                ("$name", document.DeviceName),
                ("$generated", document.GeneratedAtMs),
                ("$imported", importedAtMs),
                ("$schema", document.SchemaVersion),
                ("$offset", document.UtcOffsetMinutes),
                ("$version", document.AppVersion),
                ("$partial", document.SessionsPartial));

            InsertRequestRows(connection, transaction, "peer_request_daily", "day_ms",
                document.DeviceId, document.RequestDaily);
            InsertRequestRows(connection, transaction, "peer_request_hourly", "hour_ms",
                document.DeviceId, document.RequestHourly);
            InsertSessionRows(connection, transaction, "peer_session_daily", "day_ms",
                document.DeviceId, document.SessionDaily);
            InsertSessionRows(connection, transaction, "peer_session_hourly", "hour_ms",
                document.DeviceId, document.SessionHourly);

            return new PeerImportResult(true, false, document.DeviceId, document.DeviceName,
                document.RequestDaily.Count, document.RequestHourly.Count,
                document.SessionDaily.Count, document.SessionHourly.Count);
        });
    }

    public static int Remove(string databasePath, string deviceId)
    {
        if (!File.Exists(databasePath))
        {
            return 0;
        }
        using var store = UsageStore.Open(databasePath);
        return store.ExecuteInTransaction((connection, transaction) =>
            Execute(connection, transaction,
                "DELETE FROM peer_devices WHERE device_id = $device",
                ("$device", deviceId)));
    }

    public static int RemoveAll(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return 0;
        }
        using var store = UsageStore.Open(databasePath);
        return store.ExecuteInTransaction((connection, transaction) =>
            Execute(connection, transaction, "DELETE FROM peer_devices;"));
    }

    public static List<PeerDeviceRow> ListDevices(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return [];
        }
        using var connection = SqliteReadConnection.Open(databasePath);
        if (!TableExists(connection))
        {
            return [];
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, name, generated_at_ms, imported_at_ms, schema_version,
                   utc_offset_minutes, app_version, sessions_partial
            FROM peer_devices ORDER BY name COLLATE NOCASE, device_id;
            """;
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

    internal static bool TableExists(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'peer_devices' LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    private static void InsertRequestRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string timeColumn,
        string deviceId,
        IReadOnlyList<PeerRequestRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {table}
                (device_id, {timeColumn}, client, model, requests, requests_with_usage,
                 requests_without_usage, tokens, estimated_tokens, estimated_requests)
            VALUES ($device, $time, $client, $model, $requests, $with_usage,
                    $without_usage, $tokens, $estimated_tokens, $estimated_requests);
            """;
        var device = command.Parameters.Add("$device", SqliteType.Text);
        var time = command.Parameters.Add("$time", SqliteType.Integer);
        var client = command.Parameters.Add("$client", SqliteType.Text);
        var model = command.Parameters.Add("$model", SqliteType.Text);
        var requests = command.Parameters.Add("$requests", SqliteType.Integer);
        var withUsage = command.Parameters.Add("$with_usage", SqliteType.Integer);
        var withoutUsage = command.Parameters.Add("$without_usage", SqliteType.Integer);
        var tokens = command.Parameters.Add("$tokens", SqliteType.Integer);
        var estimatedTokens = command.Parameters.Add("$estimated_tokens", SqliteType.Integer);
        var estimatedRequests = command.Parameters.Add("$estimated_requests", SqliteType.Integer);
        command.Prepare();
        device.Value = deviceId;
        foreach (var row in rows)
        {
            time.Value = row.TimeMs;
            client.Value = row.Client;
            model.Value = row.Model;
            requests.Value = row.Requests;
            withUsage.Value = row.RequestsWithUsage;
            withoutUsage.Value = row.RequestsWithoutUsage;
            tokens.Value = row.Tokens;
            estimatedTokens.Value = row.EstimatedTokens;
            estimatedRequests.Value = row.EstimatedRequests;
            command.ExecuteNonQuery();
        }
    }

    private static void InsertSessionRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string timeColumn,
        string deviceId,
        IReadOnlyList<PeerSessionRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {table}
                (device_id, {timeColumn}, client, sessions, estimated_sessions)
            VALUES ($device, $time, $client, $sessions, $estimated_sessions);
            """;
        var device = command.Parameters.Add("$device", SqliteType.Text);
        var time = command.Parameters.Add("$time", SqliteType.Integer);
        var client = command.Parameters.Add("$client", SqliteType.Text);
        var sessions = command.Parameters.Add("$sessions", SqliteType.Integer);
        var estimated = command.Parameters.Add("$estimated_sessions", SqliteType.Integer);
        command.Prepare();
        device.Value = deviceId;
        foreach (var row in rows)
        {
            time.Value = row.TimeMs;
            client.Value = row.Client;
            sessions.Value = row.Sessions;
            estimated.Value = row.EstimatedSessions;
            command.ExecuteNonQuery();
        }
    }

    private static long? ScalarLong(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        var result = command.ExecuteScalar();
        return result is null or DBNull
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command.ExecuteNonQuery();
    }
}
