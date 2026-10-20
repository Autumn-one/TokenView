using System.IO;
using System.Text.Json;
using TokenView.Wpf.Settings;
using Xunit;

namespace TokenView.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void LastReadHashRoundTripsAndPreservesOtherDocumentFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tokenview-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path,
                """{"schemaVersion":1,"settings":{},"lastUpdateCheckMs":123}""");

            SettingsStore.SaveLastReadAnnouncementHashTo(path, "hash-1");

            Assert.Equal("hash-1", SettingsStore.LastReadAnnouncementHashFrom(path));
            using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.Equal(123, document.RootElement.GetProperty("lastUpdateCheckMs").GetInt64());
            }

            SettingsStore.SaveLastReadAnnouncementHashTo(path, null);
            Assert.Null(SettingsStore.LastReadAnnouncementHashFrom(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
