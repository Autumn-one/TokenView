using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data.Collectors;
using TokenView.Wpf.Settings;

namespace TokenView.Wpf.Services;

public sealed record RelocationResult(string Directory, string? Warning);

/// <summary>把 TokenView 自有数据库与设置迁移到用户目录，并写入 storage-location.json。</summary>
public static class StorageRelocationService
{
    private const string DatabaseName = StoragePaths.DatabaseFileName;

    private static readonly string[] OwnFiles =
    [
        DatabaseName, DatabaseName + "-wal", DatabaseName + "-shm", DatabaseName + "-journal",
        StoragePaths.LegacyDatabaseFileName, StoragePaths.LegacyDatabaseFileName + "-wal",
        StoragePaths.LegacyDatabaseFileName + "-shm", StoragePaths.LegacyDatabaseFileName + "-journal",
        "settings.json", "settings.json.tmp",
    ];

    public static RelocationResult Relocate(string requestedTarget) =>
        Relocate(requestedTarget, StoragePaths.Directory, StoragePaths.LocatorPath);

    public static RelocationResult Relocate(string requestedTarget, string currentDirectory, string locatorPath)
    {
        if (!Path.IsPathFullyQualified(requestedTarget) ||
            requestedTarget.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("请选择不包含上级路径跳转的绝对目录");
        }

        var target = Path.GetFullPath(requestedTarget);
        var current = Path.GetFullPath(currentDirectory);
        if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
        {
            return new RelocationResult(current, null);
        }
        ValidateTarget(target);
        System.IO.Directory.CreateDirectory(target);

        foreach (var name in OwnFiles)
        {
            if (File.Exists(Path.Combine(target, name)))
            {
                throw new InvalidOperationException("目标目录已有 TokenView 存储文件，请选择其他目录");
            }
        }

        var targetDatabase = Path.Combine(target, DatabaseName);
        try
        {
            SnapshotDatabase(SourceDatabase(current), targetDatabase);
            using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = targetDatabase,
                       Mode = SqliteOpenMode.ReadOnly,
                       Pooling = false,
                   }.ToString()))
            {
                snapshot.Open();
                using var check = snapshot.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                if (check.ExecuteScalar() as string != "ok")
                {
                    throw new InvalidOperationException("数据库快照完整性校验失败");
                }
            }

            var settings = SettingsStore.LoadFrom(current);
            SettingsStore.SaveTo(target, settings);
            SaveLocator(locatorPath, target);
        }
        catch
        {
            foreach (var name in OwnFiles)
            {
                try
                {
                    File.Delete(Path.Combine(target, name));
                }
                catch (IOException)
                {
                    // 保留可读错误；清理失败不覆盖原始异常。
                }
            }
            throw;
        }

        var warning = RemoveOwned(current);
        return new RelocationResult(target, warning);
    }

    private static void ValidateTarget(string target)
    {
        foreach (var collector in SyncEngine.Collectors().Values)
        {
            foreach (var source in collector.SourcePaths)
            {
                var directory = File.Exists(source) ? Path.GetDirectoryName(source) : source;
                if (directory is null) continue;
                var resolved = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var candidate = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (candidate.Equals(resolved, StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    resolved.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("存储目录与客户端原始数据目录重叠，请选择其他目录");
                }
            }
        }
    }

    private static string SourceDatabase(string directory)
    {
        var current = Path.Combine(directory, DatabaseName);
        if (File.Exists(current))
        {
            return current;
        }
        var legacy = Path.Combine(directory, StoragePaths.LegacyDatabaseFileName);
        return File.Exists(legacy) ? legacy : current;
    }

    private static void SnapshotDatabase(string source, string target)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", target);
        command.ExecuteNonQuery();
    }

    private static void SaveLocator(string locatorPath, string directory)
    {
        var temporary = locatorPath + ".tmp";
        var json = JsonSerializer.Serialize(new { schemaVersion = 1, directory });
        File.WriteAllText(temporary, json);
        File.Move(temporary, locatorPath, true);
    }

    private static string? RemoveOwned(string directory)
    {
        var failed = false;
        foreach (var name in OwnFiles)
        {
            try
            {
                File.Delete(Path.Combine(directory, name));
            }
            catch (FileNotFoundException)
            {
            }
            catch (IOException)
            {
                failed = true;
            }
            catch (UnauthorizedAccessException)
            {
                failed = true;
            }
        }
        return failed ? "存储迁移成功，但部分旧 TokenView 文件未能删除，请手动检查旧目录" : null;
    }
}
