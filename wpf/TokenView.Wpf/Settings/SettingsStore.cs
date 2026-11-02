using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenView.Wpf.Settings;

public static class SettingsStore
{
    private sealed class Document
    {
        public int SchemaVersion { get; set; } = 1;
        public AppSettings Settings { get; set; } = new();
        public long? LastUpdateCheckMs { get; set; }
        public string? LastReadAnnouncementHash { get; set; }
    }

    private static readonly object WriteGate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static AppSettings Load() => LoadFrom(StoragePaths.Directory);

    public static AppSettings LoadFrom(string directory)
    {
        var document = ReadDocument(System.IO.Path.Combine(directory, "settings.json"));
        if (document is null || document.SchemaVersion != 1)
        {
            return new AppSettings();
        }
        return Normalize(document.Settings);
    }

    public static long? LastUpdateCheckMs()
    {
        return ReadDocument()?.LastUpdateCheckMs;
    }

    public static string? LastReadAnnouncementHash() =>
        ReadDocument()?.LastReadAnnouncementHash;

    internal static string? LastReadAnnouncementHashFrom(string settingsPath) =>
        ReadDocument(settingsPath)?.LastReadAnnouncementHash;

    public static void Save(AppSettings settings) => SaveTo(StoragePaths.Directory, settings);

    public static void SaveTo(string directory, AppSettings settings)
    {
        lock (WriteGate)
        {
            var existing = ReadDocument();
            var normalized = Normalize(settings);
            if (!Guid.TryParse(normalized.DeviceId, out _) &&
                existing is not null &&
                Guid.TryParse(existing.Settings.DeviceId, out _))
            {
                // 主题等即时保存可能发生在设备身份生成之前；不要把已有 ID 覆盖成空。
                normalized.DeviceId = existing.Settings.DeviceId;
            }
            var document = new Document
            {
                SchemaVersion = 1,
                Settings = normalized,
                LastUpdateCheckMs = existing?.LastUpdateCheckMs,
                LastReadAnnouncementHash = existing?.LastReadAnnouncementHash,
            };
            WriteDocument(Path.Combine(directory, "settings.json"), document);
        }
    }

    public static void SaveLastUpdateCheck(long value)
    {
        lock (WriteGate)
        {
            var existing = ReadDocument(StoragePaths.SettingsPath) ?? new Document();
            existing.SchemaVersion = 1;
            existing.LastUpdateCheckMs = value;
            WriteDocument(StoragePaths.SettingsPath, existing);
        }
    }

    public static void SaveLastReadAnnouncementHash(string? hash)
    {
        SaveLastReadAnnouncementHashTo(StoragePaths.SettingsPath, hash);
    }

    internal static void SaveLastReadAnnouncementHashTo(string settingsPath, string? hash)
    {
        lock (WriteGate)
        {
            var document = ReadDocument(settingsPath) ?? new Document();
            document.SchemaVersion = 1;
            document.LastReadAnnouncementHash = hash;
            WriteDocument(settingsPath, document);
        }
    }

    private static void WriteDocument(string path, Document document)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
        File.Move(temporary, path, true);
    }

    private static Document? ReadDocument() =>
        ReadDocument(StoragePaths.SettingsPath);

    private static Document? ReadDocument(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<Document>(
                File.ReadAllText(path), Options);
        }
        catch
        {
            return null;
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        var normalized = settings.Clone();
        if (!new[] { 0, 1, 5, 15, 30 }.Contains(normalized.SyncIntervalMinutes))
        {
            normalized.SyncIntervalMinutes = 5;
        }
        if (!new[] { "today", "7d", "week", "month", "30d", "90d", "all" }
                .Contains(normalized.DefaultRange))
        {
            normalized.DefaultRange = "7d";
        }
        normalized.EnabledClients = normalized.EnabledClients
            .Where(AppSettings.AllClients.Contains)
            .Distinct()
            .ToList();
        normalized.DeviceName = NormalizeDeviceName(normalized.DeviceName);
        if (!string.IsNullOrWhiteSpace(normalized.DeviceId) &&
            !Guid.TryParse(normalized.DeviceId, out _))
        {
            normalized.DeviceId = "";
        }
        return normalized;
    }

    private static string NormalizeDeviceName(string? value)
    {
        var cleaned = new string((value ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = string.IsNullOrWhiteSpace(Environment.MachineName)
                ? "TokenView"
                : Environment.MachineName;
        }
        return cleaned.Length <= 64 ? cleaned : cleaned[..64];
    }
}
