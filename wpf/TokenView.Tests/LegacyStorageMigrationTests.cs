using System.IO;
using System.Text.Json;
using TokenView.Wpf.Services;
using Xunit;

namespace TokenView.Tests;

public sealed class LegacyStorageMigrationTests
{
    [Fact]
    public void DefaultLayoutCopiesOwnFilesUnderNewNames()
    {
        var root = TempRoot();
        var legacy = Path.Combine(root, "com.toklens.app");
        var target = Path.Combine(root, "com.tokenview.app");
        Directory.CreateDirectory(legacy);
        try
        {
            File.WriteAllText(Path.Combine(legacy, "toklens-usage.sqlite3"), "db");
            File.WriteAllText(Path.Combine(legacy, "toklens-usage.sqlite3-wal"), "wal");
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "settings");
            File.WriteAllText(Path.Combine(legacy, "window-state.json"), "state");

            Assert.True(LegacyStorageMigration.TryMigrate(legacy, target));

            Assert.Equal("db", File.ReadAllText(Path.Combine(target, "tokenview-usage.sqlite3")));
            Assert.Equal("wal", File.ReadAllText(Path.Combine(target, "tokenview-usage.sqlite3-wal")));
            Assert.Equal("settings", File.ReadAllText(Path.Combine(target, "settings.json")));
            Assert.Equal("state", File.ReadAllText(Path.Combine(target, "window-state.json")));
            Assert.True(File.Exists(Path.Combine(legacy, "toklens-usage.sqlite3")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExistingTargetDatabaseIsNotOverwritten()
    {
        var root = TempRoot();
        var legacy = Path.Combine(root, "com.toklens.app");
        var target = Path.Combine(root, "com.tokenview.app");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(legacy, "toklens-usage.sqlite3"), "old");
            File.WriteAllText(Path.Combine(target, "tokenview-usage.sqlite3"), "new");

            Assert.True(LegacyStorageMigration.TryMigrate(legacy, target));

            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "tokenview-usage.sqlite3")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CustomStorageDirectoryKeepsLocationFileAndRenamesDatabaseInPlace()
    {
        var root = TempRoot();
        var legacy = Path.Combine(root, "com.toklens.app");
        var target = Path.Combine(root, "com.tokenview.app");
        var storage = Path.Combine(root, "custom-storage");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(storage);
        try
        {
            File.WriteAllText(Path.Combine(storage, "toklens-usage.sqlite3"), "db");
            File.WriteAllText(Path.Combine(storage, "settings.json"), "settings");
            File.WriteAllText(Path.Combine(legacy, "storage-location.json"),
                JsonSerializer.Serialize(new { schemaVersion = 1, directory = storage }));

            Assert.True(LegacyStorageMigration.TryMigrate(legacy, target));

            Assert.Equal("db", File.ReadAllText(Path.Combine(storage, "tokenview-usage.sqlite3")));
            Assert.False(File.Exists(Path.Combine(storage, "toklens-usage.sqlite3")));
            Assert.Equal("settings", File.ReadAllText(Path.Combine(storage, "settings.json")));
            Assert.True(File.Exists(Path.Combine(target, "storage-location.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MissingLegacyRootDoesNothing()
    {
        var root = TempRoot();
        try
        {
            Assert.True(LegacyStorageMigration.TryMigrate(
                Path.Combine(root, "missing"), Path.Combine(root, "target")));
            Assert.False(Directory.Exists(Path.Combine(root, "target")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"tokenview-tests-{Guid.NewGuid():N}");
}
