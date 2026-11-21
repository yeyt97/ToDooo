using System.IO;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ToDooo.Interop;
using ToDooo.Models;
using ToDooo.Services;
using ToDooo.ViewModels;
using ToDooo.Views;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT;

namespace ToDooo;

public sealed partial class MainWindow : Window
{
    private bool _sizing;
    private bool _contextMenuOpen;
    private bool _dialogOpen;

    private enum EdgeDock
    {
        None,
        Left,
        Right,
        Top,
    }

    private EdgeDock _dockEdge;
    private bool _dockRetracted;
    private NativeMethods.RECT _dockRect;
    private bool _dockCalAttached;
    private NativeMethods.RECT _dockCalRect;
    private int _dockCalAnimStartY;
    private int _dockCalAnimTargetY;
    private int _dockCalAnimLeft;
    private int _dockCalAnimWidth;
    private int _dockCalAnimHeight;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dockTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dockExpandTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dockAnimTimer;
    private PointInt32 _animStart;
    private PointInt32 _animTarget;
    private int _animStep = -1;
    private int _animWidth;
    private int _animHeight;

    private int _resizeHit;
    private NativeMethods.POINT _resizeStartCursor;
    private NativeMethods.RECT _resizeStartRect;
    private bool _resizeCalAttached;
    private NativeMethods.RECT _resizeCalStartRect;
    private bool _resizeCalOnLeft;
    private int _resizeCalGap;

    // 鼠标离开窗口时淡出，移回恢复；设置里没开就保持原样。
    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AnimateRootOpacity(1);
        UpdateResizeCursor(e.GetCurrentPoint(Root).Position);
        CancelDockRetract();
        ScheduleDockExpand();
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        AnimateRootOpacity(_settings.Current.AutoHide && _dockEdge == EdgeDock.None ? 0.25 : 1);
        Root.SetCursor(null);
        CancelDockExpand();
        ScheduleDockRetract();
    }

    // 无边框窗口的内容层拿不到系统边缘命中，这里自己按位置判断贴边并手动缩放。
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dockRetracted)
        {
            ExpandDock();
        }

        var source = e.OriginalSource as DependencyObject;
        _pressOnSubtask = IsInsideSubtask(source);

        // 输入框一失去焦点就落盘：点到框外的第一下先提交，这一下也不再触发折叠/展开。
        _pressWhileEditing = _subtaskAddBox is not null;

        if (_subtaskAddBox is { } addBox &&
            !IsInsideTagged(source, "SubtaskAddBox") &&
            !IsInsideTagged(source, "SubtaskAddCheck"))
        {
            CommitSubtaskAdd(addBox, keepOpen: false);
        }

        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var local = e.GetCurrentPoint(Root).Position;
        var hit = EdgeHit(local);
        if (hit == 0)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out _resizeStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _resizeStartRect))
        {
            return;
        }

        _resizeCalAttached = false;
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (_calendarExpanded &&
            _calendarHostWindow is not null &&
            NativeMethods.IsWindowVisible(_calendarHostWindow.Hwnd) &&
            NativeMethods.GetWindowRect(_calendarHostWindow.Hwnd, out _resizeCalStartRect) &&
            WindowSnap.IsAttachedTo(_resizeCalStartRect, _resizeStartRect, scale))
        {
            _resizeCalAttached = true;
            _resizeCalOnLeft = _resizeCalStartRect.Right <= _resizeStartRect.Left + (int)Math.Round(24 * scale);
            _resizeCalGap = _resizeCalOnLeft
                ? _resizeStartRect.Left - _resizeCalStartRect.Right
                : _resizeCalStartRect.Left - _resizeStartRect.Right;
        }

        _resizeHit = hit;
        Root.CapturePointer(e.Pointer);
        UpdateResizeCursor(local);
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var local = e.GetCurrentPoint(Root).Position;

        if (_resizeHit == 0)
        {
            UpdateResizeCursor(local);
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            var minWidth = (int)Math.Round(320 * scale);
            var minHeight = (int)Math.Round((_resizeCalAttached ? 460 : 360) * scale);
            var dx = cursor.X - _resizeStartCursor.X;
            var dy = cursor.Y - _resizeStartCursor.Y;

            var left = _resizeStartRect.Left;
            var top = _resizeStartRect.Top;
            var right = _resizeStartRect.Right;
            var bottom = _resizeStartRect.Bottom;

            if (_resizeHit is NativeMethods.HTLEFT or NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMLEFT)
            {
                left = Math.Min(_resizeStartRect.Left + dx, _resizeStartRect.Right - minWidth);
            }

            if (_resizeHit is NativeMethods.HTRIGHT or NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMRIGHT)
            {
                right = Math.Max(_resizeStartRect.Right + dx, _resizeStartRect.Left + minWidth);
            }

            if (_resizeHit is NativeMethods.HTTOP or NativeMethods.HTTOPLEFT or NativeMethods.HTTOPRIGHT)
            {
                top = Math.Min(_resizeStartRect.Top + dy, _resizeStartRect.Bottom - minHeight);
            }

            if (_resizeHit is NativeMethods.HTBOTTOM or NativeMethods.HTBOTTOMLEFT or NativeMethods.HTBOTTOMRIGHT)
            {
                bottom = Math.Max(_resizeStartRect.Bottom + dy, _resizeStartRect.Top + minHeight);
            }

            if (!_resizeCalAttached || _calendarHostWindow is null || !NativeMethods.IsWindowVisible(_calendarHostWindow.Hwnd))
            {
                NativeMethods.SetWindowPos(
                    _hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                e.Handled = true;
                return;
            }

            var calHwnd = _calendarHostWindow.Hwnd;
            var calW = _resizeCalStartRect.Right - _resizeCalStartRect.Left;
            var calH = bottom - top;
            var calLeft = _resizeCalOnLeft
                ? left - _resizeCalGap - calW
                : right + _resizeCalGap;

            var hdwp = NativeMethods.BeginDeferWindowPos(2);
            if (hdwp != IntPtr.Zero)
            {
                hdwp = NativeMethods.DeferWindowPos(
                    hdwp, _hwnd, IntPtr.Zero,
                    left, top, right - left, calH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                if (hdwp != IntPtr.Zero)
                {
                    hdwp = NativeMethods.DeferWindowPos(
                        hdwp, calHwnd, IntPtr.Zero,
                        calLeft, top, calW, calH,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }

                if (hdwp != IntPtr.Zero)
                {
                    NativeMethods.EndDeferWindowPos(hdwp);
                }
            }
            else
            {
                NativeMethods.SetWindowPos(
                    _hwnd, IntPtr.Zero, left, top, right - left, calH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                NativeMethods.SetWindowPos(
                    calHwnd, IntPtr.Zero, calLeft, top, calW, calH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
        }

        e.Handled = true;
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHit == 0)
        {
            return;
        }

        var syncCal = _resizeCalAttached;
        _resizeHit = 0;
        _resizeCalAttached = false;
        Root.ReleasePointerCapture(e.Pointer);
        SavePosition();
        if (syncCal)
        {
            _calendarHostWindow?.SaveCalendarSize();
        }

        UpdateResizeCursor(e.GetCurrentPoint(Root).Position);
        e.Handled = true;
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHit == 0)
        {
            return;
        }

        var syncCal = _resizeCalAttached;
        _resizeHit = 0;
        _resizeCalAttached = false;
        SavePosition();
        if (syncCal)
        {
            _calendarHostWindow?.SaveCalendarSize();
        }

        Root.SetCursor(null);
    }

    private int EdgeHit(Windows.Foundation.Point point)
    {
        const double edge = 8;
        var left = point.X < edge;
        var right = point.X > Root.ActualWidth - edge;
        var top = point.Y < edge;
        var bottom = point.Y > Root.ActualHeight - edge;

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

    private void UpdateResizeCursor(Windows.Foundation.Point point)
    {
        var hit = _resizeHit != 0 ? _resizeHit : EdgeHit(point);
        Root.SetCursor(hit switch
        {
            NativeMethods.HTLEFT or NativeMethods.HTRIGHT => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
            NativeMethods.HTTOP or NativeMethods.HTBOTTOM => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
            NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMRIGHT => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
            NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMLEFT => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
            _ => null,
        });
    }

    private void AnimateRootOpacity(double target)
    {
        if (Math.Abs(Root.Opacity - target) < 0.01)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, Root);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // 拖到屏幕顶部边缘附近松手就收进去，只留一条薄边；收进去后置顶，悬停边缘即可呼出。
    private void TryDockToEdge()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var midX = rect.Left + (rect.Right - rect.Left) / 2;
        var area = DisplayArea.GetFromPoint(
            new PointInt32(midX, rect.Top + 20), DisplayAreaFallback.Nearest).WorkArea;
        var threshold = (int)Math.Round(12 * scale);

        if (rect.Top - area.Y <= threshold)
        {
            DockToEdge(EdgeDock.Top, rect);
        }
        else
        {
            Undock();
        }
    }

    private void SyncDockedRects()
    {
        if (_dockEdge == EdgeDock.None || _dockRetracted || _hwnd == IntPtr.Zero ||
            !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        _dockRect = ClampRectToWorkArea(rect);
        _dockCalAttached = false;
        if (_calendarExpanded &&
            _calendarHostWindow is not null &&
            NativeMethods.IsWindowVisible(_calendarHostWindow.Hwnd) &&
            NativeMethods.GetWindowRect(_calendarHostWindow.Hwnd, out var calRect) &&
            WindowSnap.IsAttachedTo(calRect, _dockRect, NativeMethods.GetDpiForWindow(_hwnd) / 96.0))
        {
            _dockCalAttached = true;
            _dockCalRect = ClampRectToWorkArea(calRect);
        }
    }

    private void DockToEdge(EdgeDock edge, NativeMethods.RECT rect)
    {
        _dockEdge = edge;
        _dockRect = ClampRectToWorkArea(rect);
        _dockRetracted = false;
        SyncDockedRects();

        if (_dockCalAttached && _calendarHostWindow is not null)
        {
            var calW = _dockCalRect.Right - _dockCalRect.Left;
            var calH = _dockCalRect.Bottom - _dockCalRect.Top;
            var calTop = _dockRect.Top;
            _dockCalRect = new NativeMethods.RECT
            {
                Left = _dockCalRect.Left,
                Top = calTop,
                Right = _dockCalRect.Left + calW,
                Bottom = calTop + calH,
            };
            NativeMethods.SetWindowPos(
                _calendarHostWindow.Hwnd, NativeMethods.HWND_TOPMOST,
                _dockCalRect.Left, _dockCalRect.Top, calW, calH,
                NativeMethods.SWP_NOACTIVATE);
        }

        Root.Opacity = 1;
        RetractDock();
    }

    private void RetractDock()
    {
        if (_dockEdge == EdgeDock.None || _dockRetracted)
        {
            return;
        }

        SyncDockedRects();
        _dockRetracted = true;
        AnimateRootOpacity(1);
        SlideTo(GetRetractPoint(), animate: true);
    }

    private void ExpandDock()
    {
        CancelDockExpand();
        if (_dockEdge == EdgeDock.None || !_dockRetracted)
        {
            return;
        }

        _dockRetracted = false;
        CancelDockAnimation();
        Root.Opacity = 1;
        _dockRect = ClampRectToWorkArea(_dockRect);

        if (!_dockCalAttached || !_calendarExpanded || _calendarHostWindow is null ||
            !NativeMethods.IsWindowVisible(_calendarHostWindow.Hwnd))
        {
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_TOPMOST,
                _dockRect.Left, _dockRect.Top,
                _dockRect.Right - _dockRect.Left, _dockRect.Bottom - _dockRect.Top,
                NativeMethods.SWP_NOACTIVATE);
            return;
        }

        _dockCalRect = ClampRectToWorkArea(_dockCalRect);
        var calHwnd = _calendarHostWindow.Hwnd;
        var hdwp = NativeMethods.BeginDeferWindowPos(2);
        if (hdwp != IntPtr.Zero)
        {
            hdwp = NativeMethods.DeferWindowPos(
                hdwp, _hwnd, NativeMethods.HWND_TOPMOST,
                _dockRect.Left, _dockRect.Top,
                _dockRect.Right - _dockRect.Left, _dockRect.Bottom - _dockRect.Top,
                NativeMethods.SWP_NOACTIVATE);
            if (hdwp != IntPtr.Zero)
            {
                hdwp = NativeMethods.DeferWindowPos(
                    hdwp, calHwnd, NativeMethods.HWND_TOPMOST,
                    _dockCalRect.Left, _dockCalRect.Top,
                    _dockCalRect.Right - _dockCalRect.Left, _dockCalRect.Bottom - _dockCalRect.Top,
                    NativeMethods.SWP_NOACTIVATE);
            }

            if (hdwp != IntPtr.Zero)
            {
                NativeMethods.EndDeferWindowPos(hdwp);
            }
        }
        else
        {
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_TOPMOST,
                _dockRect.Left, _dockRect.Top,
                _dockRect.Right - _dockRect.Left, _dockRect.Bottom - _dockRect.Top,
                NativeMethods.SWP_NOACTIVATE);
            NativeMethods.SetWindowPos(
                calHwnd, NativeMethods.HWND_TOPMOST,
                _dockCalRect.Left, _dockCalRect.Top,
                _dockCalRect.Right - _dockCalRect.Left, _dockCalRect.Bottom - _dockCalRect.Top,
                NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void Undock()
    {
        if (_dockEdge == EdgeDock.None)
        {
            return;
        }

        _dockEdge = EdgeDock.None;
        _dockRetracted = false;
        _dockCalAttached = false;
        CancelDockRetract();
        CancelDockAnimation();
        Root.Opacity = 1;

        if (_started)
        {
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            if (_calendarExpanded && _calendarHostWindow is not null &&
                NativeMethods.IsWindowVisible(_calendarHostWindow.Hwnd))
            {
                NativeMethods.SetWindowPos(
                    _calendarHostWindow.Hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            ApplyCornerMask();
        }
    }

    private NativeMethods.RECT ClampRectToWorkArea(NativeMethods.RECT rect)
    {
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var area = DisplayArea.GetFromPoint(
            new PointInt32(rect.Left + width / 2, rect.Top + Math.Min(20, height / 2)),
            DisplayAreaFallback.Nearest).WorkArea;

        var left = Math.Clamp(rect.Left, area.X, area.X + Math.Max(0, area.Width - width));
        var top = Math.Clamp(rect.Top, area.Y, area.Y + Math.Max(0, area.Height - height));
        return new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height,
        };
    }

    private PointInt32 GetRetractPoint()
    {
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var strip = Math.Max(2, (int)Math.Round(6 * scale));
        var width = _dockRect.Right - _dockRect.Left;
        var height = _dockRect.Bottom - _dockRect.Top;
        var area = DisplayArea.GetFromPoint(
            new PointInt32(_dockRect.Left + width / 2, _dockRect.Top + 20),
            DisplayAreaFallback.Nearest).WorkArea;

        return _dockEdge switch
        {
            EdgeDock.Left => new PointInt32(area.X - width + strip, _dockRect.Top),
            EdgeDock.Right => new PointInt32(area.X + area.Width - strip, _dockRect.Top),
            EdgeDock.Top => new PointInt32(_dockRect.Left, area.Y - height + strip),
            _ => new PointInt32(_dockRect.Left, _dockRect.Top),
        };
    }

    private void SlideTo(PointInt32 target, bool animate)
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var current))
        {
            return;
        }

        var width = current.Right - current.Left;
        var height = current.Bottom - current.Top;

        NativeMethods.RECT calCurrent = default;
        var moveCalendar = _dockCalAttached && _calendarExpanded &&
            _calendarHostWindow is not null &&
            NativeMethods.IsWindowVisible(_calendarHostWindow.Hwnd) &&
            NativeMethods.GetWindowRect(_calendarHostWindow.Hwnd, out calCurrent);
        if (moveCalendar)
        {
            var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            var strip = Math.Max(2, (int)Math.Round(6 * scale));
            var area = DisplayArea.GetFromPoint(
                new PointInt32(_dockRect.Left + width / 2, _dockRect.Top + 20),
                DisplayAreaFallback.Nearest).WorkArea;
            _dockCalAnimLeft = calCurrent.Left;
            _dockCalAnimWidth = calCurrent.Right - calCurrent.Left;
            _dockCalAnimHeight = calCurrent.Bottom - calCurrent.Top;
            _dockCalAnimStartY = calCurrent.Top;
            _dockCalAnimTargetY = area.Y - _dockCalAnimHeight + strip;
        }

        if (!animate)
        {
            CancelDockAnimation();
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_TOPMOST, target.X, target.Y, width, height,
                NativeMethods.SWP_NOACTIVATE);
            if (moveCalendar)
            {
                NativeMethods.SetWindowPos(
                    _calendarHostWindow!.Hwnd, NativeMethods.HWND_TOPMOST,
                    _dockCalAnimLeft, _dockCalAnimTargetY, _dockCalAnimWidth, _dockCalAnimHeight,
                    NativeMethods.SWP_NOACTIVATE);
            }

            return;
        }

        _animStart = new PointInt32(current.Left, current.Top);
        _animTarget = target;
        _animWidth = width;
        _animHeight = height;
        _animStep = 0;

        if (_dockAnimTimer is null)
        {
            _dockAnimTimer = _dispatcher.CreateTimer();
            _dockAnimTimer.Interval = TimeSpan.FromMilliseconds(12);
            _dockAnimTimer.Tick += OnDockAnimationTick;
        }

        _dockAnimTimer.Start();
    }

    private void OnDockAnimationTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        const int totalSteps = 8;
        _animStep++;
        var x = _animStart.X + (_animTarget.X - _animStart.X) * _animStep / totalSteps;
        var y = _animStart.Y + (_animTarget.Y - _animStart.Y) * _animStep / totalSteps;

        var moveCalendar = _dockCalAttached && _calendarExpanded &&
            _calendarHostWindow is not null &&
            NativeMethods.IsWindowVisible(_calendarHostWindow.Hwnd);

        if (!moveCalendar)
        {
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_TOPMOST, x, y, _animWidth, _animHeight,
                NativeMethods.SWP_NOACTIVATE);
        }
        else
        {
            var calY = _dockCalAnimStartY + (_dockCalAnimTargetY - _dockCalAnimStartY) * _animStep / totalSteps;
            var hdwp = NativeMethods.BeginDeferWindowPos(2);
            if (hdwp != IntPtr.Zero)
            {
                hdwp = NativeMethods.DeferWindowPos(
                    hdwp, _hwnd, NativeMethods.HWND_TOPMOST,
                    x, y, _animWidth, _animHeight, NativeMethods.SWP_NOACTIVATE);
                if (hdwp != IntPtr.Zero)
                {
                    hdwp = NativeMethods.DeferWindowPos(
                        hdwp, _calendarHostWindow!.Hwnd, NativeMethods.HWND_TOPMOST,
                        _dockCalAnimLeft, calY, _dockCalAnimWidth, _dockCalAnimHeight,
                        NativeMethods.SWP_NOACTIVATE);
                }

                if (hdwp != IntPtr.Zero)
                {
                    NativeMethods.EndDeferWindowPos(hdwp);
                }
            }
            else
            {
                NativeMethods.SetWindowPos(
                    _hwnd, NativeMethods.HWND_TOPMOST, x, y, _animWidth, _animHeight,
                    NativeMethods.SWP_NOACTIVATE);
                NativeMethods.SetWindowPos(
                    _calendarHostWindow!.Hwnd, NativeMethods.HWND_TOPMOST,
                    _dockCalAnimLeft, calY, _dockCalAnimWidth, _dockCalAnimHeight,
                    NativeMethods.SWP_NOACTIVATE);
            }
        }

        if (_animStep >= totalSteps)
        {
            CancelDockAnimation();
        }
    }

    private void CancelDockAnimation()
    {
        _dockAnimTimer?.Stop();
        _animStep = -1;
    }

    private bool IsCursorInsideDockedGroup()
    {
        if (!NativeMethods.GetCursorPos(out var cursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out var mainRect))
        {
            return false;
        }

        static bool Contains(NativeMethods.RECT r, NativeMethods.POINT p) =>
            p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;

        if (Contains(mainRect, cursor))
        {
            return true;
        }

        if (_calendarExpanded &&
            _calendarHostWindow is not null &&
            NativeMethods.IsWindowVisible(_calendarHostWindow.Hwnd) &&
            NativeMethods.GetWindowRect(_calendarHostWindow.Hwnd, out var calRect))
        {
            if (Contains(calRect, cursor))
            {
                return true;
            }

            if (WindowSnap.IsAttachedTo(calRect, mainRect, NativeMethods.GetDpiForWindow(_hwnd) / 96.0))
            {
                var gapLeft = Math.Min(mainRect.Right, calRect.Right);
                var gapRight = Math.Max(mainRect.Left, calRect.Left);
                var gapTop = Math.Max(mainRect.Top, calRect.Top);
                var gapBottom = Math.Min(mainRect.Bottom, calRect.Bottom);
                if (cursor.X >= gapLeft && cursor.X <= gapRight &&
                    cursor.Y >= gapTop && cursor.Y <= gapBottom)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void ScheduleDockRetract()
    {
        if (_dockEdge == EdgeDock.None || _dockRetracted ||
            _editor is not null || _subtaskEditor is not null || _settingsWindow is not null ||
            _contextMenuOpen || _dialogOpen || _trayMenuOpen ||
            _sizing || _resizeHit != 0 || _dragging)
        {
            return;
        }

        if (_dockTimer is null)
        {
            _dockTimer = _dispatcher.CreateTimer();
            _dockTimer.Interval = TimeSpan.FromMilliseconds(500);
            _dockTimer.Tick += (_, _) =>
            {
                _dockTimer.Stop();
                if (_dockRetracted || _dockEdge == EdgeDock.None ||
                    _editor is not null || _subtaskEditor is not null || _settingsWindow is not null ||
                    _contextMenuOpen || _dialogOpen || _trayMenuOpen ||
                    _sizing || _resizeHit != 0 || _dragging)
                {
                    return;
                }

                if (_calendarHostWindow is { } cal &&
                    (cal.IsDraggingOrResizing || cal.Calendar.IsPreviewOpen))
                {
                    return;
                }

                if (IsCursorInsideDockedGroup())
                {
                    return;
                }

                RetractDock();
            };
        }

        _dockTimer.Start();
    }

    private void CancelDockRetract() => _dockTimer?.Stop();

    private void ScheduleDockExpand()
    {
        if (_dockEdge == EdgeDock.None || !_dockRetracted)
        {
            return;
        }

        if (_dockExpandTimer is null)
        {
            _dockExpandTimer = _dispatcher.CreateTimer();
            _dockExpandTimer.Interval = TimeSpan.FromMilliseconds(120);
            _dockExpandTimer.Tick += (_, _) =>
            {
                _dockExpandTimer.Stop();
                if (_dockEdge != EdgeDock.None && _dockRetracted && IsCursorInsideDockedGroup())
                {
                    ExpandDock();
                }
            };
        }

        _dockExpandTimer.Start();
    }

    private void CancelDockExpand() => _dockExpandTimer?.Stop();

    // 收边时主窗口是置顶的，设置/编辑窗口也得置顶，否则会被压在下面。
    private void TopmostDialogIfDocked(Window dialog, bool forceTopmost = false)
    {
        if (!forceTopmost && _dockEdge == EdgeDock.None)
        {
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(dialog);
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
