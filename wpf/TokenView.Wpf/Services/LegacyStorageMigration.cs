using System.IO;
using System.Text.Json;

namespace TokenView.Wpf.Services;

/// <summary>
/// 把 TokLens 时代的自有数据一次性迁到 TokenView 命名的目录与库文件。
/// 只复制、不删除旧文件；失败时保留旧布局，由 StoragePaths 回退。
/// </summary>
public static class LegacyStorageMigration
{
    private const string WindowStateFileName = "window-state.json";

    // 主库最后处理：只要新主库出现，就说明旁边的 WAL/SHM 已经迁移完毕。
    private static readonly string[] DatabaseSuffixes = ["-wal", "-shm", "-journal", ""];

    private sealed record Location(int SchemaVersion, string Directory);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryMigrate() =>
        TryMigrate(StoragePaths.LegacyAppDataDirectory, StoragePaths.AppDataDirectory);

    internal static bool TryMigrate(string legacyRoot, string targetRoot)
    {
        try
        {
            if (!System.IO.Directory.Exists(legacyRoot))
            {
                return true;
            }
            // 新库已存在说明迁移完成；只复制缺少的文件，允许上次中断后重试。
            if (File.Exists(Path.Combine(targetRoot, StoragePaths.DatabaseFileName)))
            {
                return true;
            }

            System.IO.Directory.CreateDirectory(targetRoot);
            var legacyStorage = ResolveLegacyStorage(legacyRoot);
            if (!string.Equals(legacyStorage, legacyRoot, StringComparison.OrdinalIgnoreCase))
            {
                // 自定义存储目录：库与设置留在原处，只把库文件改名为新名。
                MoveDatabase(legacyStorage);
                CopyIfMissing(Path.Combine(legacyRoot, StoragePaths.LocatorFileName),
                    Path.Combine(targetRoot, StoragePaths.LocatorFileName));
                CopyIfMissing(Path.Combine(legacyRoot, WindowStateFileName),
                    Path.Combine(targetRoot, WindowStateFileName));
                return true;
            }

            CopyIfMissing(Path.Combine(legacyRoot, StoragePaths.SettingsFileName),
                Path.Combine(targetRoot, StoragePaths.SettingsFileName));
            CopyIfMissing(Path.Combine(legacyRoot, WindowStateFileName),
                Path.Combine(targetRoot, WindowStateFileName));
            foreach (var suffix in DatabaseSuffixes)
            {
                CopyIfMissing(
                    Path.Combine(legacyRoot, StoragePaths.LegacyDatabaseFileName + suffix),
                    Path.Combine(targetRoot, StoragePaths.DatabaseFileName + suffix));
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string ResolveLegacyStorage(string legacyRoot)
    {
        var locator = Path.Combine(legacyRoot, StoragePaths.LocatorFileName);
        if (!File.Exists(locator))
        {
            return legacyRoot;
        }
        try
        {
            var location = JsonSerializer.Deserialize<Location>(File.ReadAllText(locator), JsonOptions);
            if (location is { SchemaVersion: 1 } &&
                Path.IsPathFullyQualified(location.Directory) &&
                System.IO.Directory.Exists(location.Directory) &&
                StoragePaths.HasDatabase(location.Directory))
            {
                return location.Directory;
            }
        }
        catch (JsonException)
        {
            // 损坏的旧定位文件按默认布局迁移。
        }
        return legacyRoot;
    }

    private static void MoveDatabase(string directory)
    {
        foreach (var suffix in DatabaseSuffixes)
        {
            var source = Path.Combine(directory, StoragePaths.LegacyDatabaseFileName + suffix);
            var target = Path.Combine(directory, StoragePaths.DatabaseFileName + suffix);
            if (File.Exists(source) && !File.Exists(target))
            {
                File.Move(source, target);
            }
        }
    }

    private static void CopyIfMissing(string source, string target)
    {
        if (File.Exists(source) && !File.Exists(target))
        {
            File.Copy(source, target);
        }
    }
}
