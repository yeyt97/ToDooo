using System.Diagnostics;
using System.IO;
using System.Text;

namespace ToDooo.Services;

/// <summary>
/// 应用内自动更新：安装版下载安装包静默覆盖；绿色版下载压缩包后由脚本原地解压。
/// 两种都做到「点一下就不再管」——应用自动退出、更新、重新打开，不用去开网页。
/// </summary>
public static class UpdateInstaller
{
    /// <summary>当前 exe 是不是安装到本机的版本（用于判断能否静默覆盖安装）。</summary>
    public static bool IsInstalledCopy()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return false;
            }

            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir))
            {
                return false;
            }

            // 安装包默认装到 %LocalAppData%\Programs\ToDooo
            var installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "ToDooo");

            return string.Equals(
                Path.GetFullPath(dir).TrimEnd('\\'),
                Path.GetFullPath(installed).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 静默安装并重启。调用前应先让主窗口自行退出（这里只负责拉起安装程序）。
    /// 安装程序会等本进程退出后覆盖文件，装完再启动新版本。
    /// </summary>
    public static bool TryLaunchInstaller(string setupPath)
    {
        try
        {
            if (!File.Exists(setupPath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = setupPath,
                // /SILENT 显示进度但不要求交互；/NORESTART 别让系统重启；
                // /CLOSEAPPLICATIONS 由 Inno 关掉占用文件的进程；
                // /AUTOUPDATE=1 让安装脚本在静默模式下也重新拉起应用。
                Arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS /AUTOUPDATE=1",
                UseShellExecute = true,
            };
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 绿色版自更新：拉起一个脱离本进程的脚本，等主程序退出 → 解压压缩包覆盖程序目录 → 重新启动。
    /// 必须在调用后主动退出应用，否则脚本会一直等着。
    /// </summary>
    public static bool TryLaunchPortableUpdate(string zipPath, string appDir, string exePath)
    {
        try
        {
            if (!File.Exists(zipPath) || !File.Exists(exePath) || !Directory.Exists(appDir))
            {
                return false;
            }

            var scriptPath = Path.Combine(Path.GetTempPath(), $"ToDooo-update-{Guid.NewGuid():N}.ps1");

            // 路径里可能有中文，带 BOM 才能让 Windows PowerShell 5.1 按 UTF-8 解析。
            File.WriteAllText(
                scriptPath,
                BuildPortableUpdateScript(zipPath, appDir, exePath, Environment.ProcessId),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            return Process.Start(startInfo) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>生成绿色版自更新脚本；单独拆出来便于单测检查内容。</summary>
    public static string BuildPortableUpdateScript(string zipPath, string appDir, string exePath, int processId)
    {
        static string Quote(string path) => path.Replace("'", "''");

        return string.Join(
            "\r\n",
            "$ErrorActionPreference = 'Stop'",
            $"$targetPid = {processId}",
            // 先等主程序退干净，否则 ToDooo.exe 还被占用，覆盖文件会失败。
            "while (Get-Process -Id $targetPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }",
            "Start-Sleep -Milliseconds 800",
            // 不用 Expand-Archive -Force：Windows PowerShell 5.1 的 -Force 分支会去找
            // ZipFile.ExtractToDirectory 的三参数重载，.NET Framework 上没有，直接报 MethodException。
            // 逐条 ExtractToFile(..., $true) 是 4.5 就有的 API，覆盖写、跳过目录条目都稳。
            "Add-Type -AssemblyName System.IO.Compression.FileSystem",
            $"$dest = '{Quote(appDir)}'",
            $"$archive = [System.IO.Compression.ZipFile]::OpenRead('{Quote(zipPath)}')",
            "try {",
            "  foreach ($entry in $archive.Entries) {",
            "    if ([string]::IsNullOrEmpty($entry.Name)) { continue }",
            "    $target = Join-Path $dest $entry.FullName",
            "    $parent = Split-Path -Parent $target",
            "    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }",
            "    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)",
            "  }",
            "}",
            "finally { $archive.Dispose() }",
            // 用户数据在 %LocalAppData%\ToDooo，不在程序目录，覆盖程序文件不会碰到它。
            $"Start-Process -FilePath '{Quote(exePath)}' -WorkingDirectory $dest",
            "Start-Sleep -Seconds 2",
            $"Remove-Item -LiteralPath '{Quote(zipPath)}' -Force -ErrorAction SilentlyContinue",
            "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue",
            string.Empty);
    }
}
