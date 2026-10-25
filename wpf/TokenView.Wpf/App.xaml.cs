using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using TokenView.Wpf.Data;
using TokenView.Wpf.Services;
using TokenView.Wpf.Settings;

namespace TokenView.Wpf;

public partial class App : Application
{
    private const string RestartWaitArgument = "--restart-wait";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private DispatcherTimer? _announcementTimer;
    private CancellationTokenSource? _announcementCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WaitForRestartTarget(e.Args);
        _singleInstance = new Mutex(true, @"Local\TokenView.Wpf.SingleInstance", out var created);
        if (!created)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(@"Local\TokenView.Wpf.Activate", out var existing))
                {
                    existing.Set();
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // 无法唤醒已有实例时仍退出，避免两个进程同时写库。
            }
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
            @"Local\TokenView.Wpf.Activate");
        var listener = new Thread(() =>
        {
            while (_activateEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.RestoreFromTray());
            }
        })
        {
            IsBackground = true,
            Name = "TokenView single-instance listener",
        };
        listener.Start();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        LegacyStorageMigration.TryMigrate();
        System.IO.Directory.CreateDirectory(StoragePaths.Directory);
        var settings = SettingsStore.Load();
        _ = SyncService.Current.PrepareQoderRecordingAsync(settings.EnabledClients);
        ThemeManager.Apply(settings.Theme);
        if (settings.Theme == AppTheme.System)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
            {
                if (args.Category == Microsoft.Win32.UserPreferenceCategory.General)
                {
                    Dispatcher.BeginInvoke(() => ThemeManager.Apply(AppTheme.System));
                }
            };
        }
        AutostartService.Apply(settings);
        TokenFormatting.Display = settings.TokenDisplay;

        var window = new MainWindow();
        MainWindow = window;
        if (settings.RememberWindow)
        {
            WindowStateStore.Restore(window);
        }
        if (e.Args.Contains("--hidden", StringComparer.OrdinalIgnoreCase) && settings.AutostartHidden)
        {
            window.StartHiddenInTray();
        }
        else
        {
            window.Show();
        }
        StartAnnouncementRefresh();
    }

    private void StartAnnouncementRefresh()
    {
        _announcementCts = new CancellationTokenSource();
        _announcementTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        _announcementTimer.Tick += (_, _) => _ = RefreshAnnouncementsAsync();
        _announcementTimer.Start();
        _ = RefreshAnnouncementsAsync();
    }

    private async Task RefreshAnnouncementsAsync()
    {
        var token = _announcementCts?.Token ?? CancellationToken.None;
        try
        {
            await AnnouncementService.Current.RefreshAsync(token);
        }
        catch (OperationCanceledException)
        {
            // 应用退出时取消在途公告请求。
        }
    }

    /// <summary>等待正在进行的采集写入完成后退出。</summary>
    public async void ExitApplication()
    {
        _announcementTimer?.Stop();
        _announcementCts?.Cancel();
        var running = SyncService.Current.CurrentTask;
        if (running is not null)
        {
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(30)));
        }
        if (MainWindow is MainWindow window && window.IsLoaded)
        {
            window.ForceCloseForExit();
        }
        Shutdown();
    }

    /// <summary>启动新实例等待本进程退出后接管，再按正常流程关闭当前实例。</summary>
    public void RestartApplication()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) ||
            string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("当前运行方式不支持自动重启，请关闭后重新打开 TokenView。",
                "无法重启", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add(RestartWaitArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            MessageBox.Show($"无法启动新的 TokenView 实例：{error.Message}\n当前实例将继续运行。",
                "重启失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExitApplication();
    }

    internal static int? ParseRestartPid(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], RestartWaitArgument, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) &&
                pid > 0)
            {
                return pid;
            }
        }
        return null;
    }

    private static void WaitForRestartTarget(string[] args)
    {
        if (ParseRestartPid(args) is not { } processId)
        {
            return;
        }
        try
        {
            using var previous = Process.GetProcessById(processId);
            previous.WaitForExit(60_000);
        }
        catch (ArgumentException)
        {
            // 旧实例已经退出，直接继续启动。
        }
    }
}
