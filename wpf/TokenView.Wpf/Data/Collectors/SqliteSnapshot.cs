using System.IO;
using Microsoft.Data.Sqlite;

namespace TokenView.Wpf.Data.Collectors;

public readonly record struct FileSignature(long Size, long MtimeMs);

public readonly record struct SqliteSignature(FileSignature Main, FileSignature? Wal);

/// <summary>
/// 只读 SQLite 快照：把主库与 WAL 复制到临时库后打开，绝不打开客户端原库。
/// 使用流式复制，支持超过 2 GB 的客户端数据库。
/// </summary>
public sealed class SqliteSnapshot : IDisposable
{
    private readonly string _path;
    private readonly SqliteConnection _connection;

    private SqliteSnapshot(string path, SqliteConnection connection)
    {
        _path = path;
        _connection = connection;
    }

    public SqliteConnection Connection => _connection;

    public static SqliteSignature Signature(string path)
    {
        var main = Fingerprint(path) ?? throw new FileNotFoundException("source database missing", path);
        var wal = Fingerprint(path + "-wal");
        return new SqliteSignature(main, wal);
    }

    private static FileSignature? Fingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return new FileSignature(info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public static SqliteSnapshot Open(string path)
    {
        var before = Signature(path);
        var temp = Path.Combine(Path.GetTempPath(), $"tokenview-{Guid.NewGuid():N}.sqlite");
        var moved = new List<string> { temp, temp + "-wal", temp + "-shm", temp + "-journal" };
        try
        {
            ValidateHeader(path);
            CopyShared(path, temp);
            if (before.Wal is not null) CopyShared(path + "-wal", temp + "-wal");
            if (File.Exists(path + "-journal")) CopyShared(path + "-journal", temp + "-journal");
            if (before != Signature(path))
            {
                throw SyncException.ChangedDuringScan();
            }

            // 临时副本允许 SQLite 回放 WAL；query_only 保证不会改动快照内容。
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temp,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 5000;";
            command.ExecuteNonQuery();
            return new SqliteSnapshot(temp, connection);
        }
        catch (SqliteException error)
        {
            DeleteQuietly(moved);
            throw SyncException.Database(error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(moved);
            throw SyncException.FromSource(error);
        }
        catch
        {
            DeleteQuietly(moved);
            throw;
        }
    }

    private static void ValidateHeader(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var read = stream.Read(header);
        if (read < 16 || !header.SequenceEqual("SQLite format 3\0"u8))
        {
            throw SyncException.FromSource(new InvalidDataException("invalid SQLite header"));
        }
    }

    private static void CopyShared(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static void DeleteQuietly(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 临时文件残留不影响正确性，系统稍后会清理 TEMP。
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _connection.Dispose();
        }
        finally
        {
            DeleteQuietly([_path, _path + "-wal", _path + "-shm", _path + "-journal"]);
        }
    }
}
