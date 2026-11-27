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
/// 新建 iCloud 日程。独立窗口，版式参考 iOS 日历：主卡片放标题与时间，
/// 下面按「选项 / 详细信息」分组。开始与结束时间联动。
/// </summary>
public sealed partial class CalendarEventDialog : Window
{
    private const int LogicalWidth = 460;
    private const int LogicalHeight = 720;

    /// <summary>默认事件时长。改开始时间时，结束时间按这个长度顺推。</summary>
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);

    private TimeSpan _duration = DefaultDuration;

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SettingsService _settings;
    private readonly CalendarSyncService _calendarSync;
    private readonly List<CalendarSource> _sources;
    private List<CalendarSource> _calendarChoices = new();
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    private readonly CalendarEvent? _editing;
    private readonly bool _allowRepeatEdit;
    private readonly bool _lockCalendar;
    private bool _finished;
    private bool _saved;
    private bool _syncingTime;
    private bool _initializing = true;

    /// <summary>RRULE 不在预设选项里时（苹果常见的 BYDAY 写法），原样保留。</summary>
    private string _customRRule = string.Empty;

    /// <summary>用户在结束区里手动改过时间/日期后，就不再自动跟着开始时间跑。</summary>
    private bool _endTouched;

    public CalendarEventDialog(
        SettingsService settings,
        CalendarSyncService calendarSync,
        DateTime initialDate,
        string? initialCalendarHref = null,
        CalendarEvent? editing = null,
        string titleSuffix = "",
        bool allowRepeatEdit = false,
        bool lockCalendar = false,
        DateTime? initialEndDate = null)
    {
        _settings = settings;
        _calendarSync = calendarSync;
        _editing = editing;
        _allowRepeatEdit = allowRepeatEdit;
        _lockCalendar = lockCalendar;
        _sources = calendarSync.Repository.GetSources();

        InitializeComponent();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        ApplyWindowMaterial();

        var hasMultiDayRange = editing is null && initialEndDate is { } endDay && endDay.Date > initialDate.Date;
        HeaderTitle.Text = (editing is null
            ? hasMultiDayRange
                ? $"{initialDate:M月d日} – {initialEndDate!.Value:M月d日} 新建日程"
                : $"{initialDate:M 月 d 日} 新建日程"
            : "编辑日程") + titleSuffix;
        SaveButton.Content = BuildCreateButtonContent(editing is null ? "创建" : "保存");

        BuildRepeatOptions();
        FillCalendars(editing?.CalendarHref ?? initialCalendarHref);

        var start = initialDate.Date.AddHours(9);
        StartDate.Date = new DateTimeOffset(start.Date);
        SetTime(start: true, start.TimeOfDay);
        EndDate.Date = new DateTimeOffset(hasMultiDayRange ? initialEndDate!.Value.Date : start.Date);
        SetTime(start: false, start.TimeOfDay + DefaultDuration);
        if (hasMultiDayRange)
        {
            AllDayToggle.IsOn = true;
            _endTouched = true;
        }

        PriorityBox.SelectedIndex = 0;

        if (editing is not null)
        {
            ApplyEditingValues(editing);
        }
        UpdateColorStripe();
        CalendarBox.SelectionChanged += (_, _) => UpdateColorStripe();

        Closed += OnClosed;
        Activated += OnWindowActivated;
    }

    /// <summary>切到别的窗口时把焦点挪走，免得输入框里的光标跟着窗口一起留到后台。</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            FocusSink.Focus(FocusState.Programmatic);
            HidePlaceSuggestions();
        }
    }

    /// <summary>
    /// 点在非输入区（卡片、空白）时把焦点从输入框撤走，光标才会消失；
    /// 点在输入框上是要放光标，别抢。
    /// </summary>
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<TextBox>(source) is not null || IsInside(source, PlaceSuggestionsCard)))
        {
            return;
        }

        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        if (focused is TextBox or RichEditBox or PasswordBox)
        {
            FocusSink.Focus(FocusState.Pointer);
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

    /// <summary>true 表示已创建；关闭窗口时给出。</summary>
    public Task<bool> Result => _result.Task;

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        _initializing = false;
        _endTouched = false;
    }

    /// <summary>创建时收集的表单结果。</summary>
    public CalendarSyncService.CalendarEventDraft? Draft { get; private set; }

    public string? TargetCalendarHref { get; private set; }

    public void ShowOver(AppWindow? owner, bool topmost = false)
    {
        MoveCenterOver(owner);
        ApplyLogicalSize(DpiScale.ForOwner(owner, _hwnd));
        MoveCenterOver(owner);
        WindowCloak.ShowCloaked(_hwnd, Root, DispatcherQueue, () =>
        {
            _appWindow.Show();
            if (topmost)
            {
                // 日历窗口在贴边时是置顶的，日程窗必须同样置顶才不会被盖住。
                NativeMethods.SetWindowPos(
                    _hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            Activate();
            TitleBox.Focus(FocusState.Programmatic);
        });
    }

    private static StackPanel BuildCreateButtonContent(string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 12 });
        panel.Children.Add(new TextBlock { Text = text });
        return panel;
    }

    /// <summary>把已有日程回填到表单；日历不可改（换日历等于换集合，需重建资源）。</summary>
    private void ApplyEditingValues(CalendarEvent editing)
    {
        _syncingTime = true;
        try
        {
            TitleBox.Text = editing.Summary;
            var start = editing.StartAt;
            var end = editing.EndAt ?? start.AddHours(1);

            // 全天事件的 EndAt 是排他端（次日 0 点）：表单按「含当天」显示，要退一天。
            if (editing.IsAllDay && end.Date > start.Date && end.TimeOfDay == TimeSpan.Zero)
            {
                end = end.AddDays(-1);
            }

            _duration = end > start ? end - start : DefaultDuration;
            StartDate.Date = new DateTimeOffset(start.Date);
            SetTime(start: true, start.TimeOfDay);
            EndDate.Date = new DateTimeOffset(end.Date);
            SetTime(start: false, end.TimeOfDay);
            AllDayToggle.IsOn = editing.IsAllDay;
        SelectRepeatRule(editing.RRule);
        RepeatRow.Visibility = _allowRepeatEdit ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _syncingTime = false;
        }

        // 保持「开始 + 原时长」联动：改开始时间时按现有时长顺推结束。
        _endTouched = false;

        NoteBox.Text = editing.Notes;
        SetLocationTextSilently(editing.Location);
        UrlBox.Text = editing.Url;
        CategoryBox.Text = editing.Categories;
        PriorityBox.SelectedIndex = editing.Priority switch
        {
            >= 1 and <= 4 => 1,
            5 => 2,
            >= 6 => 3,
            _ => 0,
        };

        // 只有「仅这一次」的覆盖实例保持日历锁定：换集合要重建整条资源，语义不对。
        CalendarBox.IsEnabled = !_lockCalendar;
    }

    private void ConfigureWindow()
    {
        _appWindow.Title = "日程";
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
    }

    private void ApplyWindowMaterial()
    {
        var dark = Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        var darkFlag = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        };
        _acrylic = WindowAcrylic.Apply(
            this, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark, _backdropConfig,
            (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, Appearance.TintCount - 1), _settings.Current.CustomTintColor);
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

    // ---- 表单 --------------------------------------------------------------

    private void BuildRepeatOptions()
    {
        foreach (var option in RepeatOptions)
        {
            RepeatBox.Items.Add(option.Label);
        }

        RepeatBox.SelectedIndex = 0;
    }

    /// <summary>把已有 RRULE 映射回下拉；不在预设里就加一项「自定义」并保留原文。</summary>
    private void SelectRepeatRule(string? rrule)
    {
        var value = rrule?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            RepeatBox.SelectedIndex = 0;
            return;
        }

        var index = RepeatOptions.FindIndex(o =>
            string.Equals(o.RRule, value, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            RepeatBox.SelectedIndex = index;
            return;
        }

        _customRRule = value;
        RepeatBox.Items.Add("自定义（保留原规则）");
        RepeatBox.SelectedIndex = RepeatOptions.Count;
    }

    private sealed record RepeatOption(string Label, string RRule);

    private static readonly List<RepeatOption> RepeatOptions = new()
    {
        new RepeatOption("永不", string.Empty),
        new RepeatOption("每天", "FREQ=DAILY"),
        new RepeatOption("每周", "FREQ=WEEKLY"),
        new RepeatOption("每两周", "FREQ=WEEKLY;INTERVAL=2"),
        new RepeatOption("每月", "FREQ=MONTHLY"),
        new RepeatOption("每年", "FREQ=YEARLY"),
    };

    /// <summary>下拉里的日历候选：可见的 + 当前日程原本所属的（即使被隐藏）。</summary>
    private List<CalendarSource> CalendarChoices(string? preferredHref)
    {
        var list = _sources
            .Where(s => s.IsVisible ||
                (preferredHref is not null &&
                 string.Equals(s.Href, preferredHref, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return list.Count > 0 ? list : _sources;
    }

    private void FillCalendars(string? preferredHref)
    {
        // 编辑时目标日历可能被设为隐藏；若不把它算进来，选中项会跳到第一个日历，
        // 一保存日程就被搬到别的日历里。这里把当前日历也纳入候选。
        _calendarChoices = CalendarChoices(preferredHref);

        foreach (var source in _calendarChoices)
        {
            CalendarBox.Items.Add(BuildCalendarItem(source));
        }

        if (_calendarChoices.Count > 0)
        {
            var index = preferredHref is null
                ? 0
                : Math.Max(0, _calendarChoices.FindIndex(s => s.Href == preferredHref));
            CalendarBox.SelectedIndex = index;
        }
    }

    /// <summary>下拉项：左侧一个日历原色圆点，右侧名称，便于区分同名的日历。</summary>
    private UIElement BuildCalendarItem(CalendarSource source)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(ParseColor(source.Color)),
        });
        row.Children.Add(new TextBlock
        {
            Text = source.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private CalendarSource? SelectedSource
    {
        get
        {
            var index = CalendarBox.SelectedIndex;
            return index >= 0 && index < _calendarChoices.Count ? _calendarChoices[index] : null;
        }
    }

    private void UpdateColorStripe()
    {
        var color = SelectedSource?.Color;
        ColorStripe.Background = new SolidColorBrush(ParseColor(color));
    }

    private static Windows.UI.Color ParseColor(string? value)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var hex = value.TrimStart('#');
                if (hex.Length >= 6)
                {
                    return Windows.UI.Color.FromArgb(0xFF,
                        Convert.ToByte(hex[..2], 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
                }
            }
        }
        catch
        {
            // 颜色解析失败用强调色
        }

        return (Application.Current.Resources["AccentBrush"] as SolidColorBrush)?.Color
               ?? Windows.UI.Color.FromArgb(0xFF, 0x4D, 0x8F, 0xE3);
    }

    // ---- 时间（原生 TimePicker，24 小时制、5 分钟一档） ----------------------

    private void SetTime(bool start, TimeSpan time)
    {
        _syncingTime = true;
        try
        {
            var picker = start ? StartTimePicker : EndTimePicker;
            if (picker.SelectedTime == time)
            {
                return;
            }

            picker.SelectedTime = time;
        }
        finally
        {
            _syncingTime = false;
        }
    }

    private TimeSpan? ReadTime(bool start)
    {
        var picker = start ? StartTimePicker : EndTimePicker;
        return picker.SelectedTime;
    }

    /// <summary>原生模板的内部按钮写死 242px 最小宽度，加载后解除才能缩短。</summary>
    private void NativeTimePicker_Loaded(object sender, RoutedEventArgs ea) =>
        TimePickerSizing.ReleaseMinWidth(sender, ea);

    private void StartTimePicker_SelectedTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args)
    {
        if (_initializing || _syncingTime)
        {
            return;
        }

        if (!_endTouched)
        {
            SynchronizeEnd(false);
        }
    }

    private void EndTimePicker_SelectedTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args)
    {
        UpdateDurationFromForm();
    }

    private void EndField_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            _endTouched = true;
        }
    }

    private void StartDate_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_initializing || _syncingTime || _endTouched)
        {
            return;
        }

        SynchronizeEnd(false);
    }

    private void EndDate_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        UpdateDurationFromForm();
    }

    private void UpdateDurationFromForm()
    {
        if (_initializing || _syncingTime)
        {
            return;
        }

        _duration = ReadDuration();
    }

    /// <summary>按「开始 + 时长」重算结束日期与时间。</summary>
    private void SynchronizeEnd(bool force)
    {
        if (!force && _endTouched)
        {
            return;
        }

        var start = ReadStart();
        if (start is null)
        {
            return;
        }

        var end = start.Value + _duration;

        _syncingTime = true;
        try
        {
            EndDate.Date = new DateTimeOffset(end.Date);
            SetTime(start: false, end.TimeOfDay);
        }
        finally
        {
            _syncingTime = false;
        }
    }

    private DateTime? ReadStart()
    {
        if (StartDate.Date is not { } dateValue)
        {
            return null;
        }

        var date = dateValue.Date;
        var time = ReadTime(true) ?? new TimeSpan(9, 0, 0);
        return AllDayToggle.IsOn ? date : date + time;
    }

    /// <summary>当前表单表达的时长；结束早于开始时退回默认 1 小时。</summary>
    private TimeSpan ReadDuration()
    {
        if (EndDate.Date is not { } endDateValue || ReadTime(false) is not { } endTime)
        {
            return DefaultDuration;
        }

        if (StartDate.Date is not { } startDateValue || ReadTime(true) is not { } startTime)
        {
            return DefaultDuration;
        }

        var start = startDateValue.Date + startTime;
        var end = endDateValue.Date + endTime;
        return end > start ? end - start : DefaultDuration;
    }

    private void AllDayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var allDay = AllDayToggle.IsOn;
        StartTimePicker.Visibility = allDay ? Visibility.Collapsed : Visibility.Visible;
        EndTimePicker.Visibility = allDay ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RepeatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 改重复不改时间，这里只留一个钩子，便于以后按重复类型调整默认值。
    }

    private void TitleBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            Finish(true);
        }
    }

    // ---- 窗口拖动 ----------------------------------------------------------

    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;

    private void HeaderBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        HidePlaceSuggestions();
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

    // ---- 收尾 --------------------------------------------------------------

    private void SaveButton_Click(object sender, RoutedEventArgs e) => Finish(true);

    private CancellationTokenSource? _placeSearchCts;
    private bool _suppressPlaceSearch;

    private void SetLocationTextSilently(string text)
    {
        _suppressPlaceSearch = true;
        try
        {
            LocationBox.Text = text ?? string.Empty;
        }
        finally
        {
            _suppressPlaceSearch = false;
        }
    }

    private void LocationBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LocationBox.Text))
        {
            var recent = PlaceSearchService.GetRecent(limit: 5);
            if (recent.Count > 0)
            {
                PlaceSuggestionsList.ItemsSource = recent;
                PlaceSuggestionsList.SelectedIndex = -1;
                ShowPlaceSuggestions();
            }
        }
    }

    /// <summary>焦点离开地点输入框就收起候选，免得弹层一直挂在那儿。</summary>
    private void LocationBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 点候选条目时焦点会先离开输入框：等一帧再判断，避免弹层在点击生效前被收掉。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!PlaceSuggestionsPopup.IsOpen)
            {
                return;
            }

            var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
            if (focused is not null &&
                (ReferenceEquals(focused, LocationBox) || IsInside(focused, PlaceSuggestionsCard)))
            {
                return;
            }

            HidePlaceSuggestions();
        });
    }

    /// <summary>node 是否在 container 的子树里（含自身）。</summary>
    private static bool IsInside(DependencyObject node, DependencyObject container)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }

        return false;
    }

    private async void LocationBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressPlaceSearch)
        {
            return;
        }

        await TriggerPlaceSearchAsync(LocationBox.Text, debounceMs: 350);
    }

    private async void LocationBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (PlaceSuggestionsPopup.IsOpen && PlaceSuggestionsList.Items.Count > 0)
        {
            var count = PlaceSuggestionsList.Items.Count;
            if (e.Key == Windows.System.VirtualKey.Down)
            {
                PlaceSuggestionsList.SelectedIndex = (PlaceSuggestionsList.SelectedIndex + 1) % count;
                e.Handled = true;
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Up)
            {
                PlaceSuggestionsList.SelectedIndex = PlaceSuggestionsList.SelectedIndex <= 0
                    ? count - 1
                    : PlaceSuggestionsList.SelectedIndex - 1;
                e.Handled = true;
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Enter && PlaceSuggestionsList.SelectedItem is PlaceSuggestion selected)
            {
                ApplyPlaceSuggestion(selected);
                e.Handled = true;
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                HidePlaceSuggestions();
                e.Handled = true;
                return;
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await TriggerPlaceSearchAsync(LocationBox.Text, debounceMs: 0);
        }
    }

    private void PlaceSuggestionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaceSuggestion suggestion)
        {
            ApplyPlaceSuggestion(suggestion);
        }
    }

    private void ApplyPlaceSuggestion(PlaceSuggestion suggestion)
    {
        _placeSearchCts?.Cancel();
        // 写完整地址：手机上点这个地点能直接定位，只写简称容易搜不到。
        SetLocationTextSilently(suggestion.DisplayName);
        LocationBox.SelectionStart = LocationBox.Text.Length;
        HidePlaceSuggestions();
    }

    private void ShowPlaceSuggestions()
    {
        var origin = LocationBox.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
        PlaceSuggestionsCard.Width = Math.Max(240, LocationBox.ActualWidth);
        PlaceSuggestionsPopup.HorizontalOffset = origin.X;
        PlaceSuggestionsPopup.VerticalOffset = origin.Y + LocationBox.ActualHeight + 4;
        PlaceSuggestionsPopup.IsOpen = true;
    }

    private void HidePlaceSuggestions()
    {
        PlaceSuggestionsPopup.IsOpen = false;
        PlaceSuggestionsList.SelectedIndex = -1;
    }

    private async Task TriggerPlaceSearchAsync(string rawText, int debounceMs)
    {
        var keyword = (rawText ?? string.Empty).Trim();

        // 上一次搜索还没回来就取消，避免结果乱序覆盖。
        _placeSearchCts?.Cancel();

        // 1. 先用本地最近地点瞬时匹配（离线也能用，在线时也会排在最上方）。
        var recent = PlaceSearchService.GetRecent(keyword, limit: keyword.Length == 0 ? 5 : 4);
        if (recent.Count > 0)
        {
            PlaceSuggestionsList.ItemsSource = recent;
            PlaceSuggestionsList.SelectedIndex = -1;
            ShowPlaceSuggestions();
        }
        else if (keyword.Length < 2)
        {
            PlaceSuggestionsList.ItemsSource = null;
            HidePlaceSuggestions();
        }

        if (keyword.Length < 2)
        {
            SetPlaceSearchLoading(false);
            return;
        }

        var cts = new CancellationTokenSource();
        _placeSearchCts = cts;

        try
        {
            if (debounceMs > 0)
            {
                await Task.Delay(debounceMs, cts.Token);
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            SetPlaceSearchLoading(true);
            var online = await PlaceSearchService.SearchAsync(keyword, limit: 6, token: cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            // 合并：匹配的最近地点置顶 + 去重后的在线结果（最多 6 条）；断网时自动降级保留 recent。
            var merged = new List<PlaceSuggestion>(6);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in recent.Concat(online))
            {
                if (seen.Add(item.DisplayName))
                {
                    merged.Add(item);
                    if (merged.Count >= 6)
                    {
                        break;
                    }
                }
            }

            PlaceSuggestionsList.ItemsSource = merged;
            PlaceSuggestionsList.SelectedIndex = -1;
            if (merged.Count > 0)
            {
                ShowPlaceSuggestions();
            }
            else
            {
                HidePlaceSuggestions();
            }
        }
        catch (OperationCanceledException)
        {
            // 用户继续输入导致取消，静默忽略。
        }
        finally
        {
            if (ReferenceEquals(_placeSearchCts, cts))
            {
                SetPlaceSearchLoading(false);
            }
        }
    }

    private void SetPlaceSearchLoading(bool loading)
    {
        PlaceSearchProgress.IsActive = loading;
        PlaceSearchProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
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
        if (PlaceSuggestionsPopup.IsOpen)
        {
            HidePlaceSuggestions();
            return;
        }

        Finish(false);
    }

    private void Finish(bool saved)
    {
        if (_finished)
        {
            return;
        }

        _placeSearchCts?.Cancel();
        HidePlaceSuggestions();

        if (saved)
        {
            var summary = TitleBox.Text.Trim();
            if (summary.Length == 0)
            {
                TitleBox.Focus(FocusState.Programmatic);
                return;
            }

            var source = SelectedSource;
            if (source is null)
            {
                return;
            }

            var allDay = AllDayToggle.IsOn;
            var start = ReadStart();
            if (start is null)
            {
                return;
            }

            // 全天事件：draft.End 与写入层约定为「含当天」，直接取表单里的结束日期；
            // 序列化成 ICS 时由 BuildIcs / PatchMasterEvent 统一 +1 天转成排他端。
            var endDateValue = EndDate.Date?.Date ?? start.Value.Date;
            if (endDateValue < start.Value.Date)
            {
                endDateValue = start.Value.Date;
            }

            var end = allDay
                ? endDateValue
                : start.Value + ReadDuration();

            var repeat = RepeatBox.SelectedIndex >= 0 && RepeatBox.SelectedIndex < RepeatOptions.Count
                ? RepeatOptions[RepeatBox.SelectedIndex]
                : null;

            var location = LocationBox.Text.Trim();
            PlaceSearchService.RecordRecent(location);

            Draft = new CalendarSyncService.CalendarEventDraft
            {
                Summary = summary,
                Start = start.Value,
                End = end,
                AllDay = allDay,
                Location = location,
                Notes = NoteBox.Text.Trim(),
                Url = UrlBox.Text.Trim(),
                Categories = CategoryBox.Text.Trim(),
                Busy = _editing?.IsBusy ?? true,
                // iCloud / Apple 日历没有「公开/私密/机密」这套东西，界面上不再给选项；
                // 编辑已有日程时原样带回去（缺省按 PUBLIC）。
                Class = string.IsNullOrWhiteSpace(_editing?.Class) ? "PUBLIC" : _editing!.Class,
                Priority = PriorityBox.SelectedIndex switch
                {
                    1 => 1,
                    2 => 5,
                    3 => 9,
                    _ => 0,
                },
                RRule = repeat?.RRule ?? _customRRule,
            };
            TargetCalendarHref = source.Href;
            _saved = true;
        }

        // 先隐藏再关：和别处一致，避免关窗瞬间黑一帧。
        _appWindow.Hide();
        _finished = true;
        DispatcherQueue.TryEnqueue(Close);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _finished = true;
        WindowCloak.Cancel(_hwnd, reveal: false);
        _result.TrySetResult(_saved);
        WindowAcrylic.DetachAndDispose(ref _acrylic, ref _backdropConfig);
    }
}
