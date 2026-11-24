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
        Appearance.TintMode tintMode = Appearance.TintMode.Auto)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return null;
        }

        var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        ApplyTint(controller, tintOpacity, dark, tintMode);

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
        Appearance.TintMode tintMode = Appearance.TintMode.Auto)
    {
        var (tint, fallback) = Appearance.TintColors(tintMode, dark);
        controller.TintColor = tint;

        // 深色模式最容易出的问题：窗口拉得很透时，背后的白色应用（Excel/浏览器）会把窗口冲成浅灰，
        // 白字直接压不住。亚克力的「亮度层」（LuminosityOpacity）决定背景亮度透上来多少，
        // 所以深色下重点抬高它的下限，tint 只做温和的兜底，避免把透明度功能阉掉。
        var effectiveTint = dark ? Math.Max(tintOpacity, 0.26f) : tintOpacity;
        controller.TintOpacity = effectiveTint;

        var luminosity = (dark ? 0.06f : 0.12f) + effectiveTint * 0.90f;
        var luminosityFloor = dark ? 0.52f : 0.12f;
        controller.LuminosityOpacity = Math.Clamp(
            Math.Max(luminosity, luminosityFloor),
            0.06f,
            dark ? 0.80f : 0.88f);
        controller.FallbackColor = fallback;
    }
}