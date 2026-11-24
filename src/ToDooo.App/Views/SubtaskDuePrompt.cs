using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ToDooo.Helpers;
using ToDooo.Services;
using Windows.UI;

namespace ToDooo.Views;

internal static class SubtaskDuePrompt
{
    public static async Task<(bool Confirmed, DateTime? DueAt)> ShowAsync(
        XamlRoot? xamlRoot,
        ElementTheme theme,
        DateTime? current,
        string title = "设置子任务时间",
        Appearance.TintMode tintMode = Appearance.TintMode.Auto)
    {
        if (xamlRoot is null)
        {
            return (false, null);
        }

        var now = DateTime.Now;
        var datePicker = new DatePicker
        {
            Date = new DateTimeOffset(current?.Date ?? now.Date),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var timePicker = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            SelectedTime = RoundToFiveMinutes(current?.TimeOfDay ?? now.TimeOfDay),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(datePicker);
        panel.Children.Add(timePicker);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "确定",
            SecondaryButtonText = current is null ? string.Empty : "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.RequestedTheme = theme;
        ThemePalette.ApplyTextControlPalette(dialog.Resources);
        dialog.Resources["ContentDialogBackground"] = ThemePalette.CreateDialogAcrylicBrush(theme == ElementTheme.Dark, tintMode);
        dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["CardStrokeBrush"];
        dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(8);
        dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            return (true, null);
        }

        if (result != ContentDialogResult.Primary)
        {
            return (false, null);
        }

        var date = datePicker.Date.Date;
        return (true, date + (timePicker.SelectedTime ?? TimeSpan.Zero));
    }

    private static TimeSpan RoundToFiveMinutes(TimeSpan value)
    {
        var minutes = (int)Math.Round(value.TotalMinutes / 5.0) * 5;
        return TimeSpan.FromMinutes(minutes % (24 * 60));
    }
}
