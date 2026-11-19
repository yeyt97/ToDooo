using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ToDooo.Helpers;
using ToDooo.Interop;
using ToDooo.Models;
using ToDooo.Services;
using ToDooo.ViewModels;
using Windows.Graphics;
using Windows.UI;

namespace ToDooo.Views;

/// <summary>
/// 新建 / 编辑待办。做成独立的小窗口（而不是 ContentDialog）：
/// ContentDialog 会被限制在主窗口客户区里，主窗口一窄就挤得显示不全，也没法用窗口级磨砂。
/// </summary>
public sealed partial class TodoEditorDialog : Window
{
    private const int LogicalWidth = 420;

    // 截止时间收起/展开是两种高度，窗口跟着长，别留一大片空白。
    private const int CollapsedHeight = 520;
    private const int ExpandedHeight = 610;

    // 让后续逻辑排在关窗之后的下一轮消息里跑，别卡在关窗过程中重入 UI。
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Button> _tagButtons = new();
    private readonly List<FontIcon> _tagChecks = new();
    private Button? _clearTagButton;
    private readonly List<SubtaskDraft> _subtaskDrafts = new();
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private readonly SettingsService _settings;
    private int _tagIndex = -1;
    private bool _finished;
    private bool _ready;
    private bool _dueTouched;
    private bool _saved;

    public TodoEditorDialog(SettingsService settings) : this(settings, string.Empty)
    {
    }

    /// <summary>新建：把添加框里已经敲好的草稿带过来；草稿里识别到的日期直接预填。</summary>
    public TodoEditorDialog(SettingsService settings, string initialTitle, DateTime? initialDue = null)
    {
        _settings = settings;
        InitializeComponent();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        var dark = Appearance.IsDark((ThemeMode)settings.Current.ThemeMode);
        _acrylic = WindowAcrylic.Apply(
            this, Appearance.TintForOpacity(settings.Current.WindowOpacity), dark);
        var darkFlag = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        HeaderTitle.Text = "新建待办";
        TitleBox.Text = initialTitle ?? string.Empty;

        // 新建时选了日期就带当前时刻，避免默认落到 00:00 立刻逾期。
        DueTime.SelectedTime = RoundToFiveMinutes(DateTime.Now.TimeOfDay);

        if (initialDue is { } due)
        {
            DueDate.Date = new DateTimeOffset(due.Date);
            DueTime.SelectedTime = due.TimeOfDay;
            DueToggle.IsOn = true;
        }

        BuildTagButtons();
        UpdateTagVisuals();
        RenderSubtasks();
        ApplyDueVisibility();
        _ready = true;

        Closed += OnClosed;
    }

    public TodoEditorDialog(SettingsService settings, TodoItem item) : this(settings, item.Title)
    {
        HeaderTitle.Text = "编辑待办";
        TitleBox.Text = item.Title;

        if (item.DueAt is { } due)
        {
            DueToggle.IsOn = true;
            DueDate.Date = new DateTimeOffset(due.Date);
            DueTime.SelectedTime = due.TimeOfDay;
        }

        RecurrenceBox.SelectedIndex = (int)item.Recurrence;
        ImportantToggle.IsOn = item.IsImportant;
        _tagIndex = item.TagIndex;
        UpdateTagVisuals();

        foreach (var subtask in item.Subtasks)
        {
            _subtaskDrafts.Add(new SubtaskDraft(subtask.Id, subtask.Title, subtask.IsDone));
        }

        RenderSubtasks();
        ApplyDueVisibility();
    }

    /// <summary>true = 保存，false = 取消 / 直接关掉窗口。</summary>
    public Task<bool> Result => _result.Task;

    public string TodoTitle => TitleBox.Text.Trim();

    public DateTime? DueAt
    {
        get
        {
            // 没打开「截止时间」就是无期限，日期控件里残留的值一律不算。
            if (!DueToggle.IsOn || DueDate.Date is not { } date)
            {
                return null;
            }

            return date.Date + (DueTime.SelectedTime ?? TimeSpan.Zero);
        }
    }

    public TodoRecurrence Recurrence => (TodoRecurrence)Math.Max(0, RecurrenceBox.SelectedIndex);

    public bool IsImportant => ImportantToggle.IsOn;

    public int TagIndex => _tagIndex;

    /// <summary>保存时带出去的子任务草稿，交给主窗口按 Id 对齐落库。</summary>
    public IReadOnlyList<SubtaskDraft> Subtasks => _subtaskDrafts;

    /// <summary>先摆到主窗口正中间再激活；owner 为空就只按当前显示器兜底。</summary>
    public void ShowOver(AppWindow? owner)
    {
        _appWindow.Show();
        MoveNextTo(owner);
        Activate();

        TitleBox.Focus(FocusState.Programmatic);
        TitleBox.SelectAll();
    }

    private void ConfigureWindow()
    {
        _appWindow.Title = "待办";
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            // 无边框、无系统标题按钮：只靠底部按钮/ Esc 关闭，外观和主窗口一致。
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // 剥掉隐形 resize 边框，让 XAML 内容铺满窗口，圆角只由 DWM 裁一次。
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

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalWidth * scale),
            (int)Math.Round(CollapsedHeight * scale)));

        ExtendsContentIntoTitleBar = false;
        HeaderBar.Padding = new Thickness(18, 0, 12, 0);
    }

    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;

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

    private void MoveNextTo(AppWindow? owner)
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

        // 主窗口贴边时，居中出来的位置可能半截在屏幕外，夹回工作区。
        _appWindow.Move(new PointInt32(
            Math.Clamp(point.X, area.X, area.X + Math.Max(0, area.Width - size.Width)),
            Math.Clamp(point.Y, area.Y, area.Y + Math.Max(0, area.Height - size.Height))));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _finished = true;

        // 结果在这里统一给出：窗口已经关掉了，调用方再继续。
        // 之前在点按钮时就把结果抛出去，调用方会立刻抢焦点、刷主界面，
        // 而窗口还没真正消失，看起来就是「点取消时闪一下」。
        _result.TrySetResult(_saved);

        // 不要在 Closed 里 Dispose 亚克力控制器：窗口的合成目标正在销毁，
        // 这时释放会让进程直接挂掉（0xc000027b，CoreMessagingXP 里崩）。
        // 控制器交给 GC 回收即可，弹窗开得很少，这点驻留无所谓。
        _acrylic = null;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => Finish(true);

    private void TitleBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 单行标题框里回车就是保存，和主界面添加框的手感一致。
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            Finish(true);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Finish(true);
    }

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

        // 标题空着就不保存：既不关窗，也把焦点丢回输入框提示一下。
        if (saved && TodoTitle.Length == 0)
        {
            TitleBox.Focus(FocusState.Programmatic);
            return;
        }

        // 没手动开过截止时间的话，保存时再从标题里识别一次日期/时间。
        if (saved && !_dueTouched && !DueToggle.IsOn && !_finished &&
            NaturalDateParser.TryParse(TitleBox.Text, DateTime.Now, out var due, out var cleaned))
        {
            TitleBox.Text = cleaned;
            DueDate.Date = new DateTimeOffset(due.Date);
            DueTime.SelectedTime = due.TimeOfDay;
            DueToggle.IsOn = true;
        }

        // 保存时把还没确认的子任务输入一起带上。
        if (saved)
        {
            AppendPendingSubtask();
        }

        // 先隐藏再关：WinUI 关窗瞬间合成目标会先变黑一帧（点取消肉眼可见「闪一下」），
        // 隐藏后窗口不再参与合成，黑帧就看不到了。结果仍在 Closed 里给。
        _saved = saved;
        _finished = true;
        _appWindow.Hide();
        DispatcherQueue.TryEnqueue(Close);
    }

    private void DueToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            _dueTouched = true;
        }

        ApplyDueVisibility();
    }

    // 打开才显示日期/时间，同时把窗口高度换成展开态，收起时不留空白。
    private void ApplyDueVisibility()
    {
        var on = DueToggle.IsOn;
        DueSection.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // 打开就默认今天（时间已在构造时预填当前时刻），不用再多点一次日期；
        // 已经有值的（编辑旧待办、或之前选过）保持原样。
        if (on && DueDate.Date is null)
        {
            DueDate.Date = DateTimeOffset.Now.Date;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalWidth * scale),
            (int)Math.Round((on ? ExpandedHeight : CollapsedHeight) * scale)));
    }

    private static TimeSpan RoundToFiveMinutes(TimeSpan value)
    {
        var minutes = (int)Math.Round(value.TotalMinutes / 5.0) * 5;
        return TimeSpan.FromMinutes(minutes % (24 * 60));
    }

    private void BuildTagButtons()
    {
        for (var i = 0; i < MainViewModel.TagColors.Length; i++)
        {
            var color = Color.FromArgb(
                255,
                Convert.ToByte(MainViewModel.TagColors[i].Substring(1, 2), 16),
                Convert.ToByte(MainViewModel.TagColors[i].Substring(3, 2), 16),
                Convert.ToByte(MainViewModel.TagColors[i].Substring(5, 2), 16));

            // 亮色圆点配深色对勾，深色圆点配白色对勾。
            var luminance = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
            var checkColor = luminance > 150
                ? Color.FromArgb(255, 0x26, 0x2B, 0x33)
                : Color.FromArgb(255, 0xFF, 0xFF, 0xFF);

            AddTagButton(i, new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }, checkColor);
        }

        var clearIcon = new Grid { Width = 18, Height = 18 };
        clearIcon.Children.Add(new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["TextMutedBrush"],
        });
        clearIcon.Children.Add(new FontIcon
        {
            Glyph = "\uE711",
            FontSize = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        });

        _clearTagButton = new Button
        {
            Style = (Style)Root.Resources["TagRingButton"],
            Content = clearIcon,
            Visibility = Visibility.Collapsed,
        };
        _clearTagButton.Click += (_, _) =>
        {
            _tagIndex = -1;
            UpdateTagVisuals();
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_clearTagButton, "清除标签");
        TagPanel.Children.Add(_clearTagButton);
    }

    private void AddTagButton(int index, UIElement content, Color? checkColor = null)
    {
        var check = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 11,
            Foreground = new SolidColorBrush(checkColor ?? Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        var stack = new Grid();
        stack.Children.Add(content);
        stack.Children.Add(check);

        var host = new Button
        {
            Style = (Style)Root.Resources["TagRingButton"],
            Content = stack,
            Tag = index,
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            host, index < 0 ? "无颜色" : $"标签 {index + 1}");

        host.Click += (s, _) =>
        {
            var tag = (int)((Button)s!).Tag;
            _tagIndex = tag < 0 || _tagIndex == tag ? -1 : tag;
            UpdateTagVisuals();
        };

        _tagButtons.Add(host);
        _tagChecks.Add(check);
        TagPanel.Children.Add(host);
    }

    private void UpdateTagVisuals()
    {
        var selectedBrush = (Brush)Application.Current.Resources["SegmentSelectedBrush"];
        var transparent = (Brush)Application.Current.Resources["TransparentBrush"];
        for (var i = 0; i < _tagButtons.Count; i++)
        {
            var selected = _tagIndex == i;

            // 选中：对勾 + 中性灰底，不再套外框。
            _tagChecks[i].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            _tagButtons[i].BorderBrush = transparent;
            _tagButtons[i].Background = selected ? selectedBrush : transparent;
        }

        if (_clearTagButton is not null)
        {
            _clearTagButton.Visibility = _tagIndex >= 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SubtaskInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CommitSubtaskInput();
        }
    }

    private void AddSubtask_Click(object sender, RoutedEventArgs e) => CommitSubtaskInput();

    private void CommitSubtaskInput()
    {
        if (!AppendPendingSubtask())
        {
            return;
        }

        RenderSubtasks();
        SubtaskInput.Focus(FocusState.Programmatic);
    }

    // 输入框里敲了内容但没点“添加”就保存时，也把它当作一条子任务，避免白打。
    private bool AppendPendingSubtask()
    {
        var title = SubtaskInput.Text.Trim();
        if (title.Length == 0)
        {
            return false;
        }

        _subtaskDrafts.Add(new SubtaskDraft(0, title, false));
        SubtaskInput.Text = string.Empty;
        return true;
    }

    private void RenderSubtasks()
    {
        SubtaskPanel.Children.Clear();

        for (var i = 0; i < _subtaskDrafts.Count; i++)
        {
            var index = i;
            var draft = _subtaskDrafts[i];

            var check = new CheckBox
            {
                IsChecked = draft.Done,
                MinWidth = 0,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.Click += (_, _) =>
            {
                _subtaskDrafts[index] = _subtaskDrafts[index] with { Done = check.IsChecked == true };
                RenderSubtasks();
            };

            var title = new TextBox
            {
                Text = draft.Title,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.TextChanged += (_, _) =>
            {
                _subtaskDrafts[index] = _subtaskDrafts[index] with { Title = title.Text };
            };
            if (draft.Done)
            {
                title.Opacity = 0.55;
            }

            var delete = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                Width = 26,
                Height = 26,
                MinWidth = 0,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, "删除子任务");
            delete.Click += (_, _) =>
            {
                if (index < _subtaskDrafts.Count)
                {
                    _subtaskDrafts.RemoveAt(index);
                }

                RenderSubtasks();
            };

            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(title, 1);
            Grid.SetColumn(delete, 2);
            row.Children.Add(check);
            row.Children.Add(title);
            row.Children.Add(delete);
            SubtaskPanel.Children.Add(row);
        }

        UpdateSubtaskCount();
    }

    private void UpdateSubtaskCount() =>
        SubtaskCountText.Text = $"{_subtaskDrafts.Count(draft => draft.Done)}/{_subtaskDrafts.Count}";
}
