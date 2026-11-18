namespace ToDooo.Interop;

internal static class DesktopHost
{
    /// <summary>桌面图标层 SHELLDLL_DefView；以它为 owner 可以让顶层窗口落在应用窗口之下。</summary>
    public static IntPtr FindIconView()
    {
        var progman = NativeMethods.FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var direct = NativeMethods.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (direct != IntPtr.Zero)
        {
            return direct;
        }

        IntPtr view = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var candidate = NativeMethods.FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (candidate != IntPtr.Zero)
            {
                view = candidate;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return view;
    }

    public static IntPtr Find()
    {
        var progman = NativeMethods.FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // 通知 Explorer 生成独立 WorkerW 壁纸层；新系统上可能直接复用 Progman
        _ = NativeMethods.SendMessageTimeoutW(
            progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 800, out _);

        IntPtr host = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                var worker = NativeMethods.FindWindowExW(IntPtr.Zero, hwnd, "WorkerW", null);
                host = worker != IntPtr.Zero ? worker : hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return host != IntPtr.Zero ? host : progman;
    }
}
