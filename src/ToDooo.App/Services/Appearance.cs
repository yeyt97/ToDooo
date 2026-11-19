using Microsoft.UI.Xaml;

namespace ToDooo.Services;

/// <summary>把设置里的主题/透明度换算成 WinUI 与亚克力需要的取值。</summary>
public static class Appearance
{
    public static ElementTheme ToElementTheme(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => ElementTheme.Dark,
        ThemeMode.Light => ElementTheme.Light,
        _ => ElementTheme.Default,
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

    /// <summary>40%–100% 的“不透明度”映射到亚克力 tint：0.10–0.58。</summary>
    public static float TintForOpacity(double opacity)
        => (float)Math.Clamp(0.05 + (Math.Clamp(opacity, 0.4, 1.0) - 0.4) * 1.0, 0.05, 0.7);
}