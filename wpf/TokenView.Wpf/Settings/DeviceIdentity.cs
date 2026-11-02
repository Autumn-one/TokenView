namespace TokenView.Wpf.Settings;

/// <summary>局域网设备标识只在首次启用/重置时生成并持久化。</summary>
public static class DeviceIdentity
{
    public static void Ensure(AppSettings settings)
    {
        if (Guid.TryParse(settings.DeviceId, out _))
        {
            return;
        }
        settings.DeviceId = Guid.NewGuid().ToString("N");
        SettingsStore.Save(settings);
    }

    public static string Reset(AppSettings settings)
    {
        settings.DeviceId = Guid.NewGuid().ToString("N");
        SettingsStore.Save(settings);
        return settings.DeviceId;
    }
}
