using TokenView.Wpf.Data;
using TokenView.Wpf.Data.Collectors;

namespace TokenView.Wpf.Services;

internal static class CollectionDiagnostics
{
    public static string Build(string client, bool enabled, bool paused, int sources, int accessible,
        ClientStatsRow? stats, ClientSyncResult? result)
    {
        var info = TokenViewDatabase.Clients.GetValueOrDefault(client);
        var lines = new List<string>
        {
            $"TokenView {UpdateService.CurrentVersion} · WPF",
            $"客户端：{info?.Label ?? "未知"} ({info?.Key ?? "unknown"})",
            $"启用：{enabled} · 暂停：{paused}",
            $"来源数：{sources} · 可访问：{accessible}",
        };
        if (stats is not null)
            lines.Add($"会话 {stats.Sessions} · 明细 {stats.Messages} · 已跟踪文件 {stats.Files} · " +
                      $"无法解析 {stats.Unparsed} · 估算 {stats.EstimatedSessions}");
        if (result?.Report is { } report)
            lines.Add($"扫描 {report.FilesSeen} · 解析 {report.FilesParsed} · 未变化 {report.FilesSkippedUnchanged} · " +
                      $"失败 {report.FilesFailed} · 目录失败 {report.DirectoriesFailed} · " +
                      $"无法解析行 {report.UnparsedLines} · 陈旧会话 {report.SessionsSkippedStale}");
        // 不复制任意错误字符串，即使上游将来遗漏脱敏也不会进入剪贴板。
        if (result?.Error is not null)
            lines.Add(result.Retryable ? "状态：同步待重试" : "状态：同步失败，请检查权限与数据源格式");
        return string.Join(Environment.NewLine, lines);
    }
}
