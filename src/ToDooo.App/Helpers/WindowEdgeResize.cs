using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ToDooo.Controls;
using ToDooo.Interop;

namespace ToDooo.Helpers;

/// <summary>
/// 无边框窗口的边缘缩放：命中测试、拖动改尺寸、光标形状。
/// 窗口样式里剥掉了 WS_THICKFRAME，系统不再提供缩放边，需要自己画一层。
/// </summary>
internal sealed class WindowEdgeResize
{
    private const double Edge = 8;

    private readonly CursorGrid _root;
    private readonly IntPtr _hwnd;
    private readonly Func<int> _minWidth;
    private readonly Func<int> _minHeight;

    private int _hit;
    private NativeMethods.POINT _startCursor;
    private NativeMethods.RECT _startRect;

    public bool IsResizing => _hit != 0;

    /// <summary>缩放结束；宿主可借此保存尺寸。</summary>
    public event Action? Resized;

    public WindowEdgeResize(CursorGrid root, IntPtr hwnd,
        Func<int> minWidth, Func<int> minHeight)
    {
        _root = root;
        _hwnd = hwnd;
        _minWidth = minWidth;
        _minHeight = minHeight;

        _root.PointerEntered += (_, e) => UpdateCursor(e);
        _root.PointerExited += (_, _) =>
        {
            if (_hit == 0)
            {
                _root.SetCursor(null);
            }
        };
    }

    /// <summary>指针在边缘上按下时接管缩放；返回 true 表示已处理，宿主不要再当拖动。</summary>
    public bool TryBegin(PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed)
        {
            return false;
        }

        var hit = EdgeHit(e.GetCurrentPoint(_root).Position);
        if (hit == 0 ||
            !NativeMethods.GetCursorPos(out _startCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _startRect))
        {
            return false;
        }

        _hit = hit;
        _root.CapturePointer(e.Pointer);
        UpdateCursor(e);
        e.Handled = true;
        return true;
    }

    /// <summary>缩放中移动指针时改窗口尺寸；返回 true 表示已处理。</summary>
    public bool TryMove(PointerRoutedEventArgs e)
    {
        if (_hit == 0)
        {
            UpdateCursor(e);
            return false;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return true;
        }

        var dx = cursor.X - _startCursor.X;
        var dy = cursor.Y - _startCursor.Y;
        var left = _startRect.Left;
        var top = _startRect.Top;
        var right = _startRect.Right;
        var bottom = _startRect.Bottom;
        var minWidth = _minWidth();
        var minHeight = _minHeight();

        if (_hit is NativeMethods.HTLEFT or NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMLEFT)
        {
            left = Math.Min(_startRect.Left + dx, _startRect.Right - minWidth);
        }

        if (_hit is NativeMethods.HTRIGHT or NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMRIGHT)
        {
            right = Math.Max(_startRect.Right + dx, _startRect.Left + minWidth);
        }

        if (_hit is NativeMethods.HTTOP or NativeMethods.HTTOPLEFT or NativeMethods.HTTOPRIGHT)
        {
            top = Math.Min(_startRect.Top + dy, _startRect.Bottom - minHeight);
        }

        if (_hit is NativeMethods.HTBOTTOM or NativeMethods.HTBOTTOMLEFT or NativeMethods.HTBOTTOMRIGHT)
        {
            bottom = Math.Max(_startRect.Bottom + dy, _startRect.Top + minHeight);
        }

        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        e.Handled = true;
        return true;
    }

    /// <summary>松开指针结束缩放；返回 true 表示这次是缩放的收尾。</summary>
    public bool TryEnd(PointerRoutedEventArgs e)
    {
        if (_hit == 0)
        {
            return false;
        }

        _hit = 0;
        _root.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        Resized?.Invoke();
        return true;
    }

    public void Cancel() => _hit = 0;

    private int EdgeHit(Windows.Foundation.Point point)
    {
        var left = point.X < Edge;
        var right = point.X > _root.ActualWidth - Edge;
        var top = point.Y < Edge;
        var bottom = point.Y > _root.ActualHeight - Edge;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => NativeMethods.HTTOPLEFT,
            (_, true, true, _) => NativeMethods.HTTOPRIGHT,
            (true, _, _, true) => NativeMethods.HTBOTTOMLEFT,
            (_, true, _, true) => NativeMethods.HTBOTTOMRIGHT,
            (true, _, _, _) => NativeMethods.HTLEFT,
            (_, true, _, _) => NativeMethods.HTRIGHT,
            (_, _, true, _) => NativeMethods.HTTOP,
            (_, _, _, true) => NativeMethods.HTBOTTOM,
            _ => 0,
        };
    }

    private void UpdateCursor(PointerRoutedEventArgs e)
    {
        var hit = _hit != 0 ? _hit : EdgeHit(e.GetCurrentPoint(_root).Position);
        _root.SetCursor(hit switch
        {
            NativeMethods.HTLEFT or NativeMethods.HTRIGHT => InputSystemCursorShape.SizeWestEast,
            NativeMethods.HTTOP or NativeMethods.HTBOTTOM => InputSystemCursorShape.SizeNorthSouth,
            NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMRIGHT => InputSystemCursorShape.SizeNorthwestSoutheast,
            NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMLEFT => InputSystemCursorShape.SizeNortheastSouthwest,
            _ => null,
        });
    }
}
