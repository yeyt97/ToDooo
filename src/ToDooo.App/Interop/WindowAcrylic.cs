using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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
        SystemBackdropConfiguration? configuration = null)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return null;
        }

        var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        ApplyTint(controller, tintOpacity, dark);

        controller.AddSystemBackdropTarget(
            ((object)window).As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(configuration ?? new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        });

        return controller;
    }

    /// <summary>运行中改透明度/主题：直接改控制器属性即可，不需要重建。</summary>
    public static void ApplyTint(DesktopAcrylicController controller, float tintOpacity, bool dark)
    {
        controller.TintColor = dark
            ? Color.FromArgb(255, 0x2A, 0x30, 0x38)
            : Color.FromArgb(255, 0xF2, 0xF2, 0xF7);
        // tint 太厚会把模糊的桌面纹理糊成一片纯色，调到能透出磨砂颗粒感。
        controller.TintOpacity = tintOpacity;
        controller.LuminosityOpacity = dark ? 0.64f : 0.82f;
        controller.FallbackColor = dark
            ? Color.FromArgb(255, 0x20, 0x25, 0x2C)
            : Color.FromArgb(255, 0xF6, 0xF6, 0xF9);
    }
}