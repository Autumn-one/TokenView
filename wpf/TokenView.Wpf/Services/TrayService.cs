using System.Drawing;
using Forms = System.Windows.Forms;

namespace TokenView.Wpf.Services;

/// <summary>系统托盘：显示主窗口、立即同步、重启与退出。</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _syncItem;

    public event Action? ShowRequested;
    public event Action? SyncRequested;
    public event Action? RestartRequested;
    public event Action? ExitRequested;
    public event Action? UpdateRequested;

    public TrayService()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("打开 TokenView");
        open.Click += (_, _) => ShowRequested?.Invoke();
        _syncItem = new Forms.ToolStripMenuItem("立即同步");
        _syncItem.Click += (_, _) => SyncRequested?.Invoke();
        var restart = new Forms.ToolStripMenuItem("重启 TokenView");
        restart.Click += (_, _) => RestartRequested?.Invoke();
        var exit = new Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(open);
        menu.Items.Add(_syncItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(restart);
        menu.Items.Add(exit);

        _icon = new Forms.NotifyIcon
        {
            Text = "TokenView",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => UpdateRequested?.Invoke();
    }

    private static Icon LoadIcon()
    {
        var executable = Environment.ProcessPath;
        return executable is not null
            ? Icon.ExtractAssociatedIcon(executable) ?? SystemIcons.Application
            : SystemIcons.Application;
    }

    public void SetRunning(bool running)
    {
        _syncItem.Enabled = !running;
        _syncItem.Text = running ? "正在同步…" : "立即同步";
    }

    public void SetTooltip(string text)
    {
        var trimmed = text.Length > 63 ? text[..63] : text;
        _icon.Text = trimmed;
    }

    /// <summary>自动检查命中新版本时的一次性提示；点击后由宿主打开下载页。</summary>
    public void ShowUpdateBalloon(string version)
    {
        _icon.BalloonTipTitle = "TokenView 有新版本";
        _icon.BalloonTipText = $"v{version} 已发布，点击打开下载页。";
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(8000);
    }

    /// <summary>更新包已下载并校验完成，等待用户确认重启。</summary>
    public void ShowUpdateReadyBalloon(string version)
    {
        _icon.BalloonTipTitle = "TokenView 更新已就绪";
        _icon.BalloonTipText = $"v{version} 已下载并校验，点击重启完成更新。";
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(8000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
