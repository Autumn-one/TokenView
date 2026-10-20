using System.IO;
using System.Text.Json;

namespace TokenView.Wpf;

public static class StoragePaths
{
    public const string DirectoryName = "com.tokenview.app";
    public const string DatabaseFileName = "tokenview-usage.sqlite3";
    public const string SettingsFileName = "settings.json";
    public const string LocatorFileName = "storage-location.json";

    internal const string LegacyDirectoryName = "com.toklens.app";
    internal const string LegacyDatabaseFileName = "toklens-usage.sqlite3";

    public static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        DirectoryName);

    internal static string LegacyAppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        LegacyDirectoryName);

    public static string Directory => ResolveDirectory();

    public static string DatabasePath
    {
        get
        {
            var directory = Directory;
            var current = Path.Combine(directory, DatabaseFileName);
            if (File.Exists(current))
            {
                return current;
            }
            var legacy = Path.Combine(directory, LegacyDatabaseFileName);
            return File.Exists(legacy) ? legacy : current;
        }
    }

    public static string SettingsPath => Path.Combine(Directory, SettingsFileName);

    public static string LocatorPath => Path.Combine(AppDataDirectory, LocatorFileName);

    private sealed record Location(int SchemaVersion, string Directory);

    internal static bool HasDatabase(string directory) =>
        File.Exists(Path.Combine(directory, DatabaseFileName)) ||
        File.Exists(Path.Combine(directory, LegacyDatabaseFileName));

    private static bool HasData(string directory) =>
        HasDatabase(directory) || File.Exists(Path.Combine(directory, SettingsFileName));

    private static string ResolveDirectory()
    {
        if (File.Exists(LocatorPath))
        {
            return ResolveLocator();
        }
        if (HasData(AppDataDirectory))
        {
            return AppDataDirectory;
        }
        // 迁移失败或尚未迁移时继续使用旧目录，避免把既有统计显示为空。
        if (HasData(LegacyAppDataDirectory))
        {
            return LegacyAppDataDirectory;
        }
        return AppDataDirectory;
    }

    private static string ResolveLocator()
    {
        try
        {
            var location = JsonSerializer.Deserialize<Location>(File.ReadAllText(LocatorPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (location is null || location.SchemaVersion != 1 || !Path.IsPathFullyQualified(location.Directory))
            {
                throw new InvalidOperationException("存储定位文件无效，请恢复配置的存储目录后重新启动 TokenView。");
            }
            if (!System.IO.Directory.Exists(location.Directory) || !HasDatabase(location.Directory))
            {
                throw new InvalidOperationException("配置的存储目录不可用，请恢复该目录后重新启动 TokenView。");
            }
            return location.Directory;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("存储定位文件损坏，请恢复配置的存储目录后重新启动 TokenView。");
        }
    }
}
