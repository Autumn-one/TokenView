using System.IO;
using System.Text.Json;
using System.Windows;

namespace TokenView.Wpf.Services;

/// <summary>记住窗口尺寸与位置（独立于设置文件，不改变设置结构）。</summary>
public static class WindowStateStore
{
    private sealed class State
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static string PathName => System.IO.Path.Combine(StoragePaths.AppDataDirectory, "window-state.json");

    public static void Restore(Window window)
    {
        try
        {
            if (!File.Exists(PathName)) return;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(PathName));
            if (state is null || state.Width < 800 || state.Height < 600) return;
            var area = SystemParameters.WorkArea;
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
            if (state.Left + 80 < virtualLeft || state.Left > virtualRight - 80 ||
                state.Top + 40 < virtualTop || state.Top > virtualBottom - 40)
            {
                return;
            }
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = Math.Clamp(state.Left, virtualLeft, Math.Max(virtualLeft, virtualRight - 240));
            window.Top = Math.Clamp(state.Top, virtualTop, Math.Max(virtualTop, virtualBottom - 120));
            window.Width = Math.Min(state.Width, area.Width);
            window.Height = Math.Min(state.Height, area.Height);
            if (state.Maximized)
            {
                window.WindowState = WindowState.Maximized;
            }
        }
        catch
        {
            // 损坏的状态文件按默认窗口打开。
        }
    }

    public static void Save(Window window)
    {
        try
        {
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;
            var state = new State
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = window.WindowState == WindowState.Maximized,
            };
            System.IO.Directory.CreateDirectory(StoragePaths.AppDataDirectory);
            File.WriteAllText(PathName, JsonSerializer.Serialize(state));
        }
        catch
        {
            // 保存失败不影响关闭。
        }
    }
}
