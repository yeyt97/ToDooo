using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ToDooo.Interop;
using Windows.Foundation;

namespace ToDooo.Helpers;

/// <summary>
/// 统一鼠标与触屏/触控笔的标题栏拖拽，不修改任何窗口样式。
/// - 鼠标：继续走原有 GetCursorPos 绝对坐标逻辑；
/// - 触屏/笔：使用低优先级队列合并帧内位移，等窗口坐标更新后再采样下一帧，
///   彻底避免高频触控事件与 SetWindowPos 异步导致的回弹抽搐。
/// </summary>
internal static class WindowDragHelper
{
    private static readonly Dictionary<IntPtr, TouchDragState> TouchStates = new();

    private sealed class TouchDragState
    {
        public uint PointerId;
        public Point StartLocal;
        public Point LatestLocal;
        public bool MovePending;
        public UIElement? CaptureElement;
    }

    public static bool IsPrimaryPointerPressed(UIElement relativeTo, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(relativeTo);
        return point.PointerDeviceType == PointerDeviceType.Mouse
            ? point.Properties.IsLeftButtonPressed
            : point.IsInContact;
    }

    public static bool TryBeginTouchDrag(IntPtr hwnd, UIElement root, UIElement captureElement, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(root);
        if (point.PointerDeviceType == PointerDeviceType.Mouse || !point.IsInContact)
        {
            return false;
        }

        var state = new TouchDragState
        {
            PointerId = e.Pointer.PointerId,
            StartLocal = point.Position,
            LatestLocal = point.Position,
            CaptureElement = captureElement,
        };
        TouchStates[hwnd] = state;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    public static bool TryMoveTouchDrag(IntPtr hwnd, UIElement root, PointerRoutedEventArgs e, Action<int, int>? customMove = null)
    {
        if (!TouchStates.TryGetValue(hwnd, out var state) || e.Pointer.PointerId != state.PointerId)
        {
            return false;
        }

        state.LatestLocal = e.GetCurrentPoint(root).Position;
        e.Handled = true;

        if (state.MovePending)
        {
            return true;
        }

        state.MovePending = true;
        root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            state.MovePending = false;
            if (!TouchStates.TryGetValue(hwnd, out var current) || !ReferenceEquals(current, state))
            {
                return;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return;
            }

            var scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(hwnd) / 96.0);
            var dx = (int)Math.Round((state.LatestLocal.X - state.StartLocal.X) * scale);
            var dy = (int)Math.Round((state.LatestLocal.Y - state.StartLocal.Y) * scale);
            if (dx == 0 && dy == 0)
            {
                return;
            }

            var newX = rect.Left + dx;
            var newY = rect.Top + dy;
            if (customMove is not null)
            {
                customMove(newX, newY);
            }
            else
            {
                NativeMethods.SetWindowPos(
                    hwnd, IntPtr.Zero,
                    newX, newY,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
        });

        return true;
    }

    public static bool TryEndTouchDrag(IntPtr hwnd, PointerRoutedEventArgs e)
    {
        if (!TouchStates.TryGetValue(hwnd, out var state) || e.Pointer.PointerId != state.PointerId)
        {
            return false;
        }

        TouchStates.Remove(hwnd);
        state.CaptureElement?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        return true;
    }

    public static void CancelTouchDrag(IntPtr hwnd) => TouchStates.Remove(hwnd);

    public static bool TryGetScreenPoint(IntPtr hwnd, UIElement root, PointerRoutedEventArgs e, out NativeMethods.POINT screenPoint) =>
        NativeMethods.GetCursorPos(out screenPoint);
}
