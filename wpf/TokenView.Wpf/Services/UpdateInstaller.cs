using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TokenView.Wpf.Services;

public sealed record PreparedUpdate(UpdateInfo Info, string StagingDirectory);

/// <summary>下载、校验并暂存更新包；替换文件由独立的 PowerShell 助手在进程退出后执行。</summary>
public static class UpdateInstaller
{
    private const string ExecutableName = "TokenView.Wpf.exe";

    public static async Task<PreparedUpdate> DownloadAndPrepareAsync(UpdateInfo info,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"TokenView-update-{Guid.NewGuid():N}");
        var zip = Path.Combine(root, "update.zip");
        var staging = Path.Combine(root, "app");
        System.IO.Directory.CreateDirectory(root);
        try
        {
            await UpdateService.DownloadAsync(info, zip, progress, cancellationToken);
            System.IO.Directory.CreateDirectory(staging);
            await Task.Run(() => ExtractSafely(zip, staging), cancellationToken);
            if (!File.Exists(Path.Combine(staging, ExecutableName)))
            {
                throw new InvalidDataException("更新包缺少 TokenView.Wpf.exe");
            }
            File.Delete(zip);
            return new PreparedUpdate(info, staging);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    public static void Launch(PreparedUpdate update, Action shutdown)
    {
        var appDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var executableName = Path.GetFileName(Environment.ProcessPath) ?? ExecutableName;
        var script = Path.Combine(Path.GetTempPath(), $"TokenView-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, Script, new UTF8Encoding(false));

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-processId");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-source");
        start.ArgumentList.Add(update.StagingDirectory);
        start.ArgumentList.Add("-target");
        start.ArgumentList.Add(appDirectory);
        start.ArgumentList.Add("-exe");
        start.ArgumentList.Add(executableName);
        Process.Start(start);
        shutdown();
    }

    private static void ExtractSafely(string zip, string destination)
    {
        using var archive = ZipFile.OpenRead(zip);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包含有非法路径");
            }
        }
        ZipFile.ExtractToDirectory(zip, destination);
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private const string Script = """
        param(
            [int]$processId,
            [string]$source,
            [string]$target,
            [string]$exe
        )
        Wait-Process -Id $processId -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
        $backup = Join-Path $env:TEMP ("TokenView-update-backup-" + [guid]::NewGuid().ToString("N"))
        try {
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            Copy-Item -Path (Join-Path $target "*") -Destination $backup -Recurse -Force
            Copy-Item -Path (Join-Path $source "*") -Destination $target -Recurse -Force
            Remove-Item -LiteralPath $source -Recurse -Force -ErrorAction SilentlyContinue
            Start-Process -FilePath (Join-Path $target $exe) -WorkingDirectory $target
            Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
        } catch {
            if (Test-Path -LiteralPath $backup) {
                Copy-Item -Path (Join-Path $backup "*") -Destination $target -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
            }
            if (Test-Path -LiteralPath (Join-Path $target $exe)) {
                Start-Process -FilePath (Join-Path $target $exe) -WorkingDirectory $target
            }
        }
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;
}
