using Microsoft.UI.Windowing;
using ToDooo.Interop;

namespace ToDooo.Helpers;

internal static class DpiScale
{
    /// <summary>
    /// 弹窗最终落在 owner 所在屏，逻辑尺寸要按那块屏的 DPI 换算；
    /// 创建时窗口还在默认屏，GetDpiForWindow 拿到的不是落点 DPI。
    /// </summary>
    public static double ForOwner(AppWindow? owner, IntPtr fallbackHwnd)
    {
        if (owner is not null)
        {
            var ownerHwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(owner.Id);
            if (ownerHwnd != IntPtr.Zero)
            {
                var dpi = NativeMethods.GetDpiForWindow(ownerHwnd);
                if (dpi != 0)
                {
                    return dpi / 96.0;
                }
            }
        }

        return NativeMethods.GetDpiForWindow(fallbackHwnd) / 96.0;
    }
}
