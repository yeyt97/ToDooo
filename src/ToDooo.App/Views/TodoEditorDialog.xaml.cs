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
    // 默认宽度 360：和用户日常拖到的宽度一致，一打开就是熟悉的大小。
    private const int LogicalWidth = 360;

    // 截止时间收起/展开是两种高度，窗口跟着长，别留一大片空白。
    private const int CollapsedHeight = 576;
    private const int ExpandedHeight = 666;
    private const int MinLogicalWidth = 360;
    private const int MinLogicalHeight = 420;
    private const string SubtaskDragKey = "ToDoooEditorSubtask";

    // 让后续逻辑排在关窗之后的下一轮消息里跑，别卡在关窗过程中重入 UI。
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Button> _tagButtons = new();
    private readonly List<FontIcon> _tagChecks = new();
    private Button? _clearTagButton;
    private readonly List<SubtaskDraft> _subtaskDrafts = new();
    private int _draggedSubtaskIndex = -1;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly SettingsService _settings;
    private int _tagIndex = -1;
    private bool _finished;
    private bool _ready;
    private bool _dueTouched;
    private bool _saved;

    public TodoEditorDialog(SettingsService settings) : this(settings, string.Empty)
    {
    }

    /// <summary>新建：把添加框里已经敲好的草稿带过来；草稿里识别到的日期/重复/重要直接预填。</summary>
    public TodoEditorDialog(
        SettingsService settings,
        string initialTitle,
        DateTime? initialDue = null,
        TodoRecurrence initialRecurrence = TodoRecurrence.None,
        bool initialImportant = false)
    {
        _settings = settings;
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
            (Appearance.TintMode)Math.Clamp(settings.Current.WindowTintMode, 0, Appearance.TintCount - 1), _settings.Current.CustomTintColor);
        var darkFlag = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        HeaderTitle.Text = "新建待办";
        TitleBox.Text = initialTitle ?? string.Empty;
        RecurrenceBox.SelectedIndex = (int)initialRecurrence;
        ImportantToggle.IsOn = initialImportant;

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

        // 截止区可能初始是收起的，TimePicker 模板要到展开那帧才实例化，
        // 单靠 Loaded 解除最小宽度会漏掉，这里挂持续监听直到真正布局完成。
        TimePickerSizing.EnsureReleased(DueTime);

        ApplyDueVisibility();
        _ready = true;

        Closed += OnClosed;
    }

    public TodoEditorDialog(SettingsService settings, TodoItem item) : this(settings, item.Title)
    {
        HeaderTitle.Text = "编辑待办";
        TitleBox.Text = item.Title;
        NoteBox.Text = item.Note ?? string.Empty;

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
            _subtaskDrafts.Add(new SubtaskDraft(subtask.Id, subtask.Title, subtask.IsDone, subtask.DueAt, subtask.Note));
        }

        RenderSubtasks();
        ApplyDueVisibility();
    }

    /// <summary>true = 保存，false = 取消 / 直接关掉窗口。</summary>
    public Task<bool> Result => _result.Task;

    public string TodoTitle => TitleBox.Text.Trim();

    public string TodoNote => NoteBox.Text.Trim();

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

    /// <summary>先摆到所属窗口正中间再激活；owner 为空就只按当前显示器兜底。</summary>
    public void ShowOver(AppWindow? owner)
    {
        // 先摆好位置和尺寸再显示：中途 Show 会让窗口按默认尺寸出现在旧位置，看着闪一下。
        MoveCenterOver(owner);
        ApplyLogicalSize(DpiScale.ForOwner(owner, _hwnd));
        MoveCenterOver(owner);
        WindowCloak.ShowCloaked(_hwnd, Root, DispatcherQueue, () =>
        {
            _appWindow.Show();
            Activate();
            TitleBox.Focus(FocusState.Programmatic);
            TitleBox.SelectAll();
        });
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

        ExtendsContentIntoTitleBar = false;
        HeaderBar.Padding = new Thickness(18, 0, 10, 0);

        _edgeResize = new WindowEdgeResize(Root, _hwnd,
            () => (int)Math.Round(MinLogicalWidth * (NativeMethods.GetDpiForWindow(_hwnd) / 96.0)),
            () => (int)Math.Round(MinLogicalHeight * (NativeMethods.GetDpiForWindow(_hwnd) / 96.0)));
        _edgeResize.Resized += SaveUserSize;
    }

    private WindowEdgeResize _edgeResize = null!;

    /// <summary>用户拖过窗口尺寸后记住，下次打开沿用（按逻辑像素存，换 DPI 也不漂）。</summary>
    private void SaveUserSize()
    {
        var size = _appWindow.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0)
        {
            return;
        }

        _settings.Current.TodoEditorWidth = (int)Math.Round(size.Width / scale);
        _settings.Current.TodoEditorHeight = (int)Math.Round(size.Height / scale);
        _settings.Save();
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 边缘优先给缩放；不在边缘上才轮到标题栏拖动（由 HeaderBar 自己的处理接手）。
        _edgeResize?.TryBegin(e);

        // 点到输入控件以外的空白处时让输入框失焦。
        // WinUI 默认不会因为点了不可聚焦的元素就移走焦点，光标会一直留在原输入框里。
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<TextBox>(source) is null &&
            FindAncestor<PasswordBox>(source) is null)
        {
            Root.Focus(FocusState.Pointer);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e) =>
        _edgeResize?.TryMove(e);

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        _edgeResize?.TryEnd(e);

    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;

    private void HeaderBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_edgeResize is { IsResizing: true } ||
            !e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed ||
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

        // 主窗口贴边时，居中出来的位置可能半截在屏幕外，夹回工作区。
        _appWindow.Move(new PointInt32(
            Math.Clamp(point.X, area.X, area.X + Math.Max(0, area.Width - size.Width)),
            Math.Clamp(point.Y, area.Y, area.Y + Math.Max(0, area.Height - size.Height))));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _finished = true;
        WindowCloak.Cancel(_hwnd, reveal: false);

        // 结果在这里统一给出：窗口已经关掉了，调用方再继续。
        // 之前在点按钮时就把结果抛出去，调用方会立刻抢焦点、刷主界面，
        // 而窗口还没真正消失，看起来就是「点取消时闪一下」。
        _result.TrySetResult(_saved);
        WindowAcrylic.DetachAndDispose(ref _acrylic, ref _backdropConfig);
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

        // 保存时把还没写内容的空行丢掉，不落一条空子任务。
        if (saved)
        {
            _subtaskDrafts.RemoveAll(draft => string.IsNullOrWhiteSpace(draft.Title));
        }

        // 先隐藏再关：WinUI 关窗瞬间合成目标会先变黑一帧（点取消肉眼可见「闪一下」），
        // 隐藏后窗口不再参与合成，黑帧就看不到了。结果仍在 Closed 里给。
        _saved = saved;
        _finished = true;
        _appWindow.Hide();
        DispatcherQueue.TryEnqueue(Close);
    }

    private void NativeTimePicker_Loaded(object sender, RoutedEventArgs e) =>
        TimePickerSizing.ReleaseMinWidth(sender, e);

    private void DueToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            _dueTouched = true;
        }

        ApplyDueVisibility();
    }

    // 截止区开合只改内容显隐，不改窗口尺寸：窗口高度一变整块内容会上下跳。
    // 打开后的高度多出来一截，交给内容区的滚动条消化。
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

        // 截止区刚由隐藏变可见时，TimePicker 模板这一帧才实例化，
        // Loaded 已错过，展开后补一次解除最小宽度，否则分钟会被裁掉。
        if (on)
        {
            DispatcherQueue.TryEnqueue(() => TimePickerSizing.ReleaseMinWidth(DueTime));
        }
    }

    private void ApplyLogicalSize(double scale)
    {
        // 优先用用户上次拖到的尺寸；没拖过就用默认高度（截止已开时取展开态，免得一打开就要滚）。
        var settings = _settings.Current;
        var width = settings.TodoEditorWidth > 0
            ? Math.Max(settings.TodoEditorWidth, MinLogicalWidth)
            : LogicalWidth;
        var height = settings.TodoEditorHeight > 0
            ? settings.TodoEditorHeight
            : DueToggle.IsOn ? ExpandedHeight : CollapsedHeight;
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(width * scale),
            (int)Math.Round(Math.Max(height, MinLogicalHeight) * scale)));
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
        // 颜色标签总开关关闭：整行连配色竖条一起按“未选标签”处理。
        if (!_settings.Current.ColorTagsEnabled)
        {
            TagRow.Visibility = Visibility.Collapsed;
            ColorStripe.Background = new SolidColorBrush(TagBrushColor(-1));
            return;
        }

        TagRow.Visibility = Visibility.Visible;

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

        // 左侧竖条跟随标签色；没选标签时用中性灰。
        ColorStripe.Background = new SolidColorBrush(TagBrushColor(_tagIndex));
    }

    private static Color TagBrushColor(int index)
    {
        if (index >= 0 && index < MainViewModel.TagColors.Length)
        {
            var hex = MainViewModel.TagColors[index];
            return Color.FromArgb(0xFF,
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
        }

        return (Application.Current.Resources["TextMutedBrush"] as SolidColorBrush)?.Color
               ?? Color.FromArgb(0xFF, 0x8A, 0x8A, 0x92);
    }

    private void AddSubtask_Click(object sender, RoutedEventArgs e)
    {
        // 直接加一条空行，前面的拖动柄和圆圈一起出来，接着就能填。
        _subtaskDrafts.Add(new SubtaskDraft(_nextDraftId--, string.Empty, false));
        RenderSubtasks();

        // 把焦点落到刚加的那一行，直接就能打字。
        FocusDraftTitle(_subtaskDrafts.Count - 1);
    }

    // 新建草稿的临时 Id 用负数，和库里自增的正 Id 区分开。
    private long _nextDraftId = -1;

    private void FocusDraftTitle(int index)
    {
        if (index < 0 || index >= SubtaskPanel.Children.Count)
        {
            return;
        }

        if (SubtaskPanel.Children[index] is Grid row &&
            row.Children.OfType<TextBox>().FirstOrDefault() is { } box)
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        }
    }

    private void RenderSubtasks()
    {
        SubtaskPanel.Children.Clear();

        for (var i = 0; i < _subtaskDrafts.Count; i++)
        {
            var index = i;
            var draft = _subtaskDrafts[i];
            // 空行是刚点「+」生成的占位：内容还没写之前，勾选和拖动都不该生效。
            var blank = string.IsNullOrWhiteSpace(draft.Title);

            var check = new CheckBox
            {
                IsChecked = draft.Done,
                IsEnabled = !blank,
                Style = (Style)Application.Current.Resources["TodoCompletionCheckBoxStyle"],
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
                Style = (Style)Application.Current.Resources["AddTextBoxStyle"],
                Height = 26,
                Padding = new Thickness(4, 0, 4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                PlaceholderText = "子任务",
            };
            title.TextChanged += (_, _) =>
            {
                _subtaskDrafts[index] = _subtaskDrafts[index] with { Title = title.Text };

                // 输入内容后圈圈才可点；清空又退回不可点。直接改属性，不重渲染，免得丢焦点。
                check.IsEnabled = !string.IsNullOrWhiteSpace(title.Text);
            };
            // 标题被清空后离焦：这条没有内容了，直接删掉，别留一行空壳。
            title.LostFocus += (_, _) =>
            {
                if (index >= _subtaskDrafts.Count ||
                    !string.IsNullOrWhiteSpace(_subtaskDrafts[index].Title))
                {
                    return;
                }

                _subtaskDrafts.RemoveAt(index);
                RenderSubtasks();
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

            var dueButton = new Button
            {
                Height = 26,
                MinWidth = 0,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Opacity = (draft.DueAt is null && string.IsNullOrWhiteSpace(draft.Note)) ? 0.65 : 1,
            };
            var dueContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dueContent.Children.Add(new FontIcon
            {
                Glyph = "\uE823",
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            });
            if (draft.DueAt is { } due)
            {
                dueContent.Children.Add(new TextBlock
                {
                    Text = due.ToString("M/d HH:mm"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                });
            }
            dueButton.Content = dueContent;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dueButton, "编辑子任务");
            if (!string.IsNullOrWhiteSpace(draft.Note))
            {
                ToolTipService.SetToolTip(dueButton, draft.Note);
            }

            dueButton.Click += async (_, _) => await EditSubtaskDraftAsync(index);

            var grip = new Border
            {
                Width = 14,
                Height = 26,
                Background = new SolidColorBrush(Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Center,
                CanDrag = !blank,
                Opacity = blank ? 0.25 : 1,
                Child = new FontIcon
                {
                    Glyph = "\uE784",
                    FontSize = 10,
                    Opacity = 0.55,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                },
            };
            grip.DragStarting += (_, args) =>
            {
                _draggedSubtaskIndex = index;
                args.Data.Properties[SubtaskDragKey] = index;
                args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            };
            grip.DropCompleted += (_, _) => _draggedSubtaskIndex = -1;

            // 空行不能拖；一旦写了内容就放开拖动。
            title.TextChanged += (_, _) =>
            {
                var empty = string.IsNullOrWhiteSpace(title.Text);
                grip.CanDrag = !empty;
                grip.Opacity = empty ? 0.25 : 1;
            };

            var row = new Grid
            {
                ColumnSpacing = 6,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Colors.Transparent),
                AllowDrop = true,
            };
            row.DragOver += (_, args) =>
            {
                if (!args.DataView.Properties.ContainsKey(SubtaskDragKey) || _draggedSubtaskIndex == index)
                {
                    args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                    return;
                }

                args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                args.DragUIOverride.IsCaptionVisible = false;
                args.DragUIOverride.IsGlyphVisible = false;
                row.Background = (Brush)Application.Current.Resources["HoverBrush"];
            };
            row.DragLeave += (_, _) => row.Background = new SolidColorBrush(Colors.Transparent);
            row.Drop += (_, args) =>
            {
                row.Background = new SolidColorBrush(Colors.Transparent);
                if (_draggedSubtaskIndex < 0 || _draggedSubtaskIndex >= _subtaskDrafts.Count ||
                    index < 0 || index >= _subtaskDrafts.Count || _draggedSubtaskIndex == index)
                {
                    return;
                }

                args.Handled = true;
                var moved = _subtaskDrafts[_draggedSubtaskIndex];
                _subtaskDrafts.RemoveAt(_draggedSubtaskIndex);
                _subtaskDrafts.Insert(index, moved);
                _draggedSubtaskIndex = -1;
                RenderSubtasks();
            };

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(check, 1);
            Grid.SetColumn(title, 2);
            Grid.SetColumn(dueButton, 3);
            Grid.SetColumn(delete, 4);
            row.Children.Add(grip);
            row.Children.Add(check);
            row.Children.Add(title);
            row.Children.Add(dueButton);
            row.Children.Add(delete);
            SubtaskPanel.Children.Add(row);
        }

        UpdateSubtaskCount();
    }

    private async Task EditSubtaskDraftAsync(int index)
    {
        if (index < 0 || index >= _subtaskDrafts.Count)
        {
            return;
        }

        var draft = _subtaskDrafts[index];
        var dialog = new SubtaskEditorDialog(
            _settings,
            draft.Title,
            draft.Note,
            draft.DueAt,
            draft.Done);
        dialog.ShowOver(_appWindow);
        dialog.Activate();
        if (!await dialog.Result || index >= _subtaskDrafts.Count)
        {
            return;
        }

        _subtaskDrafts[index] = _subtaskDrafts[index] with
        {
            Title = dialog.SubtaskTitle,
            Note = dialog.SubtaskNote,
            DueAt = dialog.DueAt,
            Done = dialog.IsDone,
        };
        RenderSubtasks();
    }

    private void UpdateSubtaskCount() =>
        SubtaskCountText.Text = $"{_subtaskDrafts.Count(draft => draft.Done)}/{_subtaskDrafts.Count}";
}
