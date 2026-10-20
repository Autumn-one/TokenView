using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TokenView.Wpf.Services;

/// <summary>让原生标题栏跟随应用主题（Windows 10 20H1+）。</summary>
public static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyHandle(window);
            return;
        }
        window.SourceInitialized += (_, _) => ApplyHandle(window);
        window.Loaded += (_, _) =>
        {
            ApplyHandle(window);
            window.Dispatcher.BeginInvoke(() => ApplyHandle(window),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        window.Activated += (_, _) => ApplyHandle(window);
    }

    public static void RefreshAll()
    {
        if (Application.Current is null) return;
        foreach (Window window in Application.Current.Windows)
        {
            ApplyHandle(window);
        }
    }

    private static void ApplyHandle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var value = ThemeManager.IsDark ? 1 : 0;
        var result = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        if (result != 0)
        {
            result = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
        }
        // COLORREF is 0x00BBGGRR. 显式着色覆盖部分系统不响应深色模式的情况。
        var caption = ThemeManager.IsDark ? 0x0018130F : 0x00FAF8F6;
        var text = ThemeManager.IsDark ? 0x00F3EDE6 : 0x00362920;
        DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
    }
}
