using Microsoft.Data.Sqlite;

namespace TokenView.Wpf.Data;

/// <summary>统一的只读连接配置；查询与 peer 导出共用，避免复制 PRAGMA。</summary>
internal static class SqliteReadConnection
{
    public static SqliteConnection Open(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
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
}
