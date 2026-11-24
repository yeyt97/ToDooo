using System.Diagnostics;
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
using ToDooo.ViewModels;
using Windows.Graphics;
using Windows.UI;

namespace ToDooo.Views;

/// <summary>外观 / 行为 / 数据设置。改动即时生效，窗口本身也跟随主题。</summary>
public sealed partial class SettingsWindow : Window
{
    private const int LogicalWidth = 780;
    private const int LogicalHeight = 660;

    /// <summary>侧栏完整宽度；窗口窄于此值时侧栏只留图标。</summary>
    private const double NavExpandedWidth = 196;
    private const double NavCompactWidth = 56;

    /// <summary>低于这个宽度，侧栏文字会被收起，只留图标。</summary>
    private const double NavCollapseThreshold = 620;

    private const int MinLogicalWidth = 460;
    private const int MinLogicalHeight = 480;

    private static readonly int[] LeadOptions = { 0, 5, 15, 30, 60 };

    private readonly SettingsService _settings;
    private readonly Action _settingsChanged;
    private readonly Func<Task> _exportRequested;
    private readonly Func<Task>? _importRequested;
    private readonly CalendarSyncService? _calendarSync;
    private bool _calendarReady;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private SystemBackdropConfiguration? _backdropConfig;
    private DesktopAcrylicController? _acrylic;
    private bool _ready;
    private bool _closing;
    private int _navResizeHit;
    private NativeMethods.POINT _navResizeStartCursor;
    private NativeMethods.RECT _navResizeStartRect;

    public SettingsWindow(
        SettingsService settings,
        Action settingsChanged,
        Func<Task> exportRequested,
        Func<Task>? importRequested = null,
        CalendarSyncService? calendarSync = null)
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _exportRequested = exportRequested;
        _importRequested = importRequested;
        _calendarSync = calendarSync;

        InitializeComponent();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();

        AboutText.Text = AppInfo.AboutLine;

        ThemeBox.SelectedIndex = settings.Current.ThemeMode;
        var transparency = Math.Clamp(1.0 - settings.Current.WindowOpacity, 0.0, 0.9);
        OpacitySlider.Value = transparency;
        OpacityValue.Text = $"{Math.Round(transparency * 100)}%";
        TintBox.SelectedIndex = Math.Clamp(settings.Current.WindowTintMode, 0, 3);
        StartupToggle.IsOn = StartupService.IsEnabled;
        AutoHideToggle.IsOn = settings.Current.AutoHide;
        DefaultTabBox.SelectedIndex = Math.Clamp(settings.Current.DefaultTab, 0, 3);
        HotkeyBox.SelectedIndex = Math.Clamp(settings.Current.GlobalHotkeyMode, 0, 3);
        QuickAddHotkeyBox.SelectedIndex = Math.Clamp(settings.Current.QuickAddHotkeyMode, 0, 3);
        BuildSmartViewList();
        BuildTagNameEditors();
        LeadBox.SelectedIndex = Math.Max(0, Array.IndexOf(LeadOptions, settings.Current.ReminderLeadMinutes));
        LoadCalendarSettings();
        CalendarCard.Visibility = calendarSync is null ? Visibility.Collapsed : Visibility.Visible;

        // 首次显示前按初始宽度排一次侧栏，避免先宽后窄闪一下。
        UpdateNavLayout(LogicalWidth);
        UpdateNavSelectionVisuals(CurrentNavTag());

        _ready = true;
        Closed += (_, _) =>
        {
            WindowCloak.Cancel(_hwnd, reveal: false);
            WindowAcrylic.DetachAndDispose(ref _acrylic, ref _backdropConfig);
        };
    }

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
        });
    }

    private void ApplyLogicalSize(double scale)
    {
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalWidth * scale),
            (int)Math.Round(LogicalHeight * scale)));
    }

    /// <summary>系统主题变化时由主窗口调用，跟随系统模式下重刷本窗口外观。</summary>
    public void RefreshTheme()
    {
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode);
        UpdateNavSelectionVisuals(CurrentNavTag());
        ApplyWindowMaterial();
    }

    private void ConfigureWindow()
    {
        _appWindow.Title = "设置";
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            // 设置窗口要能改宽度：侧栏会随宽度在「图标+文字」和「只剩图标」之间切换。
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.PreferredMinimumWidth = MinLogicalWidth;
            presenter.PreferredMinimumHeight = MinLogicalHeight;
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

        // 边缘缩放手势：无边框窗口要自己判定命中的边。
        Root.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(Root_NavResizePressed),
            handledEventsToo: true);
        Root.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(Root_NavResizeMoved),
            handledEventsToo: true);
        Root.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(Root_NavResizeReleased),
            handledEventsToo: true);
        Root.PointerExited += (_, _) =>
        {
            if (_navResizeHit == 0)
            {
                Root.SetCursor(null);
            }
        };
        Root.SizeChanged += (_, args) => UpdateNavLayout(args.NewSize.Width);

        ApplyWindowMaterial();
    }

    // ---- 侧栏导航 + 响应式收缩 -------------------------------------------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        GeneralPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        RemindersPage.Visibility = tag == "reminders" ? Visibility.Visible : Visibility.Collapsed;
        CalendarPage.Visibility = tag == "calendar" ? Visibility.Visible : Visibility.Collapsed;

        SectionTitle.Text = tag switch
        {
            "reminders" => "提醒事项",
            "calendar" => "日历",
            _ => "通用",
        };

        UpdateNavSelectionVisuals(tag);
    }

    /// <summary>侧栏选中态：整条高亮底色 + 左侧竖条 + 图标文字提亮。</summary>
    private void UpdateNavSelectionVisuals(string selectedTag)
    {
        // XAML 加载过程中 Checked 可能先于其它元素就绪触发，等全部就位后再画。
        if (NavGeneralBar is null || NavRemindersBar is null || NavCalendarBar is null)
        {
            return;
        }

        var resources = Application.Current.Resources;
        var accent = (Brush)resources["AccentBrush"];
        var primary = (Brush)resources["TextPrimaryBrush"];
        var secondary = (Brush)resources["TextSecondaryBrush"];
        var selectedBackground = (Brush)resources["NavSelectedBrush"];
        var transparent = (Brush)resources["TransparentBrush"];

        void Apply(RadioButton item, Border bar, FontIcon icon, TextBlock text, bool selected)
        {
            item.Background = selected ? selectedBackground : transparent;
            bar.Background = accent;
            bar.Opacity = selected ? 1 : 0;
            icon.Foreground = selected ? primary : secondary;
            text.Foreground = selected ? primary : secondary;
        }

        Apply(NavGeneral, NavGeneralBar, NavGeneralIcon, NavGeneralText, selectedTag == "general");
        Apply(NavReminders, NavRemindersBar, NavRemindersIcon, NavRemindersText, selectedTag == "reminders");
        Apply(NavCalendar, NavCalendarBar, NavCalendarIcon, NavCalendarText, selectedTag == "calendar");
    }

    private string CurrentNavTag() =>
        NavGeneral.IsChecked == true ? "general" :
        NavReminders.IsChecked == true ? "reminders" : "calendar";

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _navAnimTimer;
    private int _navAnimStep;
    private double _navAnimFrom;
    private double _navAnimTo;
    private bool _navAnimCompact;
    private bool _navCompact;
    private const int NavAnimSteps = 10;

    /// <summary>窗口变窄时收掉侧栏文字只留图标；伸缩做成分段过渡，而不是瞬间缩掉。</summary>
    private void UpdateNavLayout(double widthDip)
    {
        if (widthDip <= 0)
        {
            return;
        }

        // SizeChanged 传进来的已经是 DIP，不要再除缩放，否则高 DPI 下会误判成窄屏。
        var compact = widthDip < NavCollapseThreshold;
        var target = compact ? NavCompactWidth : NavExpandedWidth;
        if (compact == _navCompact && _navAnimTimer is null &&
            Math.Abs(NavColumn.Width.Value - target) < 0.5)
        {
            return;
        }

        _navCompact = compact;
        _navAnimCompact = compact;
        _navAnimFrom = NavColumn.Width.Value;
        _navAnimTo = target;
        _navAnimStep = 0;

        // 展开时立刻显示文字（靠裁剪慢慢露出来）；收起时等动画结束再隐藏。
        if (!compact)
        {
            NavGeneralText.Visibility = Visibility.Visible;
            NavRemindersText.Visibility = Visibility.Visible;
            NavCalendarText.Visibility = Visibility.Visible;
        }

        // 图标在收起态居中，动画一开始就切好，避免过程中左右乱跳。
        // 收起时把左侧 4px 指示条列收掉、间距归零，否则图标会被推到裁剪边缘露出半截。
        var iconMargin = compact ? new Thickness(0) : new Thickness(0);
        NavGeneralIcon.Margin = iconMargin;
        NavRemindersIcon.Margin = iconMargin;
        NavCalendarIcon.Margin = iconMargin;

        foreach (var (grid, barColumn, iconColumn, icon) in new[]
                 {
                     (NavGeneralGrid, NavGeneralBarColumn, NavGeneralIconColumn, NavGeneralIcon),
                     (NavRemindersGrid, NavRemindersBarColumn, NavRemindersIconColumn, NavRemindersIcon),
                     (NavCalendarGrid, NavCalendarBarColumn, NavCalendarIconColumn, NavCalendarIcon),
                 })
        {
            grid.ColumnSpacing = compact ? 0 : 12;
            barColumn.Width = compact ? new GridLength(0) : new GridLength(4);
            iconColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            icon.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }

        NavPanel.Margin = compact
            ? new Thickness(6, 4, 6, 0)
            : new Thickness(12, 4, 8, 0);

        if (_navAnimTimer is null)
        {
            _navAnimTimer = DispatcherQueue.CreateTimer();
            _navAnimTimer.Interval = TimeSpan.FromMilliseconds(14);
            _navAnimTimer.Tick += (_, _) => OnNavAnimTick();
        }

        _navAnimTimer.Start();
        ApplyNavFrame(_navAnimFrom);
    }

    private void OnNavAnimTick()
    {
        _navAnimStep++;
        var t = Math.Min(1.0, (double)_navAnimStep / NavAnimSteps);
        // ease-out，收尾更柔和
        var eased = 1 - Math.Pow(1 - t, 3);
        var width = _navAnimFrom + (_navAnimTo - _navAnimFrom) * eased;
        ApplyNavFrame(width);

        if (_navAnimStep >= NavAnimSteps)
        {
            _navAnimTimer?.Stop();
            ApplyNavFrame(_navAnimTo);
            if (_navAnimCompact)
            {
                NavGeneralText.Visibility = Visibility.Collapsed;
                NavRemindersText.Visibility = Visibility.Collapsed;
                NavCalendarText.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>按当前宽度落列宽并裁剪侧栏内容，文字随宽度被一点点裁掉而不是直接消失。</summary>
    private void ApplyNavFrame(double width)
    {
        NavColumn.Width = new GridLength(width);
        var height = NavPanel.ActualHeight > 0 ? NavPanel.ActualHeight : 320;
        // 裁剪宽度取侧栏整宽：NavPanel 自身已有左右外边距，再减宽度会把图标切掉一截。
        NavPanel.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, width), height),
        };
    }

    private int EdgeHit(Windows.Foundation.Point point)
    {
        const double edge = 6;
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

    private void Root_NavResizePressed(object sender, PointerRoutedEventArgs e)
    {
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

        // 在标题栏里按下时交给标题栏拖动，避免拖窗和缩放打架。
        if (local.Y < HeaderBar.ActualHeight)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out _navResizeStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _navResizeStartRect))
        {
            return;
        }

        _navResizeHit = hit;
        Root.CapturePointer(e.Pointer);
        ApplyResizeCursor(hit);
        e.Handled = true;
    }

    private void Root_NavResizeMoved(object sender, PointerRoutedEventArgs e)
    {
        var local = e.GetCurrentPoint(Root).Position;
        if (_navResizeHit == 0)
        {
            ApplyResizeCursor(EdgeHit(local));
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var minWidth = (int)Math.Round(MinLogicalWidth * scale);
        var minHeight = (int)Math.Round(MinLogicalHeight * scale);
        var dx = cursor.X - _navResizeStartCursor.X;
        var dy = cursor.Y - _navResizeStartCursor.Y;

        var left = _navResizeStartRect.Left;
        var top = _navResizeStartRect.Top;
        var right = _navResizeStartRect.Right;
        var bottom = _navResizeStartRect.Bottom;

        if (_navResizeHit is NativeMethods.HTLEFT or NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMLEFT)
        {
            left = Math.Min(_navResizeStartRect.Left + dx, _navResizeStartRect.Right - minWidth);
        }

        if (_navResizeHit is NativeMethods.HTRIGHT or NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMRIGHT)
        {
            right = Math.Max(_navResizeStartRect.Right + dx, _navResizeStartRect.Left + minWidth);
        }

        if (_navResizeHit is NativeMethods.HTTOP or NativeMethods.HTTOPLEFT or NativeMethods.HTTOPRIGHT)
        {
            top = Math.Min(_navResizeStartRect.Top + dy, _navResizeStartRect.Bottom - minHeight);
        }

        if (_navResizeHit is NativeMethods.HTBOTTOM or NativeMethods.HTBOTTOMLEFT or NativeMethods.HTBOTTOMRIGHT)
        {
            bottom = Math.Max(_navResizeStartRect.Bottom + dy, _navResizeStartRect.Top + minHeight);
        }

        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        e.Handled = true;
    }

    private void Root_NavResizeReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_navResizeHit == 0)
        {
            return;
        }

        _navResizeHit = 0;
        Root.ReleasePointerCapture(e.Pointer);
        ApplyResizeCursor(EdgeHit(e.GetCurrentPoint(Root).Position));
        e.Handled = true;
    }

    private void ApplyResizeCursor(int hit)
    {
        Root.SetCursor(hit switch
        {
            NativeMethods.HTLEFT or NativeMethods.HTRIGHT => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
            NativeMethods.HTTOP or NativeMethods.HTBOTTOM => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
            NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMRIGHT => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
            NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMLEFT => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
            _ => null,
        });
    }

    private void ApplyWindowMaterial()
    {
        var dark = Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        var darkFlag = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        if (_acrylic is null)
        {
            _backdropConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            };
            _acrylic = WindowAcrylic.Apply(
                this, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark, _backdropConfig,
                (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, 3));
            return;
        }

        WindowAcrylic.ApplyTint(_acrylic, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark,
            (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, 3));
        if (_backdropConfig is not null)
        {
            _backdropConfig.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        }
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

    private void MoveCenterOver(AppWindow? owner)
    {
        var size = _appWindow.Size;
        var point = new PointInt32(0, 0);
        var placed = false;

        if (owner is not null)
        {
            var ownerHwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(owner.Id);
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

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.Current.ThemeMode = ThemeBox.SelectedIndex;
        _settings.Save();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)ThemeBox.SelectedIndex);
        ApplyWindowMaterial();
        _settingsChanged();
    }

    private void TintBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.Current.WindowTintMode = Math.Clamp(TintBox.SelectedIndex, 0, 3);
        _settings.Save();
        ApplyWindowMaterial();
        _settingsChanged();
    }

    private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OpacityValue.Text = $"{Math.Round(e.NewValue * 100)}%";
        if (!_ready)
        {
            return;
        }

        _settings.Current.WindowOpacity = Math.Clamp(1.0 - e.NewValue, 0.1, 1.0);
        _settings.Save();
        ApplyWindowMaterial();
        _settingsChanged();
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        StartupService.SetEnabled(StartupToggle.IsOn);
    }

    private void AutoHideToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.Current.AutoHide = AutoHideToggle.IsOn;
        _settings.Save();
        _settingsChanged();
    }

    private void DefaultTabBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || DefaultTabBox.SelectedIndex < 0)
        {
            return;
        }

        _settings.Current.DefaultTab = DefaultTabBox.SelectedIndex;
        _settings.Save();
    }

    private void BuildSmartViewList()
    {
        NormalizeSmartViews();
        SmartViewList.Children.Clear();

        var views = _settings.Current.SmartViews;
        for (var i = 0; i < views.Count; i++)
        {
            var index = i;
            var setting = views[i];
            if (!Enum.IsDefined(typeof(SmartView), setting.View))
            {
                continue;
            }

            var check = new CheckBox
            {
                Content = SmartViewInfo.Title((SmartView)setting.View),
                IsChecked = setting.Enabled,
                MinWidth = 0,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.Click += (_, _) =>
            {
                setting.Enabled = check.IsChecked == true;
                SaveSmartViews();
            };

            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var up = CreateArrowButton("\uE70E", index > 0, "上移");
            up.Click += (_, _) => MoveSmartView(index, -1);
            Grid.SetColumn(up, 1);

            var down = CreateArrowButton("\uE70D", index < views.Count - 1, "下移");
            down.Click += (_, _) => MoveSmartView(index, 1);
            Grid.SetColumn(down, 2);

            row.Children.Add(check);
            row.Children.Add(up);
            row.Children.Add(down);
            SmartViewList.Children.Add(row);
        }
    }

    private static Button CreateArrowButton(string glyph, bool enabled, string name)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["LinkButtonStyle"],
            Content = new FontIcon { Glyph = glyph, FontSize = 9 },
            Width = 26,
            Height = 26,
            MinWidth = 0,
            Padding = new Thickness(0),
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    private void MoveSmartView(int index, int delta)
    {
        var views = _settings.Current.SmartViews;
        var target = index + delta;
        if (index < 0 || index >= views.Count || target < 0 || target >= views.Count)
        {
            return;
        }

        (views[index], views[target]) = (views[target], views[index]);
        SaveSmartViews();
        BuildSmartViewList();
    }

    private void SaveSmartViews()
    {
        _settings.Save();
        _settingsChanged();
    }

    // 配置里缺的视图补到末尾；未知值丢掉，避免以后改枚举时读出怪数据。
    private void NormalizeSmartViews()
    {
        var views = _settings.Current.SmartViews ??= SmartViewSetting.CreateDefault();
        views.RemoveAll(setting => !Enum.IsDefined(typeof(SmartView), setting.View));

        foreach (var view in SmartViewInfo.All)
        {
            if (views.All(setting => setting.View != (int)view))
            {
                views.Add(new SmartViewSetting { View = (int)view, Enabled = true });
            }
        }
    }

    private void LeadBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var index = Math.Clamp(LeadBox.SelectedIndex, 0, LeadOptions.Length - 1);
        _settings.Current.ReminderLeadMinutes = LeadOptions[index];
        _settings.Save();
        _settingsChanged();
    }

    private void HotkeyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.Current.GlobalHotkeyMode = Math.Clamp(HotkeyBox.SelectedIndex, 0, 3);
        _settings.Save();
        _settingsChanged();
    }

    private void QuickAddHotkeyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.Current.QuickAddHotkeyMode = Math.Clamp(QuickAddHotkeyBox.SelectedIndex, 0, 3);
        _settings.Save();
        _settingsChanged();
    }

    private void BuildTagNameEditors()
    {
        var names = _settings.Current.TagNames ??= new List<string>();
        while (names.Count < MainViewModel.TagColors.Length)
        {
            names.Add(string.Empty);
        }

        TagNamesGrid.Children.Clear();
        TagNamesGrid.RowDefinitions.Clear();
        TagNamesGrid.ColumnDefinitions.Clear();

        for (var r = 0; r < 4; r++)
        {
            TagNamesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        TagNamesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TagNamesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < MainViewModel.TagColors.Length; i++)
        {
            var index = i;
            var hex = MainViewModel.TagColors[i];
            var color = Color.FromArgb(
                255,
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));

            var cell = new Grid { ColumnSpacing = 8 };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            cell.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(color),
            });

            var box = new TextBox
            {
                Text = names[i] ?? string.Empty,
                PlaceholderText = $"标签 {i + 1}",
                MaxLength = 12,
                Height = 28,
                FontSize = 12,
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(5),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"标签 {i + 1} 名称");
            box.TextChanged += (_, _) =>
            {
                names[index] = box.Text.Trim();
                _settings.Save();
                _settingsChanged();
            };
            Grid.SetColumn(box, 1);
            cell.Children.Add(box);

            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            TagNamesGrid.Children.Add(cell);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        await _exportRequested();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_importRequested is not null)
        {
            await _importRequested();
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.Ensure();
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Root) { UseShellExecute = true });
        }
        catch
        {
            // 打不开资源管理器就算了
        }
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";
        try
        {
            var result = await UpdateService.CheckAsync(AppInfo.Version);
            if (!result.Succeeded)
            {
                UpdateStatusText.Text = "检查失败，请确认网络后重试";
                return;
            }

            if (!result.HasUpdate)
            {
                UpdateStatusText.Text = $"已是最新版本（{AppInfo.DisplayVersion}）";
                return;
            }

            UpdateStatusText.Text = $"发现新版本 {result.Version}，正在打开下载页…";
            if (result.Url is { } url)
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private static readonly int[] RefreshOptions = { 0, 5, 15, 30, 60 };

    private void LoadCalendarSettings()
    {
        if (_calendarSync is null)
        {
            return;
        }

        _calendarReady = false;
        AppleIdBox.Text = _calendarSync.AppleId;
        var refreshIndex = Math.Max(0, Array.IndexOf(RefreshOptions, _settings.Current.CalendarRefreshMinutes));
        CalendarRefreshBox.SelectedIndex = refreshIndex;
        UpdateCalendarStatus();
        RebuildCalendarSources();
        UpdateSyncStatusText();
        _calendarReady = true;
    }

    /// <summary>「最近同步时间 + 待补传条数」，让用户能看出是否还有东西没上云。</summary>
    private void UpdateSyncStatusText()
    {
        if (_calendarSync is null || SyncStatusText is null)
        {
            return;
        }

        var pending = _calendarSync.PendingCount;
        var synced = _calendarSync.LastSyncedAt;
        SyncStatusText.Text = synced is null
            ? pending > 0 ? $"尚未同步，{pending} 项待上传" : "尚未同步"
            : pending > 0
                ? $"上次同步 {synced:HH:mm}，{pending} 项待上传"
                : $"上次同步 {synced:HH:mm}";
    }

    private void UpdateCalendarStatus()
    {
        if (_calendarSync is null)
        {
            return;
        }

        var connected = _calendarSync.IsConfigured;

        // 状态卡：圆点 + 一句话标题说清连没连上，细节放第二行。
        CalendarHeadlineText.Text = connected ? $"已连接 {_calendarSync.AppleId}" : "iCloud 日历尚未连接";
        CalendarStatusText.Text = connected
            ? "日程会与 iCloud 双向同步。待办只存在本机。"
            : "按下面三步操作，大约两分钟。";
        CalendarStatusDot.Fill = (Brush)Application.Current.Resources[
            connected ? "AccentBrush" : "TextMutedBrush"];

        // 教程只在没连接时显示；已连接就收起来，别占地方。
        CalendarSetupPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;

        // 同一张表单两种用途：首次连接 / 修改账号或换密码。
        CalendarFormTitle.Text = connected ? "修改账号或密码" : "账号";
        CalendarSaveButton.Content = connected ? "保存修改" : "保存并连接";

        // 第 2、3 步的编号徽标只在教程模式下显示，已连接后换回普通标题。
        CalendarFormStepHeader.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        CalendarFormTitle.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        CalendarSourcesStepHeader.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        CalendarSourcesTitle.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        CalendarClearButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        AppPasswordBox.PlaceholderText = connected ? "新密码（留空则保持不变）" : "应用专用密码";
    }

    /// <summary>教程第 1 步的直达按钮：打开 Apple 账户管理页，省得手输网址。</summary>
    private async void OpenAppleAccount_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate("https://account.apple.com", UriKind.Absolute, out var uri))
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
            catch
            {
                // 没有默认浏览器时静默；用户仍可照着说明手动打开
            }
        }
    }

    private void RebuildCalendarSources()
    {
        CalendarSourceList.Children.Clear();
        if (_calendarSync is null)
        {
            return;
        }

        var sources = _calendarSync.Repository.GetSources();
        if (sources.Count == 0)
        {
            CalendarSourceList.Children.Add(new TextBlock
            {
                Text = "同步一次后会列出你的 iCloud 日历。",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
            return;
        }

        foreach (var source in sources)
        {
            var check = new CheckBox
            {
                Content = source.DisplayName,
                IsChecked = source.IsVisible,
                Tag = source.Href,
            };
            check.Click += (_, _) =>
            {
                _calendarSync.Repository.SetSourceVisible(source.Href, check.IsChecked == true);
            };
            CalendarSourceList.Children.Add(check);
        }
    }

    private async void CalendarSave_Click(object sender, RoutedEventArgs e)
    {
        if (_calendarSync is null)
        {
            return;
        }

        var appleId = AppleIdBox.Text.Trim();
        var password = AppPasswordBox.Password.Trim();
        if (string.IsNullOrEmpty(appleId))
        {
            CalendarStatusText.Text = "请先填写 Apple ID。";
            return;
        }

        if (string.IsNullOrEmpty(password) && !_calendarSync.IsConfigured)
        {
            CalendarStatusText.Text = "请填写应用专用密码。";
            return;
        }

        CalendarSaveButton.IsEnabled = false;
        CalendarStatusText.Text = "正在验证…";
        _settingsChanged();
        try
        {
            var effectivePassword = string.IsNullOrEmpty(password) ? _calendarSync.Password : password;
            var result = await _calendarSync.TestConnectionAsync(appleId, effectivePassword);
            if (!result.Succeeded)
            {
                CalendarStatusText.Text = result.Message;
                return;
            }

            _calendarSync.SaveCredentials(appleId, effectivePassword);
            AppPasswordBox.Password = string.Empty;
            CalendarStatusText.Text = result.Message + " 正在同步…";

            var now = DateTime.Today;
            var sync = await _calendarSync.SyncAsync(now.AddMonths(-1), now.AddMonths(2));
            UpdateCalendarStatus();
            if (sync.Succeeded)
            {
                // 同步结果并入状态行；失败原因由 SyncAsync 的消息带出
                CalendarStatusText.Text = $"连接成功。{sync.Message}";
            }
            else
            {
                CalendarStatusText.Text = $"已连接，但同步失败：{sync.Message}";
            }

            RebuildCalendarSources();
            UpdateSyncStatusText();
            _settingsChanged();
        }
        finally
        {
            CalendarSaveButton.IsEnabled = true;
        }
    }

    private void CalendarClear_Click(object sender, RoutedEventArgs e)
    {
        if (_calendarSync is null)
        {
            return;
        }

        _calendarSync.ClearCredentials();
        AppleIdBox.Text = string.Empty;
        AppPasswordBox.Password = string.Empty;
        UpdateCalendarStatus();
        SyncStatusText.Text = "尚未同步";
        RebuildCalendarSources();
        _settingsChanged();
    }

    private async void CalendarSyncNow_Click(object sender, RoutedEventArgs e)
    {
        if (_calendarSync is null || !_calendarSync.IsConfigured)
        {
            return;
        }

        SyncNowButton.IsEnabled = false;
        try
        {
            var now = DateTime.Today;
            var result = await _calendarSync.SyncAsync(now.AddMonths(-1), now.AddMonths(2));
            UpdateCalendarStatus();
            if (!result.Succeeded)
            {
                CalendarStatusText.Text = result.Message;
            }

            RebuildCalendarSources();
            UpdateSyncStatusText();
            _settingsChanged();
        }
        finally
        {
            SyncNowButton.IsEnabled = true;
        }
    }

    private void CalendarRefreshBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_calendarReady)
        {
            return;
        }

        var index = Math.Clamp(CalendarRefreshBox.SelectedIndex, 0, RefreshOptions.Length - 1);
        _settings.Current.CalendarRefreshMinutes = RefreshOptions[index];
        _settings.Save();
        _settingsChanged();
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e) => CloseWindow();
    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CloseWindow();
    }

    private void CloseWindow()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        // 和编辑弹窗一致：先隐藏再关，避免关窗瞬间黑一帧。
        _appWindow.Hide();
        DispatcherQueue.TryEnqueue(Close);
    }
}