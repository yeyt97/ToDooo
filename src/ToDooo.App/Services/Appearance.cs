using Microsoft.UI.Xaml;
using Windows.UI;

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

    /// <summary>
    /// 底色方案。窗口透明度拉很高时亚克力几乎全透，背后的白色应用会直接把窗口冲亮，
    /// 深色模式下白字就看不清了。这里用一层带色的 tint 把背景压住。
    /// </summary>
    public enum TintMode
    {
        /// <summary>跟随主题：深色深灰、浅色浅蓝灰。</summary>
        Auto = 0,
        /// <summary>深灰（近黑）。</summary>
        Graphite = 1,
        /// <summary>藏青，偏冷。</summary>
        Slate = 2,
        /// <summary>暖褐，偏暖。</summary>
        Umber = 3,
    }

    public static (Color Tint, Color Fallback) TintColors(TintMode mode, bool dark) => mode switch
    {
        TintMode.Graphite => dark
            ? (Color.FromArgb(255, 0x14, 0x16, 0x1A), Color.FromArgb(255, 0x0E, 0x10, 0x13))
            : (Color.FromArgb(255, 0xE9, 0xE9, 0xEC), Color.FromArgb(255, 0xF2, 0xF2, 0xF5)),
        TintMode.Slate => dark
            ? (Color.FromArgb(255, 0x1B, 0x22, 0x30), Color.FromArgb(255, 0x13, 0x19, 0x24))
            : (Color.FromArgb(255, 0xE6, 0xEB, 0xF4), Color.FromArgb(255, 0xF0, 0xF3, 0xF9)),
        TintMode.Umber => dark
            ? (Color.FromArgb(255, 0x2B, 0x24, 0x1E), Color.FromArgb(255, 0x1F, 0x1A, 0x15))
            : (Color.FromArgb(255, 0xF1, 0xEB, 0xE3), Color.FromArgb(255, 0xF7, 0xF3, 0xED)),
        _ => dark
            ? (Color.FromArgb(255, 0x2A, 0x30, 0x38), Color.FromArgb(255, 0x20, 0x25, 0x2C))
            : (Color.FromArgb(255, 0xF2, 0xF2, 0xF7), Color.FromArgb(255, 0xF6, 0xF6, 0xF9)),
    };
}