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
        /// <summary>森绿。</summary>
        Moss = 4,
        /// <summary>绛紫。</summary>
        Plum = 5,
        /// <summary>雾蓝。</summary>
        Mist = 6,
        /// <summary>砂岩（浅暖）。</summary>
        Sand = 7,
        /// <summary>墨黑（最深）。</summary>
        Ink = 8,
    }

    /// <summary>底色方案的中文名，与枚举顺序一致。</summary>
    public static readonly string[] TintNames =
    {
        "跟随主题", "深灰", "藏青", "暖褐", "森绿", "绛紫", "雾蓝", "砂岩", "墨黑",
    };

    /// <summary>底色方案数量上限，设置越界时按它收敛。</summary>
    public const int TintCount = 9;

    /// <summary>
    /// 底色方案的实际色值。深色一套、浅色一套，两套都保证彼此可分辨：
    /// 深色不压到全黑，浅色不冲到全白，才能看出「藏青 / 森绿 / 绛紫」的差别。
    /// </summary>
    /// <summary>
    /// 解析 #RRGGBB 形式的自定义底色，换算成深/浅两套实际色值。
    /// 用户只选一个颜色：深色窗口下压暗到不刺眼，浅色窗口下提亮到不脏。
    /// 解析失败返回 null，调用方退回预设方案。
    /// </summary>
    public static (Color Tint, Color Fallback)? CustomTintColors(string? hex, bool dark)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6 ||
            !byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(text[4..], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return null;
        }

        // 深色：保留色相但压到 28%~55% 亮度；浅色：把颜色提亮成柔和的淡色调。
        var tint = dark
            ? Scale(new Color { A = 255, R = r, G = g, B = b }, 0.42)
            : BlendWithWhite(new Color { A = 255, R = r, G = g, B = b }, 0.62);
        var fallback = dark
            ? Scale(tint, 0.78)
            : BlendWithWhite(tint, 0.45);
        return (tint, fallback);
    }

    private static Color Scale(Color color, double factor) => Color.FromArgb(
        255,
        (byte)Math.Clamp(Math.Round(color.R * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(color.G * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(color.B * factor), 0, 255));

    private static Color BlendWithWhite(Color color, double whiteRatio) => Color.FromArgb(
        255,
        (byte)Math.Clamp(Math.Round(color.R * (1 - whiteRatio) + 255 * whiteRatio), 0, 255),
        (byte)Math.Clamp(Math.Round(color.G * (1 - whiteRatio) + 255 * whiteRatio), 0, 255),
        (byte)Math.Clamp(Math.Round(color.B * (1 - whiteRatio) + 255 * whiteRatio), 0, 255));

    public static (Color Tint, Color Fallback) TintColors(TintMode mode, bool dark) => mode switch
    {
        TintMode.Graphite => dark
            ? (Color.FromArgb(255, 0x28, 0x2A, 0x30), Color.FromArgb(255, 0x1C, 0x1E, 0x23))
            : (Color.FromArgb(255, 0xDD, 0xDE, 0xE2), Color.FromArgb(255, 0xE8, 0xE9, 0xED)),
        TintMode.Slate => dark
            ? (Color.FromArgb(255, 0x1E, 0x2E, 0x4A), Color.FromArgb(255, 0x14, 0x21, 0x36))
            : (Color.FromArgb(255, 0xD3, 0xDE, 0xF0), Color.FromArgb(255, 0xE2, 0xEA, 0xF7)),
        TintMode.Umber => dark
            ? (Color.FromArgb(255, 0x3D, 0x30, 0x26), Color.FromArgb(255, 0x2C, 0x22, 0x1B))
            : (Color.FromArgb(255, 0xE6, 0xD9, 0xCB), Color.FromArgb(255, 0xF2, 0xE9, 0xDE)),
        TintMode.Moss => dark
            ? (Color.FromArgb(255, 0x1F, 0x3A, 0x2A), Color.FromArgb(255, 0x16, 0x2A, 0x1E))
            : (Color.FromArgb(255, 0xD2, 0xE5, 0xD7), Color.FromArgb(255, 0xE1, 0xEF, 0xE4)),
        TintMode.Plum => dark
            ? (Color.FromArgb(255, 0x36, 0x24, 0x40), Color.FromArgb(255, 0x27, 0x1A, 0x2E))
            : (Color.FromArgb(255, 0xE3, 0xD4, 0xEA), Color.FromArgb(255, 0xEE, 0xE4, 0xF3)),
        TintMode.Mist => dark
            ? (Color.FromArgb(255, 0x1F, 0x33, 0x3D), Color.FromArgb(255, 0x16, 0x25, 0x2D))
            : (Color.FromArgb(255, 0xD3, 0xE2, 0xE8), Color.FromArgb(255, 0xE1, 0xEC, 0xF1)),
        TintMode.Sand => dark
            ? (Color.FromArgb(255, 0x3E, 0x36, 0x27), Color.FromArgb(255, 0x2D, 0x27, 0x1C))
            : (Color.FromArgb(255, 0xE8, 0xDF, 0xC8), Color.FromArgb(255, 0xF3, 0xEC, 0xDB)),
        TintMode.Ink => dark
            ? (Color.FromArgb(255, 0x12, 0x13, 0x17), Color.FromArgb(255, 0x0A, 0x0B, 0x0E))
            : (Color.FromArgb(255, 0xC9, 0xCB, 0xD2), Color.FromArgb(255, 0xD8, 0xDA, 0xE0)),
        _ => dark
            ? (Color.FromArgb(255, 0x2A, 0x30, 0x38), Color.FromArgb(255, 0x20, 0x25, 0x2C))
            : (Color.FromArgb(255, 0xE4, 0xE6, 0xEC), Color.FromArgb(255, 0xEE, 0xF0, 0xF5)),
    };
}
