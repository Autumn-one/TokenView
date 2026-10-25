using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace TokenView.Wpf.Services;

/// <summary>在客户端文件之外启用 Qoder 的真实 token 输出，采集器本身保持只读。</summary>
public static class QoderUsageRecordingService
{
    private static readonly object Gate = new();
    private static readonly string[] Variables =
    [
        "QODER_EXPOSE_TOKEN_USAGE",
        "QODERCN_EXPOSE_TOKEN_USAGE",
    ];

    public static Task<string?> EnsureEnabledAsync(IReadOnlyCollection<string> clients) =>
        Task.Run(() =>
        {
            lock (Gate)
            {
                return EnsureEnabled(clients, Environment.GetEnvironmentVariable,
                    Environment.SetEnvironmentVariable, NotifyEnvironmentChanged);
            }
        });

    internal static string? EnsureEnabled(IReadOnlyCollection<string> clients,
        Func<string, EnvironmentVariableTarget, string?> read,
        Action<string, string, EnvironmentVariableTarget> write,
        Action notify)
    {
        if (!clients.Contains("qoder", StringComparer.Ordinal))
        {
            return null;
        }

        var changed = false;
        var failed = false;
        foreach (var name in Variables)
        {
            foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User })
            {
                try
                {
                    if (IsEnabled(read(name, target)))
                    {
                        continue;
                    }
                    write(name, "true", target);
                    changed |= target == EnvironmentVariableTarget.User;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
                {
                    // 每个发行版与作用域独立处理，禁止把权限错误原文带入诊断。
                    failed = true;
                }
            }
        }

        if (changed)
        {
            try
            {
                notify();
            }
            catch (IOException)
            {
                return "Qoder 记录开关已保存，环境变化广播未完成";
            }
        }
        return failed ? "Qoder 记录开关未能全部写入，现有记录仍可采集" : null;
    }

    private static bool IsEnabled(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes";

    private static void NotifyEnvironmentChanged()
    {
        // 环境块由新进程继承；通知 Explorer 使后续从桌面启动的客户端也能取得开关。
        if (SendMessageTimeout(new IntPtr(0xffff), 0x001a, IntPtr.Zero, "Environment",
                0x0002 | 0x0020, 1000, out _) == IntPtr.Zero)
        {
            throw new IOException("Environment notification did not complete");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr parameter,
        string value, uint flags, uint timeout, out UIntPtr result);
}
