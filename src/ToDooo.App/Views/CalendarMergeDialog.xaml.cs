using System.IO;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ToDooo.Helpers;
using ToDooo.Interop;
using ToDooo.Models;
using ToDooo.Services;
using Windows.Graphics;

namespace ToDooo.Views;

/// <summary>
/// 本地已有日程、刚打开同步时的合并向导。
/// 口径固定为「云端优先」：云端已有的日程采用云端版本，本地独有的才作为新增上传。
/// </summary>
public sealed partial class CalendarMergeDialog : Window
{
    private const int LogicalWidth = 560;
    private const int LogicalHeight = 640;

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly SettingsService _settings;
    private readonly CalendarMergeService _merge;
    private readonly List<LocalCalendarChoice> _choices;
    private readonly List<CalendarSource> _cloudSources;

    private bool _finished;
    private bool _dragging;
    private bool _mergeAllToCloud;
    private readonly List<Action> _rowRefreshers = new();
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;

    public CalendarMergeDialog(
        SettingsService settings,
        CalendarMergeService merge,
        List<LocalCalendarChoice> choices,
        List<CalendarSource> cloudSources)
    {
        _settings = settings;
        _merge = merge;
        _choices = choices;
        _cloudSources = cloudSources;

        InitializeComponent();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        var dark = Appearance.IsDark((ThemeMode)settings.Current.ThemeMode);
        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        };
        _acrylic = WindowAcrylic.Apply(
            this, Appearance.TintForOpacity(settings.Current.WindowOpacity), dark, _backdropConfig,
            (Appearance.TintMode)Math.Clamp(settings.Current.WindowTintMode, 0, Appearance.TintCount - 1));
        var darkFlag = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        RenderChoices();
        Closed += OnClosed;
    }

    /// <summary>true = 合并流程已执行完（结果页可看），false = 用户取消。</summary>
    public Task<bool> Result => _result.Task;

    public MergeOutcome? Outcome { get; private set; }

    public void ShowOver(AppWindow? owner)
    {
        ApplyLogicalSize(DpiScale.ForOwner(owner, _hwnd));
        MoveCenterOver(owner);
        WindowCloak.ShowCloaked(_hwnd, Root, DispatcherQueue, () =>
        {
            _appWindow.Show();
            Activate();
        });
    }

    private void RenderChoices()
    {
        ChoiceList.Children.Clear();
        _rowRefreshers.Clear();

        if (_cloudSources.Count > 0)
        {
            var allToggle = new ToggleSwitch
            {
                Header = "全部合入云端",
                OnContent = "本地日程会一起上传到云端",
                OffContent = "逐个日历决定去处",
                MinWidth = 0,
                Margin = new Thickness(0, 0, 0, 8),
                IsOn = _mergeAllToCloud,
            };
            ToolTipService.SetToolTip(allToggle,
                "云端已有的同一条日程仍以云端为准，不会被本地覆盖。");
            allToggle.Toggled += (_, _) =>
            {
                _mergeAllToCloud = allToggle.IsOn;
                // 一键把所有本地日历设成「合入云端」，目标用第一个云端日历。
                foreach (var choice in _choices)
                {
                    if (_mergeAllToCloud)
                    {
                        choice.Action = MergeAction.MergeInto;
                        choice.TargetHref = _cloudSources[0].Href;
                    }
                    else
                    {
                        choice.Action = MergeAction.KeepLocal;
                        choice.TargetHref = null;
                    }
                }

                foreach (var refresh in _rowRefreshers)
                {
                    refresh();
                }

                UpdateConfirmEnabled();
            };
            ChoiceList.Children.Add(allToggle);
        }

        foreach (var choice in _choices)
        {
            var row = new StackPanel { Spacing = 4 };

            var header = new TextBlock
            {
                Text = $"{choice.DisplayName}（{choice.EventCount} 条日程）",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            };
            row.Children.Add(header);

            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var group = new RadioButtons { MaxColumns = 3, MinWidth = 0 };

            var keep = new RadioButton { Content = "保留为本地", GroupName = choice.Href, IsChecked = true };
            var merge = new RadioButton
            {
                Content = "合入云端日历",
                GroupName = choice.Href,
                IsEnabled = _cloudSources.Count > 0,
            };
            var discard = new RadioButton { Content = "丢弃", GroupName = choice.Href };
            group.Items.Add(keep);
            group.Items.Add(merge);
            group.Items.Add(discard);

            // 目标日历下拉只在选「合入」时出现。
            var target = new ComboBox
            {
                MinWidth = 180,
                Visibility = Visibility.Collapsed,
                PlaceholderText = "选择云端日历",
            };
            foreach (var source in _cloudSources)
            {
                target.Items.Add(source.DisplayName);
            }

            if (_cloudSources.Count > 0)
            {
                target.SelectedIndex = 0;
            }

            // 丢弃前必须备份：一个提示 + 一个选择保存位置的按钮。
            var backupHint = new TextBlock
            {
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            };
            var backupButton = new Button
            {
                Content = "选择备份位置…",
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            backupButton.Click += async (_, _) =>
            {
                var folder = await PickBackupFolderAsync();
                if (folder is null)
                {
                    return;
                }

                var path = Path.Combine(folder, $"ToDooo-{SanitizeFileName(choice.DisplayName)}-{DateTime.Now:yyyyMMdd-HHmm}.ics");
                try
                {
                    File.WriteAllText(path, _merge.BuildBackupIcs(choice.Href));
                    choice.BackupPath = path;
                }
                catch (Exception exception)
                {
                    backupHint.Visibility = Visibility.Visible;
                    backupHint.Text = "备份失败：" + exception.Message;
                    choice.BackupPath = null;
                }

                Sync();
            };

            void Sync()
            {
                if (_mergeAllToCloud)
                {
                    // 总开关优先：单行选项只做展示，实际都按「合入云端」执行。
                    keep.IsEnabled = false;
                    discard.IsEnabled = false;
                    merge.IsEnabled = _cloudSources.Count > 0;
                    merge.IsChecked = true;
                    if (target.SelectedIndex < 0 && _cloudSources.Count > 0)
                    {
                        target.SelectedIndex = 0;
                    }

                    target.Visibility = _cloudSources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    choice.Action = MergeAction.MergeInto;
                    choice.TargetHref = target.SelectedIndex >= 0
                        ? _cloudSources[target.SelectedIndex].Href
                        : null;
                }
                else
                {
                    keep.IsEnabled = true;
                    discard.IsEnabled = true;
                    merge.IsEnabled = _cloudSources.Count > 0;

                    if (merge.IsChecked == true)
                    {
                        choice.Action = MergeAction.MergeInto;
                        choice.TargetHref = target.SelectedIndex >= 0
                            ? _cloudSources[target.SelectedIndex].Href
                            : null;
                    }
                    else if (discard.IsChecked == true)
                    {
                        choice.Action = MergeAction.Discard;
                        choice.TargetHref = null;
                    }
                    else
                    {
                        choice.Action = MergeAction.KeepLocal;
                        choice.TargetHref = null;
                    }
                }

                // 丢弃的必须先备份：没备份过的日历不允许直接丢。
                choice.BackupDone = choice.Action != MergeAction.Discard || choice.BackupPath is not null;
                backupHint.Visibility = choice.Action == MergeAction.Discard && choice.BackupPath is null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                backupButton.Visibility = choice.Action == MergeAction.Discard
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                backupButton.Content = choice.BackupPath is null ? "选择备份位置…" : "已备份，重新选择";
                backupHint.Text = choice.BackupPath is null
                    ? "丢弃前需要先备份这份日程，选择保存位置即可。"
                    : $"已备份到 {choice.BackupPath}";

                UpdateConfirmEnabled();
            }

            _rowRefreshers.Add(Sync);

            keep.Checked += (_, _) =>
            {
                target.Visibility = Visibility.Collapsed;
                Sync();
            };
            merge.Checked += (_, _) =>
            {
                target.Visibility = Visibility.Visible;
                Sync();
            };
            discard.Checked += (_, _) =>
            {
                target.Visibility = Visibility.Collapsed;
                Sync();
            };
            target.SelectionChanged += (_, _) => Sync();

            options.Children.Add(keep);
            options.Children.Add(merge);
            options.Children.Add(discard);
            row.Children.Add(options);
            row.Children.Add(target);
            row.Children.Add(backupHint);
            row.Children.Add(backupButton);

            ChoiceList.Children.Add(row);
        }
    }

    /// <summary>「合入」必须选好目标日历，「丢弃」必须先备份，两者都满足才允许执行。</summary>
    private void UpdateConfirmEnabled()
    {
        var missingTarget = _choices.Any(c =>
            c.Action == MergeAction.MergeInto && c.TargetHref is null);
        var missingBackup = _choices.Any(c =>
            c.Action == MergeAction.Discard && c.BackupPath is null);
        ConfirmButton.IsEnabled = !missingTarget && !missingBackup;
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StepChoose.Visibility = Visibility.Collapsed;
        StepProgress.Visibility = Visibility.Visible;
        ProgressText.Text = "正在合并…";

        try
        {
            Outcome = await _merge.ApplyAsync(_choices);
            ShowResult(Outcome);
        }
        catch (Exception exception)
        {
            ProgressText.Text = "合并失败：" + exception.Message;
            ConfirmButtonText.Text = "关闭";
            ConfirmButton.IsEnabled = true;
            ConfirmButton.Click -= ConfirmButton_Click;
            ConfirmButton.Click += (_, _) => Finish(true);
        }
    }

    private void ShowResult(MergeOutcome outcome)
    {
        StepProgress.Visibility = Visibility.Collapsed;
        StepResult.Visibility = Visibility.Visible;
        HeaderTitle.Text = "合并完成";

        ResultSummary.Text =
            $"上传到云端 {outcome.UploadedCount} 条；云端已有、采用云端版本 {outcome.CloudWonCount} 条；" +
            $"保留在本地 {outcome.KeptLocalCount} 条；丢弃 {outcome.DiscardedCount} 条。" +
            (outcome.FailedCount > 0
                ? $" {outcome.FailedCount} 条上传失败，已完整保留在本地日历里，可稍后重新合并。" +
                  (outcome.KeptCalendarCount > 0 ? " 这个日历没有被删除。" : string.Empty)
                : string.Empty);

        ResultDays.Children.Clear();
        foreach (var day in outcome.Days)
        {
            var expander = new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsExpanded = false,
            };

            var total = day.Uploaded.Count + day.Merged.Count + day.Skipped.Count;
            expander.Header = new TextBlock
            {
                Text = $"{day.Date:M月d日} · {total} 条",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            };

            var body = new StackPanel { Spacing = 2 };
            foreach (var text in day.Uploaded)
            {
                body.Children.Add(DetailLine("↑ " + text, "AccentBrush"));
            }

            foreach (var text in day.Skipped)
            {
                body.Children.Add(DetailLine("· " + text, "TextMutedBrush"));
            }

            expander.Content = body;
            ResultDays.Children.Add(expander);
        }

        ConfirmButtonText.Text = "完成";
        ConfirmButton.IsEnabled = true;
        ConfirmButton.Click -= ConfirmButton_Click;
        ConfirmButton.Click += (_, _) => Finish(true);
        CancelButton.Visibility = Visibility.Collapsed;
    }

    private static TextBlock DetailLine(string text, string brushKey) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[brushKey],
    };

    /// <summary>让用户选择备份文件夹；取消返回 null。</summary>
    private async Task<string?> PickBackupFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>日历名里可能带 / : 等非法字符，做文件名前先清掉。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "calendar" : cleaned;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Finish(false);
    }

    private void Finish(bool saved)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _appWindow.Hide();
        DispatcherQueue.TryEnqueue(() =>
        {
            _result.TrySetResult(saved);
            Close();
        });
    }

    private void ConfigureWindow()
    {
        _appWindow.Title = "合并日历";
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_THICKFRAME | NativeMethods.WS_DLGFRAME
            | NativeMethods.WS_CAPTION | NativeMethods.WS_BORDER);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

        var round = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var noBorder = NativeMethods.DWMWA_COLOR_NONE;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

        ExtendsContentIntoTitleBar = false;
        HeaderBar.Padding = new Thickness(18, 0, 10, 0);
    }

    private void ApplyLogicalSize(double scale)
    {
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalWidth * scale),
            (int)Math.Round(LogicalHeight * scale)));
    }

    private void MoveCenterOver(AppWindow? owner)
    {
        var size = _appWindow.Size;
        var point = new PointInt32(0, 0);
        var placed = false;

        if (owner is not null)
        {
            var ownerHwnd = Win32Interop.GetWindowFromWindowId(owner.Id);
            if (NativeMethods.GetWindowRect(ownerHwnd, out var rect))
            {
                point = new PointInt32(
                    rect.Left + ((rect.Right - rect.Left) - size.Width) / 2,
                    rect.Top + ((rect.Bottom - rect.Top) - size.Height) / 2);
                placed = true;
            }
        }

        var area = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest).WorkArea;
        if (!placed)
        {
            point = new PointInt32(
                area.X + ((area.Width - size.Width) / 2),
                area.Y + ((area.Height - size.Height) / 2));
        }

        _appWindow.Move(new PointInt32(
            Math.Clamp(point.X, area.X, area.X + Math.Max(0, area.Width - size.Width)),
            Math.Clamp(point.Y, area.Y, area.Y + Math.Max(0, area.Height - size.Height))));
    }

    private void HeaderBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed ||
            !NativeMethods.GetCursorPos(out _dragStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _dragStartRect))
        {
            return;
        }

        _dragging = ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void HeaderBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero,
                _dragStartRect.Left + cursor.X - _dragStartCursor.X,
                _dragStartRect.Top + cursor.Y - _dragStartCursor.Y,
                _dragStartRect.Right - _dragStartRect.Left,
                _dragStartRect.Bottom - _dragStartRect.Top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        e.Handled = true;
    }

    private void HeaderBar_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void HeaderBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragging = false;

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _finished = true;
        WindowCloak.Cancel(_hwnd, reveal: false);
        _result.TrySetResult(Outcome is not null);
        WindowAcrylic.DetachAndDispose(ref _acrylic, ref _backdropConfig);
    }
}
