using System.IO;
using System.Windows;
using System.Windows.Threading;
using TokenView.Wpf.Data;
using TokenView.Wpf.Services;
using TokenView.Wpf.Settings;

namespace TokenView.Wpf;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private DispatcherTimer? _announcementTimer;
    private CancellationTokenSource? _announcementCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
}
