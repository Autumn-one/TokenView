using System.Windows;
using System.Windows.Controls;
using TokenView.Wpf.Data;
using TokenView.Wpf.Services;

namespace TokenView.Wpf;

public partial class SessionLedgerWindow : Window
{
    public SessionLedgerWindow(RangeSelection range)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        SessionsGrid.PreviewKeyDown += SessionsGrid_KeyDown;
        try
        {
            var ledger = TokenViewDatabase.LoadLedger(range);
            RangeText.Text = $"{ledger.RangeLabel} · {ledger.RangeText}";
            SessionsText.Text = ledger.Sessions.ToString("N0");
            TokensText.Text = ledger.TokenText;
            TokensText.ToolTip = ledger.HasEstimate
                ? "仅统计实测 / 换算会话；估算会话以 ≈ 标注，未计入区间合计"
                : null;
            TodayTokensText.Text = ledger.TodayTokenText;
            MessagesText.Text = ledger.Messages.ToString("N0");
            NoteText.Text = ledger.HasEstimate
                ? $"单位：Token · 按会话开始日 · 估算会话以 ≈ 标注，未计入区间合计与图表（另有估算 ≈ {TokenViewDatabase.FormatTokens(ledger.EstimatedTokens)}）"
                : "单位：Token · 按会话开始日";
            CacheHintText.Text = ledger.CacheHitBps is { } bps
                ? $"缓存命中率 {bps / 100d:0.0}%"
                : ledger.SessionsWithoutUsage > 0
                    ? $"{ledger.SessionsWithoutUsage:N0} 个会话总量未知"
                    : "";
            DailyChart.Points = ledger.Daily;
            DailyChart.Metric = ChartMetric.Tokens;
            ClientList.ItemsSource = ledger.Clients;
            SessionsGrid.ItemsSource = ledger.SessionRows;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "TokenView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SessionsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SessionsGrid.SelectedItem is SessionRow session)
        {
            new SessionDetailWindow(session) { Owner = this }.ShowDialog();
        }
    }

    private void DailyChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var points = DailyChart.Points;
        if (points is null || points.Count == 0)
        {
            ChartTooltip.Visibility = Visibility.Collapsed;
            return;
        }
        var position = e.GetPosition(DailyChart);
        var plotWidth = Math.Max(10, DailyChart.ActualWidth - 86);
        var index = points.Count == 1
            ? 0
            : (int)Math.Round((position.X - 64) / plotWidth * (points.Count - 1));
        index = Math.Clamp(index, 0, points.Count - 1);
        var point = points[index];
        TooltipTitle.Text = point.Title;
        var unknown = point.Requests > 0 && point.RequestsWithoutUsage >= point.Requests;
        TooltipTokens.Text = unknown
            ? "当日会话 Token：—"
            : $"当日会话 Token：{point.TokenText} Token";
        var exact = point.Tokens.ToString("N0");
        var compact = TokenViewDatabase.FormatTokens(point.Tokens);
        var showExact = !unknown && compact != exact;
        TooltipExact.Text = showExact ? $"精确值：{exact} Token" : "";
        TooltipExact.Visibility = showExact ? Visibility.Visible : Visibility.Collapsed;
        TooltipSessions.Text = $"会话数：{point.Sessions:N0}";
        var slices = (point.Clients ?? [])
            .Where(slice => slice.Tokens > 0)
            .OrderByDescending(slice => slice.Tokens)
            .Select(slice => new TooltipClientRow(slice.ClientKey, slice.Label, slice.ColorHex,
                $"{slice.TokenText} Token"))
            .ToList();
        TooltipBreakdown.ItemsSource = slices;
        TooltipBreakdown.Visibility = slices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ChartTooltip.Visibility = Visibility.Visible;
        ChartTooltip.Measure(new Size(280, double.PositiveInfinity));
        var tooltipWidth = Math.Max(210, ChartTooltip.DesiredSize.Width);
        var tooltipHeight = Math.Max(80, ChartTooltip.DesiredSize.Height);
        var x = position.X + 14;
        if (x + tooltipWidth > ChartOverlay.ActualWidth) x = position.X - tooltipWidth - 10;
        var y = position.Y + 14;
        if (y + tooltipHeight > ChartOverlay.ActualHeight) y = position.Y - tooltipHeight - 10;
        Canvas.SetLeft(ChartTooltip, Math.Max(0, x));
        Canvas.SetTop(ChartTooltip, Math.Max(0, y));
    }

    private void DailyChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        ChartTooltip.Visibility = Visibility.Collapsed;

    private sealed record TooltipClientRow(string ClientKey, string Label, string ColorHex,
        string ValueText);

    private void SessionsGrid_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && SessionsGrid.SelectedItem is SessionRow session)
        {
            e.Handled = true;
            new SessionDetailWindow(session) { Owner = this }.ShowDialog();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
