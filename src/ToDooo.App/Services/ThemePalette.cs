using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ToDooo.Services;

/// <summary>
/// 应用级色板：深/浅两套颜色直接写进共享画刷实例。
/// 应用级 ThemeDictionaries 不会跟随窗口的 RequestedTheme，所以主题和透明度都走这里改颜色，
/// 已经引用这些画刷的控件会立即跟着变。
/// </summary>
public static class ThemePalette
{
    private static readonly Dictionary<string, Color> DarkSolid = new()
    {
        ["TextPrimaryBrush"] = Rgb(0xF5, 0xF5, 0xF5),
        ["TextSecondaryBrush"] = Rgb(0xC9, 0xD0, 0xD8),
        ["TextMutedBrush"] = Rgb(0x93, 0x9D, 0xA9),
        ["DividerBrush"] = Argb(0x1A, 0xFF, 0xFF, 0xFF),
        ["OverdueBrush"] = Rgb(0xF2, 0xC9, 0x4C),
        ["HoverBrush"] = Argb(0x14, 0xFF, 0xFF, 0xFF),
        ["PressedBrush"] = Argb(0x20, 0xFF, 0xFF, 0xFF),
        ["LayerFillSecondaryBrush"] = Argb(0x0B, 0xFF, 0xFF, 0xFF),
        ["LayerStrokeBrush"] = Argb(0x14, 0xFF, 0xFF, 0xFF),
        ["ItemCardBrush"] = Argb(0x12, 0xFF, 0xFF, 0xFF),
        ["ItemCardHoverBrush"] = Argb(0x1E, 0xFF, 0xFF, 0xFF),
        ["ItemCardStrokeBrush"] = Argb(0x0C, 0xFF, 0xFF, 0xFF),
        ["SegmentBarBrush"] = Argb(0x0A, 0xFF, 0xFF, 0xFF),
        ["SegmentSelectedBrush"] = Argb(0x26, 0xFF, 0xFF, 0xFF),
        ["NavSelectedBrush"] = Argb(0x30, 0xFF, 0xFF, 0xFF),
        ["OverlaySurfaceBrush"] = Rgb(0x2A, 0x30, 0x38),
        ["NeutralCheckFillBrush"] = Rgb(0xED, 0xEF, 0xF2),
        ["CheckGlyphBrush"] = Rgb(0x26, 0x2B, 0x33),
        ["CheckBorderBrush"] = Rgb(0x7A, 0x82, 0x8E),
    };

    private static readonly Dictionary<string, Color> LightSolid = new()
    {
        ["TextPrimaryBrush"] = Rgb(0x1B, 0x1B, 0x1F),
        ["TextSecondaryBrush"] = Rgb(0x43, 0x46, 0x4C),
        ["TextMutedBrush"] = Rgb(0x6C, 0x70, 0x77),
        ["DividerBrush"] = Argb(0x14, 0x00, 0x00, 0x00),
        ["OverdueBrush"] = Rgb(0xA1, 0x5E, 0x00),
        ["HoverBrush"] = Argb(0x0F, 0x00, 0x00, 0x00),
        ["PressedBrush"] = Argb(0x1A, 0x00, 0x00, 0x00),
        ["LayerFillSecondaryBrush"] = Argb(0x08, 0x00, 0x00, 0x00),
        ["LayerStrokeBrush"] = Argb(0x12, 0x00, 0x00, 0x00),
        ["ItemCardBrush"] = Argb(0x0D, 0x00, 0x00, 0x00),
        ["ItemCardHoverBrush"] = Argb(0x17, 0x00, 0x00, 0x00),
        ["ItemCardStrokeBrush"] = Argb(0x0F, 0x00, 0x00, 0x00),
        ["SegmentBarBrush"] = Argb(0x08, 0x00, 0x00, 0x00),
        ["SegmentSelectedBrush"] = Argb(0x1A, 0x00, 0x00, 0x00),
        ["NavSelectedBrush"] = Argb(0x1E, 0x00, 0x00, 0x00),
        ["OverlaySurfaceBrush"] = Rgb(0xF3, 0xF3, 0xF6),
        ["NeutralCheckFillBrush"] = Rgb(0x2A, 0x2A, 0x2E),
        ["CheckGlyphBrush"] = Rgb(0xFF, 0xFF, 0xFF),
        ["CheckBorderBrush"] = Rgb(0x8A, 0x8A, 0x92),
    };

    private static readonly Color[] DarkCard =
    {
        Argb(0x5E, 0x2E, 0x34, 0x3D),
        Argb(0x54, 0x2A, 0x30, 0x38),
        Argb(0x5E, 0x26, 0x2B, 0x33),
    };

    private static readonly Color[] LightCard =
    {
        Argb(0x66, 0xFF, 0xFF, 0xFF),
        Argb(0x5C, 0xF7, 0xF7, 0xFA),
        Argb(0x66, 0xF0, 0xF0, 0xF4),
    };

    private static readonly Color[] DarkCardStroke =
    {
        Argb(0x18, 0xFF, 0xFF, 0xFF),
        Argb(0x0A, 0xFF, 0xFF, 0xFF),
        Argb(0x14, 0xFF, 0xFF, 0xFF),
    };

    private static readonly Color[] LightCardStroke =
    {
        Argb(0x33, 0x00, 0x00, 0x00),
        Argb(0x16, 0x00, 0x00, 0x00),
        Argb(0x26, 0x00, 0x00, 0x00),
    };

    /// <summary>
    /// 输入框的文字色和光标色走 TextControl* 这套框架资源；它们按“应用主题”解析，
    /// 不会跟着窗口的 RequestedTheme 变（浅色下光标还是白的）。这里用共享画刷覆盖掉，
    /// 光标和文字都和窗口主题一致。
    /// </summary>
    public static void ApplyTextControlPalette(ResourceDictionary resources)
    {
        var app = Application.Current.Resources;
        void Set(string key, string paletteKey)
        {
            if (!resources.ContainsKey(key))
            {
                resources[key] = app[paletteKey];
            }
        }

        Set("TextControlForeground", "TextPrimaryBrush");
        Set("TextControlForegroundFocused", "TextPrimaryBrush");
        Set("TextControlForegroundPointerOver", "TextPrimaryBrush");
        Set("TextControlForegroundDisabled", "TextMutedBrush");
        Set("TextControlPlaceholderForeground", "TextMutedBrush");
        Set("TextControlPlaceholderForegroundFocused", "TextMutedBrush");
        Set("TextControlPlaceholderForegroundPointerOver", "TextMutedBrush");
    }

    public static AcrylicBrush CreateDialogAcrylicBrush(
        bool dark, Appearance.TintMode tintMode = Appearance.TintMode.Auto)
    {
        var (tint, fallback) = Appearance.TintColors(tintMode, dark);
        return new AcrylicBrush
        {
            TintColor = tint,
            FallbackColor = fallback,
            TintOpacity = dark ? 0.75 : 0.85,
        };
    }

    public static void Apply(bool dark, double opacity,
        Appearance.TintMode tintMode = Appearance.TintMode.Auto)
    {
        var resources = Application.Current.Resources;
        foreach (var (key, color) in dark ? DarkSolid : LightSolid)
        {
            if (resources[key] is SolidColorBrush brush)
            {
                brush.Color = color;
            }
        }

        // 不透明度滑杆直接作用在卡片底色上：10% 时几乎只剩磨砂，100% 回到基线。
        var factor = opacity >= 0.4
            ? 0.1 + 0.9 * Math.Clamp((opacity - 0.4) / 0.6, 0.0, 1.0)
            : 0.1 * Math.Clamp((opacity - 0.1) / 0.3, 0.0, 1.0);
        var card = dark ? DarkCard : LightCard;
        SetGradient(resources, "CardBrush", card, factor);
        SetGradient(resources, "CardStrokeBrush", dark ? DarkCardStroke : LightCardStroke, 1.0);
        // 弹框用独立磨砂刷：太透会看不清，这里不跟随窗口透明度一起减淡。
        if (resources["DialogAcrylicBrush"] is AcrylicBrush dialogAcrylic)
        {
            var (dialogTint, dialogFallback) = Appearance.TintColors(tintMode, dark);
            dialogAcrylic.TintColor = dialogTint;
            dialogAcrylic.FallbackColor = dialogFallback;
            dialogAcrylic.TintOpacity = dark ? 0.75 : 0.85;
        }
    }

    private static void SetGradient(ResourceDictionary resources, string key, Color[] colors, double factor)
    {
        if (resources[key] is not LinearGradientBrush gradient || gradient.GradientStops.Count < colors.Length)
        {
            return;
        }

        for (var i = 0; i < colors.Length; i++)
        {
            var color = colors[i];
            var alpha = (byte)Math.Round(color.A * factor);
            gradient.GradientStops[i].Color = Color.FromArgb(alpha, color.R, color.G, color.B);
        }
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
}