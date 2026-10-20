using System.Windows;
using System.Windows.Media;
using TokenView.Wpf.Data;
using TokenView.Wpf.Services;

namespace TokenView.Wpf;

public partial class SessionDetailWindow : Window
{
    private const int PageSize = 100;

    private readonly SessionRow _session;
    private int _offset;

    public SessionDetailWindow(SessionRow session)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        _session = session;
        TitleText.Text = $"{session.ClientLabel} · {session.SessionId}";
        ClientIconView.ClientKey = session.ClientKey;
        ClientIconView.IconBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(session.ColorHex));
        SubtitleText.Text = $"{session.Started} · 完整会话（不受日期筛选限制） · 本地时间";
        ProjectText.Text = $"项目：{session.Project}";
        ModelText.Text = $"模型：{session.Model}";
        TokensText.Text = TokenViewDatabase.FormatTokens(session.Tokens);
        LoadPage();
    }

    private void LoadPage()
    {
        try
        {
            var detail = TokenViewDatabase.LoadSessionDetail(_session.ClientKey, _session.SessionId,
                _offset, PageSize);
            TitleText.Text = $"{detail.ClientLabel} · {detail.SessionId}";
            SubtitleText.Text = $"{detail.Started} · {detail.Coverage} · {detail.TotalBasis} · 本地时间";
            ProjectText.Text = $"项目：{detail.Project}";
            ModelText.Text = $"模型：{detail.Model} · {detail.Started} — {detail.Ended}";
            TokensText.Text = TokenViewDatabase.FormatTokens(detail.Tokens);
            RequestsText.Text = detail.TotalMessages.ToString("N0");
            CoverageText.Text = detail.Coverage;
            InputText.Text = Format(detail.Input);
            OutputText.Text = Format(detail.Output);
            ReasoningText.Text = Format(detail.Reasoning);
            CacheReadText.Text = Format(detail.CacheRead);
            CacheWriteText.Text = Format(detail.CacheWrite);
            BasisText.Text = detail.TotalBasis;
            var hints = new List<string>
            {
                $"有时间 {detail.Timed:N0} / 无时间 {detail.Untimed:N0}",
                $"有用量 {detail.WithUsage:N0} / 无用量 {detail.WithoutUsage:N0}",
            };
            if (detail.Estimated > 0) hints.Add($"估算 {detail.Estimated:N0}");
            if (detail.UnknownRelations.Count > 0)
            {
                hints.Add($"未知关系：{string.Join("、", detail.UnknownRelations)}");
            }
            CoverageHintText.Text = string.Join(" · ", hints);
            MessagesGrid.ItemsSource = detail.Messages;
            PageText.Text = $"第 {detail.Page} 页 · 每页最多 {PageSize} 条 · 全会话 {detail.TotalMessages:N0} 条";
            PrevButton.IsEnabled = _offset > 0;
            NextButton.IsEnabled = _offset + PageSize < detail.TotalMessages;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "TokenView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Format(long? value) =>
        value is { } number ? TokenViewDatabase.FormatTokens(number) : "—";

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        _offset = Math.Max(0, _offset - PageSize);
        LoadPage();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _offset += PageSize;
        LoadPage();
    }
}
