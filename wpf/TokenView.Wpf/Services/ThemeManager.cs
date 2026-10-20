using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using TokenView.Wpf.Settings;

namespace TokenView.Wpf.Services;

/// <summary>运行时主题：直接改写共享画刷，已有 StaticResource 引用会同步刷新。</summary>
public static class ThemeManager
{
    private sealed record Palette(
        string Window, string Card, string Border, string Text, string Muted,
        string Accent, string AccentDark, string AccentSoft, string Hover,
        string Track, string Chip, string RowHover, string Sidebar, string Danger, string ChartGrid);

    private static readonly Palette Light = new(
        "#F6F8FA", "#FFFFFF", "#DEE5ED", "#202936", "#687589",
        "#099F83", "#087961", "#E6F5EF", "#EEF2F5",
        "#EDF1F5", "#EEF2F5", "#E3EAF2", "#F8FAFB", "#C94343", "#DEE5ED");

    private static readonly Palette Dark = new(
        "#0F1318", "#161C23", "#2A333E", "#E6EDF3", "#93A1B3",
        "#2BB39A", "#7FE6CD", "#183B34", "#1D252E",
        "#232C36", "#202832", "#28323D", "#12171D", "#F07178", "#2A333E");

    public static bool IsDark { get; private set; }

    public static void Apply(AppTheme theme)
    {
        IsDark = theme == AppTheme.Dark ||
                 (theme == AppTheme.System && SystemPrefersDark());
        ApplyPalette(IsDark ? Dark : Light);
        WindowTheme.RefreshAll();
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int number && number == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyPalette(Palette palette)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        Set(resources, "WindowBrush", palette.Window);
        Set(resources, "CardBrush", palette.Card);
        Set(resources, "BorderBrush", palette.Border);
        Set(resources, "TextBrush", palette.Text);
        Set(resources, "MutedBrush", palette.Muted);
        Set(resources, "AccentBrush", palette.Accent);
        Set(resources, "AccentDarkBrush", palette.AccentDark);
        Set(resources, "AccentSoftBrush", palette.AccentSoft);
        Set(resources, "HoverBrush", palette.Hover);
        Set(resources, "TrackBrush", palette.Track);
        Set(resources, "ChipBrush", palette.Chip);
        Set(resources, "RowHoverBrush", palette.RowHover);
        Set(resources, "SidebarBrush", palette.Sidebar);
        Set(resources, "DangerBrush", palette.Danger);
        Set(resources, "ChartGridBrush", palette.ChartGrid);
    }

    private static void Set(ResourceDictionary resources, string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        resources[key] = brush;
    }
}
