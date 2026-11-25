using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ToDooo.Services;
using Windows.UI;
using WinRT;

namespace ToDooo.Interop;

internal static class WindowAcrylic
{
    /// <summary>
    /// 给窗口套上和主界面同一套磨砂（亚克力）。
    /// 返回值必须在窗口存活期间持有，否则背景材质会被回收、窗口变纯色。
    /// </summary>
    public static DesktopAcrylicController? Apply(
        Window window,
        float tintOpacity = 0.42f,
        bool dark = true,
        SystemBackdropConfiguration? configuration = null,
        Appearance.TintMode tintMode = Appearance.TintMode.Auto,
        string? customTintHex = null)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return null;
        }

        var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        ApplyTint(controller, tintOpacity, dark, tintMode, customTintHex);

        controller.AddSystemBackdropTarget(
            ((object)window).As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(configuration ?? new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        });

        return controller;
    }

    public static void DetachAndDispose(
        ref DesktopAcrylicController? controller,
        ref SystemBackdropConfiguration? configuration)
    {
        if (controller is not null)
        {
            try
            {
                controller.RemoveAllSystemBackdropTargets();
                controller.Dispose();
            }
            catch
            {
            }

            controller = null;
        }

        configuration = null;
    }

    /// <summary>运行中改透明度/主题：直接改控制器属性即可，不需要重建。</summary>
    public static void ApplyTint(DesktopAcrylicController controller, float tintOpacity, bool dark,
        Appearance.TintMode tintMode = Appearance.TintMode.Auto, string? customTintHex = null)
    {
        var (tint, fallback) = Appearance.CustomTintColors(customTintHex, dark)
            ?? Appearance.TintColors(tintMode, dark);
        controller.TintColor = tint;
        controller.FallbackColor = fallback;

        // 不透明度越高，tint 与亮度层都更「实」，窗口越不透明；越低越透。
        var effectiveTint = dark ? Math.Max(tintOpacity, 0.26f) : tintOpacity;
        controller.TintOpacity = effectiveTint;

        // 亮度层与不透明度同向：越高越实（底色更明显），越低越透。
        var luminosity = (dark ? 0.06f : 0.12f) + effectiveTint * 0.90f;
        controller.LuminosityOpacity = Math.Clamp(
            luminosity,
            dark ? 0.06f : 0.10f,
            dark ? 0.80f : 0.88f);
    }
}