using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ToDooo.Interop;

/// <summary>
/// 显示或大跨度调整窗口尺寸前先通过 DWMWA_CLOAK 隐藏视觉输出，
/// 等 XAML 与亚克力完成首帧提交后再解除，避免露出 WinUI 3 的黑底过渡帧。
/// </summary>
internal static class WindowCloak
{
    private static readonly Dictionary<IntPtr, Action> PendingCancels = new();

    public static void ShowCloaked(
        IntPtr hwnd,
        FrameworkElement root,
        DispatcherQueue dispatcher,
        Action showAction,
        Action? onRevealed = null,
        int waitFrames = 2)
    {
        Cancel(hwnd, reveal: false);

        if (hwnd == IntPtr.Zero)
        {
            showAction();
            onRevealed?.Invoke();
            return;
        }

        NativeMethods.SetWindowCloaked(hwnd, true);
        showAction();

        var finished = false;
        var remainingFrames = Math.Max(1, waitFrames);
        EventHandler<object>? onRendering = null;
        DispatcherQueueTimer? fallbackTimer = null;

        void Complete(bool invokeCallback)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            PendingCancels.Remove(hwnd);

            if (onRendering is not null)
            {
                CompositionTarget.Rendering -= onRendering;
            }

            fallbackTimer?.Stop();

            if (NativeMethods.IsWindow(hwnd))
            {
                NativeMethods.SetWindowCloaked(hwnd, false);
            }

            if (invokeCallback)
            {
                onRevealed?.Invoke();
            }
        }

        onRendering = (_, _) =>
        {
            if (!root.IsLoaded)
            {
                return;
            }

            remainingFrames--;
            if (remainingFrames <= 0)
            {
                Complete(invokeCallback: true);
            }
        };

        fallbackTimer = dispatcher.CreateTimer();
        fallbackTimer.IsRepeating = false;
        fallbackTimer.Interval = TimeSpan.FromMilliseconds(160);
        fallbackTimer.Tick += (_, _) => Complete(invokeCallback: true);

        PendingCancels[hwnd] = () => Complete(invokeCallback: false);
        CompositionTarget.Rendering += onRendering;
        fallbackTimer.Start();
    }

    public static void Cancel(IntPtr hwnd, bool reveal = true)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (PendingCancels.TryGetValue(hwnd, out var cancel))
        {
            cancel();
        }

        if (reveal && NativeMethods.IsWindow(hwnd))
        {
            NativeMethods.SetWindowCloaked(hwnd, false);
        }
    }
}
