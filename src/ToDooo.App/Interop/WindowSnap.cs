using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace ToDooo.Interop;

/// <summary>工具窗口与主窗口之间的贴边摆放、拖动磁吸及同步跟随。</summary>
internal static class WindowSnap
{
    public const int DefaultGapLogical = 6;
    private const int SnapThresholdLogical = 14;
    private const int ScreenSnapThresholdLogical = 10;
    private const int AttachThresholdLogical = 18;

    public static (int X, int Y, bool PlacedOnLeft) PlaceNextTo(
        IntPtr hwnd,
        AppWindow? owner,
        int width,
        int height,
        bool preferLeft = true,
        int gapLogical = DefaultGapLogical,
        IntPtr avoidHwnd = default,
        bool allowOffscreen = false)
    {
        NativeMethods.RECT ownerRect;
        var hasOwner = false;
        if (owner is not null)
        {
            var ownerHwnd = Win32Interop.GetWindowFromWindowId(owner.Id);
            hasOwner = NativeMethods.GetWindowRect(ownerHwnd, out ownerRect);
        }
        else
        {
            ownerRect = default;
        }

        if (!hasOwner)
        {
            var primary = DisplayArea.GetFromWindowId(
                owner?.Id ?? Win32Interop.GetWindowIdFromWindow(hwnd),
                DisplayAreaFallback.Primary).WorkArea;
            var cx = primary.X + Math.Max(0, (primary.Width - width) / 2);
            var cy = primary.Y + Math.Max(0, (primary.Height - height) / 2);
            return (cx, cy, true);
        }

        var area = DisplayArea.GetFromPoint(
            new PointInt32((ownerRect.Left + ownerRect.Right) / 2, ownerRect.Top + 20),
            DisplayAreaFallback.Nearest).WorkArea;
        var scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(hwnd) / 96.0);
        var gap = (int)Math.Round(gapLogical * scale);

        var leftX = ownerRect.Left - width - gap;
        var rightX = ownerRect.Right + gap;
        var y = Math.Clamp(ownerRect.Top, area.Y, area.Y + Math.Max(0, area.Height - height));

        NativeMethods.RECT avoidRect = default;
        var hasAvoid = avoidHwnd != IntPtr.Zero
            && avoidHwnd != hwnd
            && NativeMethods.IsWindowVisible(avoidHwnd)
            && NativeMethods.GetWindowRect(avoidHwnd, out avoidRect);

        bool Fits(int candidateX) =>
            candidateX >= area.X && candidateX + width <= area.X + area.Width;

        bool OverlapsAvoid(int candidateX) =>
            hasAvoid
            && candidateX < avoidRect.Right
            && candidateX + width > avoidRect.Left
            && y < avoidRect.Bottom
            && y + height > avoidRect.Top;

        int chosenX;
        var first = preferLeft ? leftX : rightX;
        var second = preferLeft ? rightX : leftX;

        if (Fits(first) && !OverlapsAvoid(first))
        {
            chosenX = first;
        }
        else if (Fits(second) && !OverlapsAvoid(second))
        {
            chosenX = second;
        }
        else if (hasAvoid && Fits(avoidRect.Left - width - gap) && !OverlapsAvoid(avoidRect.Left - width - gap))
        {
            chosenX = avoidRect.Left - width - gap;
        }
        else if (hasAvoid && Fits(avoidRect.Right + gap) && !OverlapsAvoid(avoidRect.Right + gap))
        {
            chosenX = avoidRect.Right + gap;
        }
        else if (Fits(first))
        {
            chosenX = first;
        }
        else if (Fits(second))
        {
            chosenX = second;
        }
        else if (allowOffscreen)
        {
            // 屏幕放不下也要保住吸附：让窗口一部分越出屏幕，而不是夹回区里压在主窗口上。
            chosenX = first;
        }
        else
        {
            chosenX = Math.Clamp(first, area.X, area.X + Math.Max(0, area.Width - width));
        }

        var placedOnLeft = chosenX + width / 2 < (ownerRect.Left + ownerRect.Right) / 2;
        return (chosenX, y, placedOnLeft);
    }

    public static (int X, int Y) SnapDragged(
        IntPtr hwnd,
        int candidateX,
        int candidateY,
        int width,
        int height,
        IntPtr primaryAnchorHwnd,
        IntPtr secondaryAnchorHwnd = default,
        int gapLogical = DefaultGapLogical,
        bool allowLeftSnap = true,
        bool allowOffscreen = false)
    {
        var scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(hwnd) / 96.0);
        var gap = (int)Math.Round(gapLogical * scale);
        var snap = (int)Math.Round(SnapThresholdLogical * scale);
        var screenSnap = (int)Math.Round(ScreenSnapThresholdLogical * scale);

        var area = DisplayArea.GetFromPoint(
            new PointInt32(candidateX + width / 2, candidateY + Math.Min(40, height / 2)),
            DisplayAreaFallback.Nearest).WorkArea;

        var x = candidateX;
        var y = candidateY;

        void TrySnapTo(IntPtr anchorHwnd)
        {
            if (anchorHwnd == IntPtr.Zero || anchorHwnd == hwnd ||
                !NativeMethods.IsWindowVisible(anchorHwnd) ||
                !NativeMethods.GetWindowRect(anchorHwnd, out var anchor))
            {
                return;
            }

            if (y < anchor.Bottom + snap && y + height > anchor.Top - snap)
            {
                var snappedH = false;
                if (allowLeftSnap && Math.Abs((x + width) - (anchor.Left - gap)) <= snap)
                {
                    x = anchor.Left - gap - width;
                    snappedH = true;
                }
                else if (Math.Abs(x - (anchor.Right + gap)) <= snap)
                {
                    x = anchor.Right + gap;
                    snappedH = true;
                }
                else if ((allowLeftSnap && Math.Abs((x + width) - (anchor.Left - gap)) <= gap + snap) ||
                         Math.Abs(x - (anchor.Right + gap)) <= gap + snap)
                {
                    snappedH = true;
                }

                if (snappedH)
                {
                    if (Math.Abs(y - anchor.Top) <= snap)
                    {
                        y = anchor.Top;
                    }
                    else if (Math.Abs((y + height) - anchor.Bottom) <= snap)
                    {
                        y = anchor.Bottom - height;
                    }
                }
            }

            if (x < anchor.Right + snap && x + width > anchor.Left - snap)
            {
                var snappedV = false;
                if (Math.Abs((y + height) - (anchor.Top - gap)) <= snap)
                {
                    y = anchor.Top - gap - height;
                    snappedV = true;
                }
                else if (Math.Abs(y - (anchor.Bottom + gap)) <= snap)
                {
                    y = anchor.Bottom + gap;
                    snappedV = true;
                }

                if (snappedV)
                {
                    if (Math.Abs(x - anchor.Left) <= snap)
                    {
                        x = anchor.Left;
                    }
                    else if (Math.Abs((x + width) - anchor.Right) <= snap)
                    {
                        x = anchor.Right - width;
                    }
                }
            }
        }

        TrySnapTo(primaryAnchorHwnd);
        TrySnapTo(secondaryAnchorHwnd);

        if (Math.Abs(x - area.X) <= screenSnap)
        {
            x = area.X;
        }
        else if (Math.Abs((x + width) - (area.X + area.Width)) <= screenSnap)
        {
            x = area.X + area.Width - width;
        }

        if (Math.Abs(y - area.Y) <= screenSnap)
        {
            y = area.Y;
        }
        else if (Math.Abs((y + height) - (area.Y + area.Height)) <= screenSnap)
        {
            y = area.Y + area.Height - height;
        }

        if (!allowOffscreen)
        {
            x = Math.Clamp(x, area.X, area.X + Math.Max(0, area.Width - width));
            y = Math.Clamp(y, area.Y, area.Y + Math.Max(0, area.Height - height));
        }

        return (x, y);
    }

    public static bool IsAttachedTo(NativeMethods.RECT subRect, NativeMethods.RECT mainRect, double scale)
    {
        var threshold = (int)Math.Round(AttachThresholdLogical * Math.Max(1.0, scale));
        var horizontalAdjacent =
            (Math.Abs(mainRect.Left - subRect.Right) <= threshold ||
             Math.Abs(subRect.Left - mainRect.Right) <= threshold) &&
            subRect.Top < mainRect.Bottom &&
            subRect.Bottom > mainRect.Top;

        var verticalAdjacent =
            (Math.Abs(mainRect.Top - subRect.Bottom) <= threshold ||
             Math.Abs(subRect.Top - mainRect.Bottom) <= threshold) &&
            subRect.Left < mainRect.Right &&
            subRect.Right > mainRect.Left;

        return horizontalAdjacent || verticalAdjacent;
    }
}
