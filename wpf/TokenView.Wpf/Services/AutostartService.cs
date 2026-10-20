using System.IO;
using Microsoft.Win32;
using TokenView.Wpf.Settings;

namespace TokenView.Wpf.Services;

/// <summary>通过 HKCU Run 键实现开机自启，路径始终指向当前可执行文件。</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TokenView";
    private const string LegacyValueName = "TokLens";

    public static void Apply(AppSettings settings)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            if (!settings.Autostart)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) return;
            var command = $"\"{executable}\"" + (settings.AutostartHidden ? " --hidden" : "");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // 自启失败不应阻断启动；设置页仍保存用户选择。
        }
    }
}
