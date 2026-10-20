using System.Windows;
using System.Windows.Threading;
using TokenView.Wpf.Services;

namespace TokenView.Wpf;

public partial class AnnouncementWindow : Window
{
    private static readonly TimeSpan ReadDwell = TimeSpan.FromSeconds(2);

    private readonly AnnouncementService _service = AnnouncementService.Current;
    private readonly DispatcherTimer _readTimer = new();
    private string? _renderedHash;
    private string? _timerHash;

    public AnnouncementWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        _readTimer.Interval = ReadDwell;
        _readTimer.Tick += ReadTimer_Tick;
        _service.Changed += OnAnnouncementChanged;
        IsVisibleChanged += OnVisibilityChanged;
        StateChanged += OnWindowStateChanged;
        Closed += OnClosed;
        Render();
    }

    private void OnAnnouncementChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            Render();
            return;
        }
        Dispatcher.BeginInvoke(Render);
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RestartReadTimer();
        }
        else
        {
            StopReadTimer();
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            StopReadTimer();
        }
        else if (IsVisible)
        {
            RestartReadTimer();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _service.Changed -= OnAnnouncementChanged;
        StopReadTimer();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Retry_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        _ = _service.RefreshAsync();
    }

    private void Render()
    {
        var content = _service.Content;
        if (content is not null && content.ContentHash != _renderedHash)
        {
            _renderedHash = content.ContentHash;
            MarkdownViewer.Markdown = content.Markdown;
            RestartReadTimer();
        }

        MarkdownViewer.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
        StatePanel.Visibility = content is null ? Visibility.Visible : Visibility.Collapsed;

        if (content is null)
        {
            if (_service.IsFetching)
            {
                StateTitle.Text = "正在获取公告…";
                StateHint.Text = "内容来自公开仓库，只在内存中展示，不保存正文。";
            }
            else if (_service.Error is not null)
            {
                StateTitle.Text = "获取公告失败";
                StateHint.Text = "内容源暂时不可用，可以稍后重试。";
            }
            else
            {
                StateTitle.Text = "暂无公告";
                StateHint.Text = "公开仓库暂时没有可展示的内容。";
            }
        }

        UpdatedText.Text = content is null
            ? "尚未获取"
            : $"更新于 {content.FetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        UnreadChip.Visibility = _service.HasUnread ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Content = _service.IsFetching ? "刷新中…" : "刷新";
        RefreshButton.IsEnabled = !_service.IsFetching;
        RetryButton.IsEnabled = !_service.IsFetching;

        ErrorBar.Visibility = _service.Error is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = _service.Error ?? "";
    }

    private void RestartReadTimer()
    {
        StopReadTimer();
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }
        if (_service.Content is not { } content || _service.IsRead(content.ContentHash))
        {
            return;
        }
        _timerHash = content.ContentHash;
        _readTimer.Start();
    }

    private void StopReadTimer()
    {
        _readTimer.Stop();
        _timerHash = null;
    }

    private void ReadTimer_Tick(object? sender, EventArgs e)
    {
        var hash = _timerHash;
        StopReadTimer();
        if (hash is null || !IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }
        if (_service.Content is { } content &&
            string.Equals(content.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            _service.MarkRead(hash);
        }
    }
}
