using System.Runtime.InteropServices;
using ToDooo.Services;

namespace ToDooo.Interop;

public sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;

    private readonly IntPtr _hwnd;
    private NativeMethods.NOTIFYICONDATAW _data;
    private bool _added;

    public TrayIcon(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    public event Action? ShowRequested;

    public event Action? ExitRequested;

    public event Action<bool>? StartupToggled;

    public void Add()
    {
        if (_added)
        {
            return;
        }

        var icon = NativeMethods.ExtractIconW(IntPtr.Zero, Environment.ProcessPath ?? string.Empty, 0);
        _data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAY,
            hIcon = icon,
            szTip = "ToDooo · 待办",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref _data);
        _data.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref _data);
        _added = true;
    }

    public void ReAdd()
    {
        _added = false;
        Add();
    }

    public void HandleMessage(IntPtr lParam)
    {
        var message = (int)(lParam.ToInt64() & 0xFFFF);
        switch (message)
        {
            case NativeMethods.NIN_SELECT:
            case NativeMethods.NIN_KEYSELECT:
            case NativeMethods.WM_LBUTTONUP:
            case NativeMethods.WM_LBUTTONDBLCLK:
                ShowRequested?.Invoke();
                break;
            case NativeMethods.WM_CONTEXTMENU:
            case NativeMethods.WM_RBUTTONUP:
                ShowMenu();
                break;
        }
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added)
        {
            return;
        }

        _data.uFlags = NativeMethods.NIF_INFO;
        _data.szInfoTitle = title;
        _data.szInfo = text;
        _data.dwInfoFlags = NativeMethods.NIIF_INFO;
        _data.uTimeoutOrVersion = 5000;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref _data);
        _data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
    }

    private void ShowMenu()
    {
        NativeMethods.SetForegroundWindow(_hwnd);
        NativeMethods.GetCursorPos(out var point);

        var menu = NativeMethods.CreatePopupMenu();

        // 不用 MF_CHECKED：勾选状态会给整张菜单加一条勾选列，勾上/取消之间菜单宽度和文字位置都会变一次，
        // 看起来就是「点一下位置跳一下」。状态直接写进文案（开/关同为全角，宽度恒定），
        // 菜单本身保持纯文字项，和 DeskBox 的托盘菜单一个路子。
        var startupItem = StartupService.IsEnabled ? "开机自启 · 已开启" : "开机自启 · 已关闭";
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 1, startupItem);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 2, "退出");

        var command = NativeMethods.TrackPopupMenuEx(
            menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_NONOTIFY,
            point.X, point.Y, _hwnd, IntPtr.Zero);

        NativeMethods.DestroyMenu(menu);
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        switch (command)
        {
            case 1:
                StartupToggled?.Invoke(!StartupService.IsEnabled);
                break;
            case 2:
                ExitRequested?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref _data);
            _added = false;
        }
    }
}
