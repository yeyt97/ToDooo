using System.Reflection;

namespace ToDooo.Helpers;

/// <summary>版本信息统一取自程序集，界面不再手写版本号。</summary>
public static class AppInfo
{
    public static Version Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>形如 1.4.0，用于界面展示与更新比较。</summary>
    public static string DisplayVersion => Version.ToString(3);

    public static string AboutLine =>
        $"ToDooo {DisplayVersion} · WinUI 3\n数据目录：%LocalAppData%\\ToDooo";
}