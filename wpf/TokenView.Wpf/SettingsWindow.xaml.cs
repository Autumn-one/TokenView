using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data;
using TokenView.Wpf.Data.Collectors;
using TokenView.Wpf.Services;
using TokenView.Wpf.Settings;

namespace TokenView.Wpf;

public partial class SettingsWindow : Window
{
    private AppSettings _settings = new();
    private bool _loading = true;
    private string? _renderedAboutHash;

    public SettingsWindow(int section = 0)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        _settings = SettingsStore.Load();
        Populate();
        NavList.SelectedIndex = Math.Clamp(section, 0, NavList.Items.Count - 1);
        _loading = false;
        SyncService.Current.Completed += OnSyncCompleted;
        AboutInfoService.Current.Changed += OnAboutInfoChanged;
        Closed += (_, _) =>
        {
            SyncService.Current.Completed -= OnSyncCompleted;
            AboutInfoService.Current.Changed -= OnAboutInfoChanged;
        };
    }

    private void OnSyncCompleted(SyncBatch? batch, string? error)
    {
        Dispatcher.BeginInvoke(BuildClientList);
    }

    private void Populate()
    {
        SelectTag(CloseBehaviorBox, _settings.CloseBehavior switch
        {
            CloseBehavior.Tray => "tray",
            CloseBehavior.Exit => "exit",
            _ => "ask",
        });
        SelectTag(ThemeBox, _settings.Theme switch
        {
            AppTheme.Light => "light",
            AppTheme.Dark => "dark",
            _ => "system",
        });
        SelectTag(TokenDisplayBox, _settings.TokenDisplay switch
        {
            TokenDisplay.Chinese => "chinese",
            TokenDisplay.K => "k",
            TokenDisplay.M => "m",
            TokenDisplay.B => "b",
            TokenDisplay.Exact => "exact",
            _ => "auto",
        });
        SelectTag(DefaultRangeBox, _settings.DefaultRange);
        SelectTag(SyncIntervalBox, _settings.SyncIntervalMinutes.ToString());

        AutostartCheck.IsChecked = _settings.Autostart;
        AutostartHiddenCheck.IsChecked = _settings.AutostartHidden;
        RememberWindowCheck.IsChecked = _settings.RememberWindow;
        PausedCheck.IsChecked = _settings.CollectionPaused;
        SyncOnStartCheck.IsChecked = _settings.SyncOnStart;
        AutomaticUpdatesCheck.IsChecked = _settings.AutomaticUpdates;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _preparedUpdate = null;

        BuildClientList();

        DatabasePathText.Text = TokenViewDatabase.DatabasePath;
        SettingsPathText.Text = StoragePaths.SettingsPath;
        AboutVersionText.Text = $"版本 {UpdateService.CurrentVersion}";
        RenderAboutInfo();
        var lastCheck = SettingsStore.LastUpdateCheckMs();
        LastUpdateText.Text = lastCheck is null
            ? "尚未检查更新。"
            : $"上次检查：{DateTimeOffset.FromUnixTimeMilliseconds(lastCheck.Value).LocalDateTime:yyyy-MM-dd HH:mm}";
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem { Tag: string value } && value == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box) =>
        box.SelectedItem is ComboBoxItem { Tag: string value } ? value : "";

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralPanel is null)
        {
            return;
        }
        var index = Math.Max(0, NavList.SelectedIndex);
        GeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        CollectionPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        DataPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        if (index == 5)
        {
            _ = LoadAboutInfoAsync();
        }
    }

    private async Task LoadAboutInfoAsync()
    {
        await AboutInfoService.Current.EnsureLoadedAsync();
    }

    private void AboutRetry_Click(object sender, RoutedEventArgs e)
    {
        _ = AboutInfoService.Current.RetryAsync();
    }

    private void AboutMarkdown_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        SettingsScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = SettingsScroll,
        });
    }

    private void OnAboutInfoChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            RenderAboutInfo();
            return;
        }
        Dispatcher.BeginInvoke(RenderAboutInfo);
    }

    private void RenderAboutInfo()
    {
        var service = AboutInfoService.Current;
        var content = service.Content;
        AboutInfoStatusText.Text = content is null
            ? service.IsFetching
                ? "正在获取动态信息…"
                : service.HasAttempted
                    ? "尚未获取动态信息"
                    : "打开此分类时自动获取"
            : service.IsFetching
                ? $"更新于 {content.FetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 正在刷新…"
                : $"更新于 {content.FetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

        AboutInfoErrorText.Visibility = service.Error is null ? Visibility.Collapsed : Visibility.Visible;
        AboutInfoErrorText.Text = service.Error ?? "";
        AboutRetryButton.Visibility = service.Error is null ? Visibility.Collapsed : Visibility.Visible;
        AboutRetryButton.IsEnabled = !service.IsFetching;

        AboutInfoStateText.Visibility = content is null ? Visibility.Visible : Visibility.Collapsed;
        if (content is null)
        {
            AboutInfoStateText.Text = service.IsFetching
                ? "正在从公开镜像源获取…"
                : service.Error is not null
                    ? "动态信息暂时不可用，可稍后重试。"
                    : "尚未获取动态信息。";
        }

        AboutMarkdown.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
        if (content is not null && content.ContentHash != _renderedAboutHash)
        {
            _renderedAboutHash = content.ContentHash;
            AboutMarkdown.Markdown = content.Markdown;
        }
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.CloseBehavior = TagOf(CloseBehaviorBox) switch
        {
            "tray" => CloseBehavior.Tray,
            "exit" => CloseBehavior.Exit,
            _ => CloseBehavior.Ask,
        };
        _settings.Theme = TagOf(ThemeBox) switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };
        _settings.TokenDisplay = TagOf(TokenDisplayBox) switch
        {
            "chinese" => TokenDisplay.Chinese,
            "k" => TokenDisplay.K,
            "m" => TokenDisplay.M,
            "b" => TokenDisplay.B,
            "exact" => TokenDisplay.Exact,
            _ => TokenDisplay.Auto,
        };
        _settings.DefaultRange = TagOf(DefaultRangeBox);
        _settings.SyncIntervalMinutes = int.TryParse(TagOf(SyncIntervalBox), out var minutes) ? minutes : 5;
        _settings.Autostart = AutostartCheck.IsChecked == true;
        _settings.AutostartHidden = AutostartHiddenCheck.IsChecked == true;
        _settings.RememberWindow = RememberWindowCheck.IsChecked == true;
        _settings.CollectionPaused = PausedCheck.IsChecked == true;
        _settings.SyncOnStart = SyncOnStartCheck.IsChecked == true;
        _settings.AutomaticUpdates = AutomaticUpdatesCheck.IsChecked == true;
        Save();
        TokenFormatting.Display = _settings.TokenDisplay;
        AutostartService.Apply(_settings);
        ThemeManager.Apply(_settings.Theme);
    }

    private void Client_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EnabledClients = EnumerateCheckBoxes(ClientListPanel)
            .Where(box => box.IsChecked == true && box.Tag is string)
            .Select(box => (string)box.Tag)
            .Distinct()
            .ToList();
        Save();
    }

    private static IEnumerable<CheckBox> EnumerateCheckBoxes(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is CheckBox box) yield return box;
            foreach (var nested in EnumerateCheckBoxes(child)) yield return nested;
        }
    }

    private void BuildClientList()
    {
        ClientListPanel.Children.Clear();
        var stats = TokenViewDatabase.LoadClientStats();
        var muted = (Brush)FindResource("MutedBrush");
        var text = (Brush)FindResource("TextBrush");
        var service = SyncService.Current;

        foreach (var key in AppSettings.AllClients)
        {
            var info = TokenViewDatabase.Clients.GetValueOrDefault(key) ??
                       new ClientInfo(key, key, "#6b7280");
            var enabled = _settings.EnabledClients.Contains(key);
            var row = new Border
            {
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 10, 0, 12),
            };
            var stack = new StackPanel();

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var chip = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(info.ColorHex)) { Opacity = 0.12 },
                Child = new ClientIcon
                {
                    ClientKey = key,
                    IconSize = 15,
                    IconBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(info.ColorHex)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            header.Children.Add(chip);
            var check = new CheckBox
            {
                Content = info.Label,
                IsChecked = enabled,
                Tag = key,
                FontWeight = FontWeights.SemiBold,
                Foreground = text,
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.Checked += Client_Changed;
            check.Unchecked += Client_Changed;
            Grid.SetColumn(check, 1);
            header.Children.Add(check);

            var status = new TextBlock
            {
                FontSize = 11,
                Foreground = muted,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(status, 2);
            header.Children.Add(status);
            stack.Children.Add(header);

            var result = service.LastResults.GetValueOrDefault(key);
            if (!enabled)
            {
                status.Text = "已停用";
            }
            else if (result?.Error is { } errorText)
            {
                status.Text = result.Retryable ? "源数据变化 · 待重试" : "读取失败";
                if (!result.Retryable) status.Foreground = Brushes.IndianRed;
            }
            else if (result?.Report is { } report)
            {
                status.Text = report.RootMissing
                    ? "未发现数据"
                    : $"已检查 · {report.FilesParsed:N0} 个文件已解析";
            }
            else
            {
                status.Text = "尚未检查";
            }

            var collector = SyncEngine.Find(key);
            if (collector is not null)
            {
                foreach (var path in collector.SourcePaths)
                {
                    var exists = File.Exists(path) || Directory.Exists(path);
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"{path} · {(exists ? "可访问" : "不存在")}",
                        FontSize = 11,
                        Foreground = muted,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 5, 0, 0),
                    });
                }
            }

            if (stats.GetValueOrDefault(key) is { } clientStats)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"会话 {clientStats.Sessions:N0} · 明细 {clientStats.Messages:N0} · " +
                           $"已跟踪文件 {clientStats.Files:N0} · 无法解析 {clientStats.Unparsed:N0}" +
                           (clientStats.EstimatedSessions > 0
                               ? $" · 估算 {clientStats.EstimatedSessions:N0}"
                               : ""),
                    FontSize = 11,
                    Foreground = muted,
                    Margin = new Thickness(0, 5, 0, 0),
                });
            }

            if (result?.Error is { } message)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = message,
                    FontSize = 11,
                    Foreground = result.Retryable ? muted : Brushes.IndianRed,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 5, 0, 0),
                });
            }

            var rescan = new Button
            {
                Content = "重扫此客户端",
                Style = (Style)FindResource("LinkButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 7, 0, 0),
                IsEnabled = enabled && !_settings.CollectionPaused && !service.IsRunning,
            };
            rescan.Click += (_, _) => RescanClient(key);
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 7, 0, 0),
            };
            actions.Children.Add(rescan);
            var copy = new Button
            {
                Content = "复制脱敏诊断",
                Style = (Style)FindResource("LinkButton"),
                Margin = new Thickness(8, 0, 0, 0),
            };
            copy.Click += (_, _) => CopyDiagnostics(key, info, clientStats: stats.GetValueOrDefault(key),
                result);
            actions.Children.Add(copy);
            stack.Children.Add(actions);

            row.Child = stack;
            ClientListPanel.Children.Add(row);
        }
    }

    private void CopyDiagnostics(string key, ClientInfo info, ClientStatsRow? clientStats,
        ClientSyncResult? result)
    {
        var lines = new List<string>
        {
            $"TokenView {UpdateService.CurrentVersion} · WPF",
            $"客户端：{info.Label} ({key})",
            $"启用：{_settings.EnabledClients.Contains(key)} · 暂停：{_settings.CollectionPaused}",
        };
        var collector = SyncEngine.Find(key);
        if (collector is not null)
        {
            foreach (var path in collector.SourcePaths)
            {
                var state = File.Exists(path) || Directory.Exists(path) ? "可访问" : "不存在";
                lines.Add($"来源：{path} · {state}");
            }
        }
        if (clientStats is not null)
        {
            lines.Add($"会话 {clientStats.Sessions} · 明细 {clientStats.Messages} · " +
                      $"已跟踪文件 {clientStats.Files} · 无法解析 {clientStats.Unparsed}" +
                      (clientStats.EstimatedSessions > 0
                          ? $" · 估算 {clientStats.EstimatedSessions}"
                          : ""));
        }
        if (result?.Report is { } report)
        {
            lines.Add($"扫描 {report.FilesSeen} · 解析 {report.FilesParsed} · " +
                      $"未变化 {report.FilesSkippedUnchanged} · 失败 {report.FilesFailed} · " +
                      $"目录失败 {report.DirectoriesFailed}");
            if (report.UnparsedLines > 0) lines.Add($"无法解析行 {report.UnparsedLines}");
        }
        if (result?.Error is { } error)
        {
            lines.Add($"错误：{error}{(result.Retryable ? "（可重试）" : "")}");
        }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            MessageBox.Show(this, "已复制脱敏诊断摘要。", "TokenView", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception failure)
        {
            MessageBox.Show(this, $"复制失败：{failure.Message}", "TokenView", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void RescanClient(string key)
    {
        if (!_settings.EnabledClients.Contains(key) || _settings.CollectionPaused) return;
        if (!SyncService.Current.TryStart([key], true)) return;
        BuildClientList();
    }

    private void Save()
    {
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "TokenView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private UpdateInfo? _update;
    private PreparedUpdate? _preparedUpdate;

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateResultText.Text = "正在检查更新…";
        _update = null;
        _preparedUpdate = null;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            _update = await UpdateService.CheckAsync();
            SettingsStore.SaveLastUpdateCheck(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            LastUpdateText.Text =
                $"上次检查：{DateTimeOffset.Now:yyyy-MM-dd HH:mm}";
            if (_update is null)
            {
                UpdateResultText.Text = $"已是最新版本（v{UpdateService.CurrentVersion}）。";
            }
            else
            {
                var notes = string.IsNullOrWhiteSpace(_update.Notes)
                    ? ""
                    : $"\n{_update.Notes!.Trim()}";
                UpdateResultText.Text = $"发现新版本 v{_update.Version}。{notes}";
                DownloadUpdateButton.Visibility = _update.DownloadUrl is null
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }
        catch (Exception error)
        {
            UpdateResultText.Text = $"检查更新失败：{error.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        DownloadUpdateButton.IsEnabled = false;
        UpdateResultText.Text = "正在下载并校验更新包…";
        try
        {
            _preparedUpdate = await UpdateInstaller.DownloadAndPrepareAsync(_update, null, CancellationToken.None);
            UpdateResultText.Text =
                $"v{_update.Version} 已下载并校验通过，点击“重启并更新”完成安装。";
            DownloadUpdateButton.Visibility = Visibility.Collapsed;
            InstallUpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception error)
        {
            UpdateResultText.Text = $"下载更新失败：{error.Message}";
        }
        finally
        {
            DownloadUpdateButton.IsEnabled = true;
        }
    }

    private void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_preparedUpdate is null) return;
        try
        {
            UpdateInstaller.Launch(_preparedUpdate, () => ((App)Application.Current).ExitApplication());
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "TokenView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(UpdateService.ReleasePage) { UseShellExecute = true });
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(TokenViewDatabase.DatabasePath);
        if (directory is not null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
    }

    private void Relocate_Click(object sender, RoutedEventArgs e)
    {
        if (SyncService.Current.IsRunning)
        {
            MessageBox.Show(this, "正在采集，请等待同步完成后再迁移存储目录。", "TokenView",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 TokenView 数据目录",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this,
                $"将统计数据与设置迁移到：\n{dialog.FolderName}\n\n迁移成功后旧目录中的 TokenView 文件会被删除。",
                "迁移存储目录", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        try
        {
            var result = Services.StorageRelocationService.Relocate(dialog.FolderName);
            DatabasePathText.Text = TokenViewDatabase.DatabasePath;
            SettingsPathText.Text = StoragePaths.SettingsPath;
            MessageBox.Show(this,
                result.Warning is null
                    ? $"已迁移到：\n{result.Directory}"
                    : $"已迁移到：\n{result.Directory}\n\n{result.Warning}",
                "TokenView", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "TokenView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "恢复默认设置会重置窗口、主题、采集与更新偏好；统计数据和存储位置保留，采集会恢复。",
                "恢复默认设置", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        try
        {
            _settings = new AppSettings();
            SettingsStore.Save(_settings);
            _loading = true;
            Populate();
            _loading = false;
            AutostartService.Apply(_settings);
            ThemeManager.Apply(_settings.Theme);
            TokenFormatting.Display = _settings.TokenDisplay;
            MessageBox.Show(this, "已恢复默认设置。", "TokenView", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "TokenView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearStatistics_Click(object sender, RoutedEventArgs e)
    {
        if (SyncService.Current.IsRunning)
        {
            MessageBox.Show(this, "正在采集，请等待同步完成后再清除统计数据。", "TokenView",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                "确定清除 TokenView 的全部统计数据吗？客户端原始会话文件不会被修改。",
                "清除统计数据", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = TokenViewDatabase.DatabasePath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA busy_timeout = 5000;
                BEGIN IMMEDIATE;
                DELETE FROM messages;
                DELETE FROM sessions;
                DELETE FROM source_files;
                DELETE FROM request_hourly;
                DELETE FROM request_hourly_sessions;
                UPDATE usage_rollup_state SET request_dirty = 0 WHERE id = 1;
                COMMIT;
                """;
            command.ExecuteNonQuery();
            _settings.CollectionPaused = true;
            SettingsStore.Save(_settings);
            _loading = true;
            PausedCheck.IsChecked = true;
            _loading = false;
            MessageBox.Show(this, "统计数据已清除，采集已暂停。", "TokenView", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "TokenView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
