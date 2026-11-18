using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT;

namespace ToDooo.Interop;

internal static class WindowAcrylic
{
    /// <summary>
    /// 给窗口套上和主界面同一套深色磨砂（亚克力）。
    /// 返回值必须在窗口存活期间持有，否则背景材质会被回收、窗口变纯色。
    /// </summary>
    public static DesktopAcrylicController? Apply(Window window, float tintOpacity = 0.42f)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return null;
        }

        var controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
            TintColor = Color.FromArgb(255, 0x2A, 0x30, 0x38),
            // tint 太厚会把模糊的桌面纹理糊成一片纯色，调到能透出磨砂颗粒感。
            TintOpacity = tintOpacity,
            LuminosityOpacity = 0.64f,
            FallbackColor = Color.FromArgb(255, 0x20, 0x25, 0x2C),
        };

        controller.AddSystemBackdropTarget(
            ((object)window).As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark,
        });

        return controller;
    }
}
