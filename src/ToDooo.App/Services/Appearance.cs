using Microsoft.UI.Xaml;

namespace ToDooo.Services;

/// <summary>把设置里的主题/透明度换算成 WinUI 与亚克力需要的取值。</summary>
public static class Appearance
{
    public static ElementTheme ToElementTheme(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => ElementTheme.Dark,
        ThemeMode.Light => ElementTheme.Light,
        // Application.RequestedTheme 只在启动时取一次系统值，这里显式换算，
        // 系统主题切换后重新应用才能真的跟着变。
        _ => IsSystemDark() ? ElementTheme.Dark : ElementTheme.Light,
    };

    public static bool IsDark(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => IsSystemDark(),
    };

    /// <summary>Application.RequestedTheme 不反映系统值，直接读“应用模式”注册表。</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return Application.Current.RequestedTheme == ApplicationTheme.Dark;
        }
    }

    /// <summary>10%–100% 的窗口不透明度映射到亚克力 tint：0.02–0.65。</summary>
    public static float TintForOpacity(double opacity)
    {
        var o = Math.Clamp(opacity, 0.1, 1.0);
        // 0.4 以上沿用原曲线；更低区间继续向 0 靠近，让窗口更透。
        var tint = o >= 0.4
            ? 0.05 + (o - 0.4)
            : 0.05 * (o - 0.1) / 0.3;
        return (float)Math.Clamp(tint, 0.0, 0.7);
    }
}