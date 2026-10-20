using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TokenView.Wpf.Controls;
using TokenView.Wpf.Data;
using TokenView.Wpf.Data.Collectors;
using TokenView.Wpf.Services;
using TokenView.Wpf.Settings;

namespace TokenView.Wpf;

public partial class MainWindow : Window
{
    private const double DailyStripMinPitch = 84;

    private string _rangeKey = "7d";
    private RangeSelection? _customRange;
    private ChartMetric _metric = ChartMetric.Tokens;
    private OverviewSnapshot? _snapshot;
    private readonly HashSet<string> _expandedClients = [];
    private bool _ready;
    private AppSettings _settings = new();
    private readonly SyncSchedule _schedule = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DispatcherTimer? _timer;
    private TrayService? _tray;
    private bool _forceClose;
    private bool _pendingExit;
    private readonly DispatcherTimer _skeletonTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _tooltipHideTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private bool _tooltipByModel;
    private ChartPoint? _hoveredPoint;
    private readonly System.Windows.Media.Animation.DoubleAnimation _skeletonPulse = new(0.35, 0.9,
        TimeSpan.FromSeconds(0.85))
    {
        AutoReverse = true,
        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
    };

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        _settings = SettingsStore.Load();
        _rangeKey = _settings.DefaultRange;
        SelectRange();
        SyncService.Current.Progress += OnSyncProgress;
        SyncService.Current.Completed += OnSyncCompleted;
        RecentSessionsGrid.PreviewKeyDown += RecentSessions_KeyDown;
        RangePicker.RangeConfirmed += RangePicker_RangeConfirmed;
        RangePicker.CancelRequested += RangePicker_CancelRequested;
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(Window_PreviewMouseDown), true);
        LocationChanged += (_, _) => RangePopup.IsOpen = false;
        StateChanged += (_, _) => RangePopup.IsOpen = false;
        _skeletonTimer.Tick += (_, _) =>
        {
            _skeletonTimer.Stop();
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingSkeleton.BeginAnimation(OpacityProperty, _skeletonPulse);
        };
        _tooltipHideTimer.Tick += (_, _) =>
        {
            _tooltipHideTimer.Stop();
            if (!ChartTooltip.IsMouseOver && !LineChart.IsMouseOver)
            {
                ChartTooltip.Visibility = Visibility.Collapsed;
            }
        };
        CreateTray();
        Closing += Window_Closing;
        _ready = true;
        Loaded += Main_Loaded;
        AnnouncementService.Current.Changed += OnAnnouncementChanged;
        UpdateAnnouncementBadge();
        Closed += (_, _) =>
        {
            _timer?.Stop();
            _tooltipHideTimer.Stop();
            SyncService.Current.Progress -= OnSyncProgress;
            SyncService.Current.Completed -= OnSyncCompleted;
            AnnouncementService.Current.Changed -= OnAnnouncementChanged;
            _tray?.Dispose();
        };
    }

    private void CreateTray()
    {
        _tray = new TrayService();
        _tray.ShowRequested += RestoreFromTray;
        _tray.SyncRequested += () =>
        {
            _settings = SettingsStore.Load();
            TriggerSync(false);
        };
        _tray.ExitRequested += () => ((App)Application.Current).ExitApplication();
        _tray.UpdateRequested += ApplyOrOpenPendingUpdate;
        _tray.SetRunning(SyncService.Current.IsRunning);
    }

    private void ApplyOrOpenPendingUpdate()
    {
        if (_pendingUpdate is not null)
        {
            try
            {
                UpdateInstaller.Launch(_pendingUpdate, () => ((App)Application.Current).ExitApplication());
                return;
            }
            catch
            {
                // 写入失败时退回下载页，避免用户没有可见反馈。
            }
        }
        var url = _pendingUpdateUrl ?? UpdateService.ReleasePage;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void StartHiddenInTray()
    {
        ShowInTaskbar = false;
        Show();
        Hide();
    }

    public void ForceCloseForExit()
    {
        if (_settings.RememberWindow)
        {
            WindowStateStore.Save(this);
        }
        _forceClose = true;
        Close();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    internal void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose)
        {
            if (_settings.RememberWindow)
            {
                WindowStateStore.Save(this);
            }
            return;
        }

        _settings = SettingsStore.Load();
        var behavior = _settings.CloseBehavior;
        if (behavior == CloseBehavior.Ask)
        {
            var dialog = CloseWindowDialog.Show(this);
            if (dialog.Choice == CloseChoice.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (dialog.Remember)
            {
                _settings.CloseBehavior = dialog.Choice == CloseChoice.Tray
                    ? CloseBehavior.Tray
                    : CloseBehavior.Exit;
                SettingsStore.Save(_settings);
            }
            behavior = dialog.Choice == CloseChoice.Tray ? CloseBehavior.Tray : CloseBehavior.Exit;
        }

        if (behavior == CloseBehavior.Tray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        e.Cancel = true;
        if (SyncService.Current.IsRunning)
        {
            _pendingExit = true;
            StatusText.Text = "等待写入完成后退出…";
            return;
        }
        _forceClose = true;
        // Closing 事件内不能同步调用 Close()；等本次关闭流程结束后再走退出流程。
        Dispatcher.BeginInvoke(() => ((App)Application.Current).ExitApplication());
    }

    private async void Main_Loaded(object sender, RoutedEventArgs e)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) =>
        {
            _settings = SettingsStore.Load();
            if (_schedule.Due(_clock.Elapsed, _settings.SyncIntervalMinutes, _settings.CollectionPaused))
            {
                TriggerSync(false);
            }
            _ = CheckForUpdatesIfDueAsync();
        };
        _timer.Start();
        await LoadAsync();
        if (_settings.SyncOnStart && !_settings.CollectionPaused)
        {
            TriggerSync(false);
        }
        else
        {
            ApplySyncStatus();
        }
        _ = CheckForUpdatesIfDueAsync();
    }

    private void SelectRange()
    {
        foreach (var radio in new[] { RangeToday, Range7d, RangeWeek, Range30d, RangeMonth, Range90d, RangeAll })
        {
            if (radio.Tag is string key && key == _rangeKey)
            {
                radio.IsChecked = true;
                return;
            }
        }
        Range7d.IsChecked = true;
    }

    private async void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: string key })
        {
            if (RangePopup is not null)
            {
                RangePopup.IsOpen = false;
            }
            _rangeKey = key;
            _customRange = null;
            SetCustomRangeActive(false);
            if (_ready)
            {
                await LoadAsync();
            }
        }
    }

    private RangeSelection CurrentRange() =>
        _customRange ?? TokenViewDatabase.CreateRange(_rangeKey);

    private void CustomRange_Click(object sender, RoutedEventArgs e)
    {
        if (RangePopup.IsOpen)
        {
            RangePopup.IsOpen = false;
            return;
        }

        var current = CurrentRange();
        var fromMs = current.FromMs;
        var toMs = current.ToMs;
        if (_snapshot is { RangeFromMs: > 0, RangeToMs: > 0 } snapshot)
        {
            fromMs = snapshot.RangeFromMs;
            toMs = snapshot.RangeToMs;
        }

        RangePicker.SetRange(fromMs, toMs);
        if (RangePopup.Child is { } child)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            RangePopup.HorizontalOffset = CustomRangeButton.ActualWidth - child.DesiredSize.Width + 12;
        }
        RangePopup.IsOpen = true;
    }

    private void RangePopup_Opened(object? sender, EventArgs e) => RangePicker.Focus();

    private void RangePicker_CancelRequested(object? sender, EventArgs e) => RangePopup.IsOpen = false;

    private async void RangePicker_RangeConfirmed(object? sender, RangeConfirmedEventArgs e)
    {
        RangePopup.IsOpen = false;
        await ApplyCustomRangeAsync(e.FromMs, e.ToMs);
    }

    private async Task ApplyCustomRangeAsync(long fromMs, long toMs)
    {
        _customRange = TokenViewDatabase.CreateCustomRange(fromMs, toMs);
        foreach (var radio in RangeRadios())
        {
            radio.IsChecked = false;
        }
        SetCustomRangeActive(true);
        await LoadAsync();
    }

    private RadioButton[] RangeRadios() =>
        [RangeToday, Range7d, RangeWeek, Range30d, RangeMonth, Range90d, RangeAll];

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!RangePopup.IsOpen || CustomRangeButton.IsMouseOver)
        {
            return;
        }
        if (RangePopup.Child is UIElement child && child.IsMouseOver)
        {
            return;
        }
        RangePopup.IsOpen = false;
    }

    private void SetCustomRangeActive(bool active)
    {
        if (CustomRangeButton is null || CustomRangeIcon is null)
        {
            return;
        }

        CustomRangeButton.Background = active ? (Brush)FindResource("AccentSoftBrush") : Brushes.Transparent;
        CustomRangeButton.Foreground = active
            ? (Brush)FindResource("AccentDarkBrush")
            : (Brush)FindResource("TextBrush");
        CustomRangeButton.BorderBrush = active
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("BorderBrush");
        CustomRangeIcon.Stroke = active
            ? (Brush)FindResource("AccentDarkBrush")
            : (Brush)FindResource("MutedBrush");
    }

    private void Metric_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { IsChecked: true } button)
        {
            return;
        }
        _metric = ReferenceEquals(button, MetricTokens) ? ChartMetric.Tokens : ChartMetric.Requests;
        ApplyMetric();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _settings = SettingsStore.Load();
        if (_settings.CollectionPaused)
        {
            await LoadAsync();
            return;
        }
        TriggerSync(false);
    }

    private void Announcement_Click(object sender, RoutedEventArgs e)
    {
        new AnnouncementWindow { Owner = this }.ShowDialog();
        UpdateAnnouncementBadge();
    }

    private void About_Click(object sender, RoutedEventArgs e) => _ = OpenSettingsAsync(5);

    private void OnAnnouncementChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateAnnouncementBadge();
            return;
        }
        Dispatcher.BeginInvoke(UpdateAnnouncementBadge);
    }

    private void UpdateAnnouncementBadge()
    {
        var service = AnnouncementService.Current;
        AnnouncementUnreadDot.Visibility = service.HasUnread ? Visibility.Visible : Visibility.Collapsed;
        AnnouncementButton.ToolTip = service.HasUnread ? "公告（有未读）" : "公告";
    }

    private void StatusText_Click(object sender, MouseButtonEventArgs e)
    {
        var service = SyncService.Current;
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("CardBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
        };
        void Add(string text)
        {
            menu.Items.Add(new MenuItem { Header = text, IsEnabled = false });
        }
        Add("采集状态");
        Add(StatusText.Text);
        if (service.FinishedAtMs is { } finished)
        {
            Add($"上次完成：{DateTimeOffset.FromUnixTimeMilliseconds(finished).LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            menu.Items.Add(new Separator());
        }
        if (service.LastBatch is { } batch)
        {
            var seen = batch.Clients.Sum(client => client.Report?.FilesSeen ?? 0);
            var parsed = batch.Clients.Sum(client => client.Report?.FilesParsed ?? 0);
            var skipped = batch.Clients.Sum(client => client.Report?.FilesSkippedUnchanged ?? 0);
            var sessions = batch.Clients.Sum(client => client.Report?.SessionsImported ?? 0);
            var failed = batch.Clients.Sum(client => client.Report?.FilesFailed ?? 0);
            Add($"扫描 {seen:N0} 个文件 · 解析 {parsed:N0} 个 · 跳过 {skipped:N0} 个未变化文件");
            Add($"导入 {sessions:N0} 个会话 · 失败文件 {failed:N0} 个");
            var issues = batch.Clients.Where(client => client.Error is not null).ToList();
            if (issues.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var issue in issues)
                {
                    var label = TokenViewDatabase.Clients.GetValueOrDefault(issue.Client)?.Label ?? issue.Client;
                    Add($"{label}：{issue.Error}{(issue.Retryable ? "（可重试）" : "")}");
                }
            }
        }
        var open = new MenuItem { Header = "打开设置" };
        open.Click += (_, _) => Settings_Click(this, new RoutedEventArgs());
        menu.Items.Add(new Separator());
        menu.Items.Add(open);
        menu.PlacementTarget = StatusText;
        menu.IsOpen = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _ = OpenSettingsAsync(0);

    private async Task OpenSettingsAsync(int section)
    {
        new SettingsWindow(section) { Owner = this }.ShowDialog();
        _settings = SettingsStore.Load();
        ApplySyncStatus();
        await LoadAsync();
    }

    private bool _updateChecking;
    private string? _pendingUpdateUrl;
    private PreparedUpdate? _pendingUpdate;

    private async Task CheckForUpdatesIfDueAsync()
    {
        if (!_settings.AutomaticUpdates || _updateChecking) return;
        var last = SettingsStore.LastUpdateCheckMs();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (last is not null && now - last < TimeSpan.FromHours(24).TotalMilliseconds) return;
        _updateChecking = true;
        try
        {
            var info = await UpdateService.CheckAsync();
            SettingsStore.SaveLastUpdateCheck(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (info is null) return;
            _pendingUpdateUrl = info.DownloadUrl;
            try
            {
                _pendingUpdate = await UpdateInstaller.DownloadAndPrepareAsync(info, null, CancellationToken.None);
                _tray?.ShowUpdateReadyBalloon(info.Version);
            }
            catch
            {
                // 自动下载失败时仍提示新版本，点击退回下载页。
                _tray?.ShowUpdateBalloon(info.Version);
            }
        }
        catch
        {
            // 后台检查不打断使用，也不弹窗；手动入口在设置 · 更新。
        }
        finally
        {
            _updateChecking = false;
        }
    }

    private void TriggerSync(bool force)
    {
        if (_settings.CollectionPaused) return;
        if (!SyncService.Current.TryStart(_settings.EnabledClients, force)) return;
        ApplySyncStatus();
    }

    private void OnSyncProgress(SyncProgress progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"同步中 {progress.CompletedClients}/{progress.TotalClients}";
            StatusDot.Fill = (Brush)FindResource("MutedBrush");
            _tray?.SetRunning(true);
        });
    }

    private void OnSyncCompleted(SyncBatch? batch, string? error)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            _tray?.SetRunning(false);
            ApplySyncStatus();
            await LoadAsync();
            if (_pendingExit)
            {
                _pendingExit = false;
                _forceClose = true;
                ((App)Application.Current).ExitApplication();
            }
        });
    }

    private void ApplySyncStatus()
    {
        var service = SyncService.Current;
        RefreshButton.IsEnabled = !_settings.CollectionPaused && !service.IsRunning;
        if (service.IsRunning)
        {
            StatusText.Text = "同步中";
            StatusDot.Fill = (Brush)FindResource("MutedBrush");
            StatusText.ToolTip = "正在读取本机客户端数据";
            return;
        }
        if (_settings.CollectionPaused)
        {
            StatusText.Text = "已暂停";
            StatusDot.Fill = (Brush)FindResource("MutedBrush");
            StatusText.ToolTip = "采集已暂停，恢复后继续读取本机数据";
            return;
        }
        if (service.HardError is not null)
        {
            StatusText.Text = "同步异常";
            StatusDot.Fill = Brushes.IndianRed;
            StatusText.ToolTip = service.HardError;
            return;
        }
        StatusText.Text = "已同步";
        StatusDot.Fill = (Brush)FindResource("AccentBrush");
        var parts = new List<string>();
        if (service.FinishedAtMs is { } finished)
        {
            parts.Add($"上次同步 {DateTimeOffset.FromUnixTimeMilliseconds(finished).LocalDateTime:HH:mm:ss}");
        }
        if (service.LastBatch is { } batch)
        {
            var parsed = batch.Clients.Sum(client => client.Report?.FilesParsed ?? 0);
            var inserted = batch.Clients.Sum(client => client.Report?.MessagesInserted ?? 0);
            parts.Add($"解析 {parsed:N0} 个文件 · 写入 {inserted:N0} 条明细");
            var retryable = batch.Clients.Where(client => client.Retryable)
                .Select(client => TokenViewDatabase.Clients.GetValueOrDefault(client.Client)?.Label ?? client.Client)
                .ToList();
            if (retryable.Count > 0)
            {
                parts.Add($"{string.Join("、", retryable)} 正在写入，下次同步自动重试");
            }
        }
        StatusText.ToolTip = parts.Count == 0 ? "尚未同步" : string.Join(" · ", parts);
    }

    private void Ledger_Click(object sender, RoutedEventArgs e)
    {
        new SessionLedgerWindow(CurrentRange()) { Owner = this }.ShowDialog();
    }

    private void RecentSessions_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentSessionsGrid.SelectedItem is SessionRow session)
        {
            new SessionDetailWindow(session) { Owner = this }.ShowDialog();
        }
    }

    // DataGrid 内嵌 ScrollViewer 会吞掉滚轮；列表自身滚到头后把事件转交给页面。
    private void RecentSessionsGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var viewer = FindVisualChild<ScrollViewer>(RecentSessionsGrid);
        var canScroll = viewer is not null &&
            (e.Delta < 0 ? viewer.VerticalOffset < viewer.ScrollableHeight : viewer.VerticalOffset > 0);
        if (canScroll)
        {
            return;
        }

        ForwardWheelToPage(e);
    }

    // 每日条带的水平 ScrollViewer 会无条件把滚轮标记为已处理，而它本身不能纵向滚动，
    // 所以直接转交给页面；条带横向仍需拖滚动条。
    private void DailyStripScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        ForwardWheelToPage(e);
    }

    private void ForwardWheelToPage(MouseWheelEventArgs e)
    {
        e.Handled = true;
        PageScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = PageScrollViewer,
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }

    private void RecentSessions_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RecentSessionsGrid.SelectedItem is SessionRow session)
        {
            e.Handled = true;
            new SessionDetailWindow(session) { Owner = this }.ShowDialog();
        }
    }

    private void ClientUsage_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClientUsageRow row }) return;
        if (!_expandedClients.Add(row.Client.Key))
        {
            _expandedClients.Remove(row.Client.Key);
        }
        RenderClientUsage();
        e.Handled = true;
    }

    private void RenderClientUsage()
    {
        var rows = _snapshot?.Clients
            .Select(row => row with { IsExpanded = _expandedClients.Contains(row.Client.Key) })
            .ToList() ?? [];
        ClientUsageList.ItemsSource = rows;
        NoClients.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _loading;
    private bool _reloadPending;

    /// <summary>加载期间再次触发（切换区间 / 同步完成 / 设置返回）合并为一次，结束后重跑。</summary>
    private async Task LoadAsync()
    {
        if (_loading)
        {
            _reloadPending = true;
            return;
        }
        _loading = true;
        try
        {
            do
            {
                _reloadPending = false;
                await LoadOnceAsync();
            }
            while (_reloadPending);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadOnceAsync()
    {
        RefreshButton.IsEnabled = false;
        _skeletonTimer.Stop();
        _skeletonTimer.Start();
        if (!SyncService.Current.IsRunning)
        {
            StatusText.Text = "读取中…";
            StatusDot.Fill = (Brush)FindResource("MutedBrush");
        }
        try
        {
            var range = CurrentRange();
            var snapshot = await Task.Run(() => TokenViewDatabase.Load(range));
            _snapshot = snapshot;

            RangeDaysText.Text = snapshot.RangeDaysText;
            TokensValue.Text = TokenViewDatabase.FormatTokens(snapshot.TotalTokens);
            RequestsValue.Text = snapshot.TotalRequests.ToString("N0");
            SessionsValue.Text = snapshot.Sessions.ToString("N0");

            var usageNotes = new List<string>();
            if (snapshot.RequestsWithoutUsage > 0)
            {
                usageNotes.Add($"{(snapshot.RequestsWithUsage > 0 ? "已知子集 · " : "")}{snapshot.RequestsWithoutUsage:N0} 条请求用量未知");
            }
            if (snapshot.SessionsPartial > 0)
            {
                usageNotes.Add($"{snapshot.SessionsPartial:N0} 个会话明细不完整");
            }
            var usageTooltip = usageNotes.Count > 0
                ? $"{snapshot.RangeText}\n{string.Join(" · ", usageNotes)}"
                : snapshot.RangeText;
            UsageTitle.ToolTip = usageTooltip;
            RangeDaysText.ToolTip = usageTooltip;

            LineChart.Points = snapshot.ChartPoints;
            ApplyMetric();
            RenderClientUsage();
            ModelRanking.ItemsSource = snapshot.Models;
            ProjectRanking.ItemsSource = snapshot.Projects;
            RecentSessionsGrid.ItemsSource = snapshot.RecentSessions;

            ApplySyncStatus();
            UpdateTrayTooltip();
        }
        catch (Exception error)
        {
            StatusText.Text = "读取失败";
            StatusDot.Fill = Brushes.IndianRed;
            MessageBox.Show(this, error.Message, "TokenView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _skeletonTimer.Stop();
            LoadingSkeleton.BeginAnimation(OpacityProperty, null);
            LoadingOverlay.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = !_settings.CollectionPaused && !SyncService.Current.IsRunning;
        }
    }

    private void UpdateTrayTooltip()
    {
        try
        {
            var today = TokenViewDatabase.LoadTodaySummary();
            _tray?.SetTooltip(
                $"TokenView · 今日 {TokenViewDatabase.FormatTokens(today.Tokens)} Token · {today.Requests:N0} 请求");
        }
        catch
        {
            _tray?.SetTooltip("TokenView");
        }
    }

    private void LineChart_MouseMove(object sender, MouseEventArgs e)
    {
        _tooltipHideTimer.Stop();
        var points = LineChart.Points;
        if (points is null || points.Count == 0)
        {
            ChartTooltip.Visibility = Visibility.Collapsed;
            return;
        }

        var position = e.GetPosition(LineChart);
        var plotWidth = Math.Max(10, LineChart.ActualWidth - 86);
        var index = points.Count == 1
            ? 0
            : (int)Math.Round((position.X - 64) / plotWidth * (points.Count - 1));
        index = Math.Clamp(index, 0, points.Count - 1);
        var point = points[index];
        // 同一个数据点只重建一次 tooltip；鼠标在同一段内移动时仅移动浮层位置。
        if (!ReferenceEquals(_hoveredPoint, point))
        {
            _hoveredPoint = point;
            ShowTooltip(point);
        }
        PositionTooltip(position);
    }

    private void ShowTooltip(ChartPoint point)
    {
        TooltipTitle.Text = point.Title;
        var totalLabel = _snapshot?.IsHourly == true ? "所选时段 Token 用量" : "Token 用量";
        TooltipTokens.Text = $"{totalLabel}：{TokenViewDatabase.FormatTokens(point.Tokens)}";
        TooltipRequests.Text = $"请求数：{point.Requests:N0}";
        TooltipSessions.Text = $"会话数：{point.Sessions:N0}";
        TooltipUnknown.Text = point.RequestsWithoutUsage > 0
            ? $"用量未知：{point.RequestsWithoutUsage:N0} 条"
            : "";
        TooltipUnknown.Visibility = point.RequestsWithoutUsage > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderTooltipBreakdown(point);
        ChartTooltip.Visibility = Visibility.Visible;
    }

    private void RenderTooltipBreakdown(ChartPoint point)
    {
        if (_tooltipByModel)
        {
            var models = (point.Models ?? [])
                .Where(slice => slice.Requests > 0)
                .OrderBy(slice => slice.Model == "未标注模型")
                .ThenByDescending(slice => _metric == ChartMetric.Tokens ? slice.Tokens : slice.Requests)
                .ThenByDescending(slice => _metric == ChartMetric.Tokens ? slice.Requests : slice.Tokens)
                .Select(slice => new TooltipModelRow(
                    slice.Model,
                    _metric == ChartMetric.Tokens
                        ? $"{slice.TokenText} Token"
                        : $"{slice.RequestText} 条请求",
                    slice.RequestsWithoutUsage > 0
                        ? $"{slice.RequestsWithoutUsage:N0} 条请求用量未知"
                        : ""))
                .ToList();
            TooltipModelBreakdown.ItemsSource = models;
            TooltipModelBreakdown.Visibility =
                models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TooltipBreakdown.Visibility = Visibility.Collapsed;
            return;
        }

        var slices = (point.Clients ?? [])
            .Where(slice => slice.Requests > 0)
            .OrderByDescending(slice => _metric == ChartMetric.Tokens ? slice.Tokens : slice.Requests)
            .ThenByDescending(slice => slice.Tokens)
            .Select(slice => new TooltipClientRow(
                slice.ClientKey,
                slice.Label,
                slice.ColorHex,
                _metric == ChartMetric.Tokens ? $"{slice.TokenText} Token" : $"{slice.RequestText} 条请求",
                slice.RequestsWithoutUsage > 0
                    ? $"{slice.RequestsWithoutUsage:N0} 条请求用量未知"
                    : ""))
            .ToList();
        TooltipBreakdown.ItemsSource = slices;
        TooltipBreakdown.Visibility = slices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TooltipModelBreakdown.Visibility = Visibility.Collapsed;
    }

    private void PositionTooltip(Point position)
    {
        ChartTooltip.Measure(new Size(280, double.PositiveInfinity));
        var tooltipWidth = Math.Max(210, ChartTooltip.DesiredSize.Width);
        var tooltipHeight = Math.Max(80, ChartTooltip.DesiredSize.Height);
        var x = position.X + 14;
        if (x + tooltipWidth > ChartOverlay.ActualWidth)
        {
            x = position.X - tooltipWidth - 10;
        }
        var y = position.Y + 14;
        if (y + tooltipHeight > ChartOverlay.ActualHeight)
        {
            y = position.Y - tooltipHeight - 10;
        }
        Canvas.SetLeft(ChartTooltip, Math.Max(0, x));
        Canvas.SetTop(ChartTooltip, Math.Max(0, y));
    }

    private void TooltipDimension_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { IsChecked: true })
        {
            return;
        }
        _tooltipByModel = ReferenceEquals(sender, TooltipModelTab);
        if (_hoveredPoint is { } point && ChartTooltip.Visibility == Visibility.Visible)
        {
            RenderTooltipBreakdown(point);
        }
    }

    private void ChartTooltip_MouseEnter(object sender, MouseEventArgs e)
    {
        _tooltipHideTimer.Stop();
    }

    private void ChartTooltip_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!LineChart.IsMouseOver)
        {
            ChartTooltip.Visibility = Visibility.Collapsed;
        }
    }

    private sealed record TooltipClientRow(string ClientKey, string Label, string ColorHex,
        string ValueText, string UnknownText);

    private sealed record TooltipModelRow(string Model, string ValueText, string UnknownText);

    private void LineChart_MouseLeave(object sender, MouseEventArgs e)
    {
        // 离开后允许下次重新构建 tooltip（同一数据点不应命中缓存）。
        _hoveredPoint = null;
        _tooltipHideTimer.Start();
    }

    private void ApplyMetric()
    {
        LineChart.Metric = _metric;
        if (_snapshot is not null)
        {
            DailyStrip.ItemsSource = BuildStrip(_snapshot, _metric);
        }
        UpdateDailyStripWidth();
        if (_hoveredPoint is { } point && ChartTooltip.Visibility == Visibility.Visible)
        {
            RenderTooltipBreakdown(point);
        }
    }

    private void DailyStripScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDailyStripWidth();
    }

    private void UpdateDailyStripWidth()
    {
        var count = _snapshot?.ChartPoints.Count ?? 0;
        if (count == 0)
        {
            DailyStrip.Width = double.NaN;
            return;
        }

        // Equal-width cells: fill the viewport when every day fits, otherwise grow
        // and let the scroll viewer move through a fixed minimum pitch.
        DailyStrip.Width = Math.Max(DailyStripScroll.ViewportWidth, count * DailyStripMinPitch);
    }

    private static List<DailyStripRow> BuildStrip(OverviewSnapshot snapshot, ChartMetric metric)
    {
        return snapshot.ChartPoints.Select(point =>
        {
            var text = metric == ChartMetric.Tokens
                ? TokenViewDatabase.FormatTokens(point.Tokens)
                : point.Requests.ToString("N0");
            return new DailyStripRow(point.Label, text);
        }).ToList();
    }
}
