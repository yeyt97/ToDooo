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

    public event Action? AddRequested;

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
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 1, "显示待办");
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 2, "添加待办");
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
        var startupFlags = NativeMethods.MF_STRING |
            (StartupService.IsEnabled ? NativeMethods.MF_CHECKED : 0);
        NativeMethods.AppendMenuW(menu, startupFlags, 3, "开机自启");
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 4, "退出");

        var command = NativeMethods.TrackPopupMenuEx(
            menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_NONOTIFY,
            point.X, point.Y, _hwnd, IntPtr.Zero);

        NativeMethods.DestroyMenu(menu);
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        switch (command)
        {
            case 1:
                ShowRequested?.Invoke();
                break;
            case 2:
                AddRequested?.Invoke();
                break;
            case 3:
                StartupToggled?.Invoke(!StartupService.IsEnabled);
                break;
            case 4:
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
