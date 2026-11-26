using System.Diagnostics;
using System.IO;

namespace ToDooo.Services;

/// <summary>
/// 应用内自动更新：下载安装包 → 启动安装程序（静默）→ 安装完成后自动重新拉起应用。
/// 只在当前进程确实是安装版（exe 位于安装目录）时才走自动安装；
/// 否则退回「打开下载页」，避免把绿色版用户带进半吊子状态。
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
}
