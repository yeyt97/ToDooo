using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ToDooo.Helpers;

/// <summary>
/// 原生 TimePicker 模板把内部按钮的最小宽度写死成 242px，外层给多窄都会被它撑回去。
/// 模板加载后把这个限制清零，控件才能跟着所在列自适应宽度。
/// </summary>
internal static class TimePickerSizing
{
    public static void ReleaseMinWidth(object sender, RoutedEventArgs e)
    {
        if (sender is TimePicker picker)
        {
            ReleaseMinWidth(picker);
        }
    }

    /// <summary>
    /// 解除内部按钮的固定最小宽度。
    /// 控件初始是隐藏状态时（例如收起的截止区），Loaded 触发时模板还没完成实例化，
    /// 之后展开会重新套上 242px，所以显示出来之后要再调一次。
    /// </summary>
    public static void ReleaseMinWidth(TimePicker picker)
    {
        if (FindFlyoutButton(picker) is { } button)
        {
            button.MinWidth = 0;
        }
    }

    /// <summary>
    /// 控件可能先隐藏、后显示：模板在显示时才实例化，Loaded 已错过。
    /// 挂一次 SizeChanged + LayoutUpdated，在真正布局完成后再解除，
    /// 成功后自动摘掉处理器，避免每次布局都跑一遍。
    /// </summary>
    public static void EnsureReleased(TimePicker picker)
    {
        if (picker.IsLoaded)
        {
            ReleaseMinWidth(picker);
        }

        void TryRelease(object? sender, object args)
        {
            if (FindFlyoutButton(picker) is not { } button)
            {
                return;
            }

            button.MinWidth = 0;
            picker.SizeChanged -= TryRelease;
            picker.LayoutUpdated -= TryRelease;
        }

        picker.SizeChanged += TryRelease;
        picker.LayoutUpdated += TryRelease;
    }

    private static Button? FindFlyoutButton(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { Name: "FlyoutButton" } button)
            {
                return button;
            }

            if (FindFlyoutButton(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
