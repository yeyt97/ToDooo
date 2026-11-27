using System.Diagnostics;
using System.IO;
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
    private readonly Action? _settingsChangedForExit;
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
    private bool _rebuildingSources;
    private int _navResizeHit;
    private NativeMethods.POINT _navResizeStartCursor;
    private NativeMethods.RECT _navResizeStartRect;

    public SettingsWindow(
        SettingsService settings,
        Action settingsChanged,
        Func<Task> exportRequested,
        Func<Task>? importRequested = null,
        CalendarSyncService? calendarSync = null,
        Action? exitRequested = null)
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _exportRequested = exportRequested;
        _importRequested = importRequested;
        _calendarSync = calendarSync;
        _settingsChangedForExit = exitRequested;

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
        StickyAcrylicToggle.IsOn = settings.Current.StickyAcrylicEnabled;
        StickyOpacityRow.Visibility = settings.Current.StickyAcrylicEnabled ? Visibility.Visible : Visibility.Collapsed;
        var stickyTransparency = Math.Clamp(1.0 - settings.Current.StickyOpacity, 0.0, 0.9);
        StickyOpacitySlider.Value = stickyTransparency;
        StickyOpacityValue.Text = $"{Math.Round(stickyTransparency * 100)}%";
        var stickyFontSize = Math.Clamp(settings.Current.StickyDefaultFontSize, 11.0, 20.0);
        StickyFontSizeSlider.Value = stickyFontSize;
        StickyFontSizeValue.Text = $"{stickyFontSize:0} px";
        StickyDefaultPinnedToggle.IsOn = settings.Current.StickyDefaultPinned;
        StickyRestoreToggle.IsOn = settings.Current.StickyRestoreOnStartup;
        StickyAutoDockTopToggle.IsOn = settings.Current.StickyAutoDockTop;
        BuildStickyColorSwatches();
        BuildTintSwatches();
        StartupToggle.IsOn = StartupService.IsEnabled;
        AutoHideToggle.IsOn = settings.Current.AutoHide;
        AutoHideDelayBox.Value = Math.Clamp(settings.Current.AutoHideDelaySeconds, 0, 600);
        AutoHideDelayRow.Visibility = settings.Current.AutoHide ? Visibility.Visible : Visibility.Collapsed;
        SmartViewToggle.IsOn = settings.Current.SmartViewEnabled;
        SmartViewOptions.Visibility = settings.Current.SmartViewEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        DefaultTabBox.SelectedIndex = Math.Clamp(settings.Current.DefaultTab, 0, 3);
        HotkeyBox.SelectedIndex = Math.Clamp(settings.Current.GlobalHotkeyMode, 0, 3);
        QuickAddHotkeyBox.SelectedIndex = Math.Clamp(settings.Current.QuickAddHotkeyMode, 0, 3);
        BuildSmartViewList();
        ColorTagToggle.IsOn = settings.Current.ColorTagsEnabled;
        ColorTagOptions.Visibility = settings.Current.ColorTagsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        BuildTagNameEditors();
        LeadBox.SelectedIndex = Math.Max(0, Array.IndexOf(LeadOptions, settings.Current.ReminderLeadMinutes));
        LoadCalendarSettings();
        LoadPlaceSettings();

        // 后台检查发现过新版本时，进设置就能看到，不用再点一次检查。
        if (!string.IsNullOrWhiteSpace(settings.Current.PendingUpdateVersion))
        {
            UpdateStatusText.Text = $"发现新版本 {settings.Current.PendingUpdateVersion}，点「检查更新」可直接安装。";
        }
        CalendarCard.Visibility = calendarSync is null ? Visibility.Collapsed : Visibility.Visible;

        // 首次显示前按初始宽度排一次侧栏，避免先宽后窄闪一下。
        UpdateNavLayout(LogicalWidth);
        UpdateNavSelectionVisuals(CurrentNavTag());

        _ready = true;
        Closed += (_, _) =>
        {
            WindowCloak.Cancel(_hwnd, reveal: false);
            WindowAcrylic.DetachAndDispose(ref _acrylic, ref _backdropConfig);

            if (_calendarSync is not null)
            {
                _calendarSync.Repository.SourceVisibilityChanged -= OnSourceVisibilityChanged;
            }
        };

        // 日历筛选菜单里改的显示开关要立刻反映到这一页的勾选上。
        if (_calendarSync is not null)
        {
            _calendarSync.Repository.SourceVisibilityChanged += OnSourceVisibilityChanged;
        }
    }

    private void OnSourceVisibilityChanged(object? sender, string href)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_rebuildingSources)
            {
                return;
            }

            RebuildCalendarSources();
        });
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
            // 窗口要能改宽度（侧栏会随宽度收缩），但缩放由下面自己处理边缘命中：
            // 交给 presenter 的 IsResizable 会让系统在顶部留一条非客户区，
            // 无边框窗口上就表现成一条黑条。
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
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
        StickyPage.Visibility = tag == "sticky" ? Visibility.Visible : Visibility.Collapsed;

        SectionTitle.Text = tag switch
        {
            "reminders" => "提醒事项",
            "calendar" => "日历",
            "sticky" => "便签",
            _ => "通用",
        };

        UpdateNavSelectionVisuals(tag);
    }

    /// <summary>侧栏选中态：整条高亮底色 + 左侧竖条 + 图标文字提亮。</summary>
    private void UpdateNavSelectionVisuals(string selectedTag)
    {
        // XAML 加载过程中 Checked 可能先于其它元素就绪触发，等全部就位后再画。
        if (NavGeneralBar is null || NavRemindersBar is null || NavCalendarBar is null || NavStickyBar is null)
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
        Apply(NavSticky, NavStickyBar, NavStickyIcon, NavStickyText, selectedTag == "sticky");
    }

    private string CurrentNavTag() =>
        NavGeneral.IsChecked == true ? "general" :
        NavReminders.IsChecked == true ? "reminders" :
        NavSticky.IsChecked == true ? "sticky" : "calendar";

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
            NavStickyText.Visibility = Visibility.Visible;
        }

        // 图标在收起态居中，动画一开始就切好，避免过程中左右乱跳。
        // 收起时把左侧 4px 指示条列收掉、间距归零，否则图标会被推到裁剪边缘露出半截。
        var iconMargin = compact ? new Thickness(0) : new Thickness(0);
        NavGeneralIcon.Margin = iconMargin;
        NavRemindersIcon.Margin = iconMargin;
        NavCalendarIcon.Margin = iconMargin;
        NavStickyIcon.Margin = iconMargin;

        foreach (var (grid, barColumn, iconColumn, icon) in new[]
                 {
                     (NavGeneralGrid, NavGeneralBarColumn, NavGeneralIconColumn, NavGeneralIcon),
                     (NavRemindersGrid, NavRemindersBarColumn, NavRemindersIconColumn, NavRemindersIcon),
                     (NavCalendarGrid, NavCalendarBarColumn, NavCalendarIconColumn, NavCalendarIcon),
                     (NavStickyGrid, NavStickyBarColumn, NavStickyIconColumn, NavStickyIcon),
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
                NavStickyText.Visibility = Visibility.Collapsed;
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

    private bool? _lastDarkMode;

    private void ApplyWindowMaterial()
    {
        var dark = Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        if (_lastDarkMode != dark)
        {
            _lastDarkMode = dark;
            var darkFlag = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(
                _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));
        }

        if (_acrylic is null)
        {
            _backdropConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            };
            _acrylic = WindowAcrylic.Apply(
                this, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark, _backdropConfig,
                (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, Appearance.TintCount - 1), _settings.Current.CustomTintColor);
            return;
        }

        WindowAcrylic.ApplyTint(_acrylic, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark,
            (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, Appearance.TintCount - 1), _settings.Current.CustomTintColor);
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
        if (Helpers.WindowDragHelper.TryBeginTouchDrag(_hwnd, Root, (UIElement)sender, e))
        {
            return;
        }

        if (!Helpers.WindowDragHelper.IsPrimaryPointerPressed((UIElement)sender, e) ||
            !Helpers.WindowDragHelper.TryGetScreenPoint(_hwnd, Root, e, out _dragStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _dragStartRect))
        {
            return;
        }

        _dragging = ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void HeaderBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Helpers.WindowDragHelper.TryMoveTouchDrag(_hwnd, Root, e))
        {
            return;
        }

        if (!_dragging)
        {
            return;
        }

        if (Helpers.WindowDragHelper.TryGetScreenPoint(_hwnd, Root, e, out var cursor))
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
        if (Helpers.WindowDragHelper.TryEndTouchDrag(_hwnd, e))
        {
            return;
        }

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

        // 深浅主题下底色是两个色值，切换后色块要重画才所见即所得。
        BuildTintSwatches();
        _settingsChanged();
    }

    private readonly List<Button> _tintSwatches = new();

    /// <summary>
    /// 底色用色块预览：每个方案画成一个半深半浅的圆片（左深右浅），
    /// 这样不管当前是什么主题，九种底色的色相差别都一眼能分辨出来。
    /// </summary>
    private void BuildTintSwatches()
    {
        TintSwatchPanel.Children.Clear();
        _tintSwatches.Clear();
        // 用了自定义色时，预设色块都不高亮。
        var hasCustom = _settings.Current.CustomTintColor is { Length: > 0 };
        var selected = hasCustom
            ? -1
            : Math.Clamp(_settings.Current.WindowTintMode, 0, Appearance.TintCount - 1);

        for (var i = 0; i < Appearance.TintCount; i++)
        {
            var index = i;
            var (darkTint, _) = Appearance.TintColors((Appearance.TintMode)i, dark: true);
            var (lightTint, _) = Appearance.TintColors((Appearance.TintMode)i, dark: false);

            // 上深下浅的双色圆片：一眼看出这个方案在两种主题下的样子。
            var dot = new Grid
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 26, 26) },
            };
            dot.Children.Add(new Border
            {
                Background = new SolidColorBrush(Solid(darkTint)),
                VerticalAlignment = VerticalAlignment.Top,
                Height = 13,
            });
            dot.Children.Add(new Border
            {
                Background = new SolidColorBrush(Solid(lightTint)),
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = 13,
            });
            dot.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            });

            var button = new Button
            {
                Width = 38,
                Height = 38,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Background = index == selected
                    ? (Brush)Application.Current.Resources["SegmentSelectedBrush"]
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(19),
                Content = dot,
                Tag = index,
            };
            ToolTipService.SetToolTip(button, Appearance.TintNames[i]);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button, $"底色 {Appearance.TintNames[i]}");
            button.Click += TintSwatch_Click;

            _tintSwatches.Add(button);
            TintSwatchPanel.Children.Add(button);
        }

        // 最后一个是调色盘入口：彩色渐变圆片，点开自选颜色。
        var customButton = new Button
        {
            Width = 38,
            Height = 38,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(19),
            Background = _settings.Current.CustomTintColor is { Length: > 0 }
                ? (Brush)Application.Current.Resources["SegmentSelectedBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Microsoft.UI.Colors.Red, Offset = 0 },
                        new GradientStop { Color = Microsoft.UI.Colors.Yellow, Offset = 0.28 },
                        new GradientStop { Color = Microsoft.UI.Colors.Lime, Offset = 0.5 },
                        new GradientStop { Color = Microsoft.UI.Colors.Cyan, Offset = 0.72 },
                        new GradientStop { Color = Microsoft.UI.Colors.Blue, Offset = 1 },
                    },
                },
            },
        };
        ToolTipService.SetToolTip(customButton, "自定义颜色");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(customButton, "底色 自定义颜色");
        customButton.Click += async (_, _) => await PickCustomTintAsync();
        TintSwatchPanel.Children.Add(customButton);
    }

    /// <summary>调色盘：选一个任意颜色作为底色，选完立刻生效。</summary>
    private async Task PickCustomTintAsync()
    {
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Box,
        };

        if (Appearance.CustomTintColors(_settings.Current.CustomTintColor, dark: true) is { } current)
        {
            picker.Color = current.Tint;
        }

        var dialog = new ContentDialog
        {
            Title = "自定义底色",
            Content = picker,
            PrimaryButtonText = "使用这个颜色",
            SecondaryButtonText = "恢复预设",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var c = picker.Color;
            _settings.Current.CustomTintColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _settings.Current.CustomTintColor = string.Empty;
        }
        else
        {
            return;
        }

        _settings.Save();
        BuildTintSwatches();
        ApplyWindowMaterial();
        _settingsChanged();
    }

    private static Color Solid(Windows.UI.Color color) =>
        Color.FromArgb(255, color.R, color.G, color.B);

    private void TintSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index })
        {
            return;
        }

        _settings.Current.WindowTintMode = Math.Clamp(index, 0, Appearance.TintCount - 1);
        // 选预设就意味着放弃自定义色。
        _settings.Current.CustomTintColor = string.Empty;
        _settings.Save();

        var accent = (Brush)Application.Current.Resources["SegmentSelectedBrush"];
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        for (var i = 0; i < _tintSwatches.Count; i++)
        {
            _tintSwatches[i].Background = i == index ? accent : transparent;
        }

        ApplyWindowMaterial();
        _settingsChanged();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _opacityDebounce;

    private void ScheduleOpacityCommit()
    {
        if (_opacityDebounce is null)
        {
            _opacityDebounce = DispatcherQueue.CreateTimer();
            _opacityDebounce.Interval = TimeSpan.FromMilliseconds(150);
            _opacityDebounce.IsRepeating = false;
            _opacityDebounce.Tick += (_, _) =>
            {
                ApplyWindowMaterial();
                _settings.Save();
                _settingsChanged();
            };
        }

        _opacityDebounce.Stop();
        _opacityDebounce.Start();
    }

    private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OpacityValue.Text = $"{Math.Round(e.NewValue * 100)}%";
        if (!_ready)
        {
            return;
        }

        _settings.Current.WindowOpacity = Math.Clamp(1.0 - e.NewValue, 0.1, 1.0);
        // 拖动期间只更新数值，停手 150ms 后再统一刷新亚克力材质与落盘，彻底避免 DWM 频繁重合成卡顿。
        ScheduleOpacityCommit();
    }

    private void StickyAcrylicToggle_Toggled(object sender, RoutedEventArgs e)
    {
        StickyOpacityRow.Visibility = StickyAcrylicToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (!_ready)
        {
            return;
        }

        _settings.Current.StickyAcrylicEnabled = StickyAcrylicToggle.IsOn;
        _settings.Save();
        _settingsChanged();
    }

    private void StickyOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        StickyOpacityValue.Text = $"{Math.Round(e.NewValue * 100)}%";
        if (!_ready)
        {
            return;
        }

        _settings.Current.StickyOpacity = Math.Clamp(1.0 - e.NewValue, 0.1, 1.0);
        ScheduleOpacityCommit();
    }

    private void BuildStickyColorSwatches()
    {
        StickyColorSwatchPanel.Children.Clear();
        var current = StickyColorTheme.Resolve(_settings.Current.StickyDefaultColor).Key;
        foreach (var theme in StickyColorTheme.All)
        {
            var isSelected = string.Equals(theme.Key, current, StringComparison.OrdinalIgnoreCase);
            var dot = new Grid { Width = 24, Height = 24 };
            dot.Children.Add(new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(theme.Swatch),
                BorderBrush = isSelected
                    ? new SolidColorBrush(Microsoft.UI.Colors.White)
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(60, 0, 0, 0)),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
            });

            if (isSelected)
            {
                dot.Children.Add(new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var btn = new Button
            {
                Width = 28,
                Height = 28,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                Content = dot,
            };
            ToolTipService.SetToolTip(btn, theme.Name);
            btn.Click += (_, _) =>
            {
                _settings.Current.StickyDefaultColor = theme.Key;
                _settings.Save();
                BuildStickyColorSwatches();
            };
            StickyColorSwatchPanel.Children.Add(btn);
        }
    }

    private void StickyFontSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        StickyFontSizeValue.Text = $"{Math.Round(e.NewValue):0} px";
        if (!_ready)
        {
            return;
        }

        _settings.Current.StickyDefaultFontSize = Math.Clamp(Math.Round(e.NewValue), 11.0, 20.0);
        _settings.Save();
    }

    private void StickyDefaultPinnedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.Current.StickyDefaultPinned = StickyDefaultPinnedToggle.IsOn;
        _settings.Save();
    }

    private void StickyRestoreToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.Current.StickyRestoreOnStartup = StickyRestoreToggle.IsOn;
        _settings.Save();
    }

    private void StickyAutoDockTopToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.Current.StickyAutoDockTop = StickyAutoDockTopToggle.IsOn;
        _settings.Save();
        _settingsChanged();
    }

    private void OpenStickyFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(AppPaths.Root, "notes");
        System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
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
        AutoHideDelayRow.Visibility = AutoHideToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

        if (!_ready)
        {
            return;
        }

        _settings.Current.AutoHide = AutoHideToggle.IsOn;
        _settings.Save();
        _settingsChanged();
    }

    private void AutoHideDelayBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready || double.IsNaN(args.NewValue))
        {
            return;
        }

        _settings.Current.AutoHideDelaySeconds =
            Math.Clamp((int)Math.Round(args.NewValue), 0, 600);
        _settings.Save();
        _settingsChanged();
    }

    private void SmartViewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        SmartViewOptions.Visibility = SmartViewToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_ready)
        {
            return;
        }

        _settings.Current.SmartViewEnabled = SmartViewToggle.IsOn;
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

    private void ColorTagToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ColorTagOptions.Visibility = ColorTagToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_ready)
        {
            return;
        }

        _settings.Current.ColorTagsEnabled = ColorTagToggle.IsOn;
        _settings.Save();
        _settingsChanged();
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

            if (!result.HasUpdate || result.Version is not { } version)
            {
                UpdateStatusText.Text = $"已是最新版本（{AppInfo.DisplayVersion}）";
                return;
            }

            _settings.Current.LastUpdateCheckDate = DateTime.Now.ToString("yyyy-MM-dd");
            _settings.Save();
            UpdateStatusText.Text = $"发现新版本 {version}。";
            await OfferUpdateAsync(version, result.Url);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 发现新版本后的出口。安装版静默覆盖安装；绿色版下载压缩包后原地解压重启。
    /// 两条路都是「点一下就不用管」，只有下载失败才退回打开下载页。
    /// </summary>
    private async Task OfferUpdateAsync(string version, string? pageUrl)
    {
        var dialog = new ContentDialog
        {
            Title = $"发现新版本 {version}",
            Content = new TextBlock
            {
                Text = "现在下载并安装吗？安装过程中应用会自动关闭，完成后自动重新打开。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "立即更新",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await DownloadAndInstallAsync(version, pageUrl);
    }

    private async Task DownloadAndInstallAsync(string version, string? pageUrl)
    {
        CheckUpdateButton.IsEnabled = false;

        // 安装版能静默覆盖安装；绿色版没有安装程序可覆盖，改下载压缩包原地解压。
        var installed = UpdateInstaller.IsInstalledCopy();
        var progress = new Progress<double>(value =>
            UpdateStatusText.Text = $"正在下载更新… {value * 100:0}%");

        var payload = installed
            ? await UpdateService.DownloadSetupAsync(version, progress)
            : await UpdateService.DownloadPortableAsync(version, progress);

        if (payload is null)
        {
            UpdateStatusText.Text = "下载失败，正在打开下载页手动更新。";
            CheckUpdateButton.IsEnabled = true;
            if (pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            {
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }

            return;
        }

        UpdateStatusText.Text = "下载完成，正在更新…应用即将重启。";
        _settings.Current.PendingUpdateVersion = string.Empty;
        _settings.Save();

        if (!installed)
        {
            // 绿色版：先拉起等着我们退出的更新脚本，再自行退出；脚本解压完会把应用重新打开。
            if (!UpdateInstaller.TryLaunchPortableUpdate(
                    payload,
                    AppContext.BaseDirectory,
                    Environment.ProcessPath ?? string.Empty))
            {
                UpdateStatusText.Text = "无法启动更新程序，请到 GitHub 下载页手动更新。";
                CheckUpdateButton.IsEnabled = true;
                return;
            }

            _settingsChangedForExit?.Invoke();
            return;
        }

        // 先把应用退干净，安装程序才能覆盖 exe；安装完由 /RESTARTAPPLICATIONS 自动拉起。
        _settingsChangedForExit?.Invoke();
        if (!UpdateInstaller.TryLaunchInstaller(payload))
        {
            UpdateStatusText.Text = "无法启动安装程序，请到 GitHub 下载页手动更新。";
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private static readonly int[] RefreshOptions = { 0, 5, 15, 30, 60 };

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _calendarFontDebounce;

    private void CalendarFontSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var size = Math.Round(Math.Clamp(e.NewValue, 8.0, 16.0), 1);
        if (CalendarFontSizeText is not null)
        {
            CalendarFontSizeText.Text = $"{size:0.#}";
        }

        if (!_calendarReady)
        {
            return;
        }

        _settings.Current.CalendarFontSize = size;

        // 防抖 160ms 再落盘与重绘日历，避免拖动滑块时每步都重建整页导致卡顿。
        _calendarFontDebounce ??= DispatcherQueue.CreateTimer();
        _calendarFontDebounce.Stop();
        _calendarFontDebounce.Interval = TimeSpan.FromMilliseconds(160);
        _calendarFontDebounce.IsRepeating = false;
        _calendarFontDebounce.Tick -= CalendarFontDebounce_Tick;
        _calendarFontDebounce.Tick += CalendarFontDebounce_Tick;
        _calendarFontDebounce.Start();
    }

    private void CalendarFontDebounce_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _settings.Save();
        _settingsChanged();
    }

    // ---- 地点搜索数据源 ------------------------------------------------------

    private bool _placeReady;

    private void LoadPlaceSettings()
    {
        _placeReady = false;
        CalendarOpenOnStartupToggle.IsOn = _settings.Current.CalendarOpenOnStartup;
        CalendarScrollMonthHintBox.SelectedIndex = Math.Clamp(_settings.Current.CalendarScrollMonthHintMode, 0, 2);
        PlaceSourceBox.SelectedIndex = Math.Clamp(_settings.Current.PlaceSearchProvider, 0, 3);
        PlaceAmapKeyBox.Text = _settings.Current.PlaceSearchAmapKey;
        UpdatePlaceSourceVisibility();
        PlaceTestResult.Text = string.Empty;
        _placeReady = true;
    }

    private void CalendarOpenOnStartup_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_placeReady)
        {
            return;
        }

        _settings.Current.CalendarOpenOnStartup = CalendarOpenOnStartupToggle.IsOn;
        _settings.Save();
    }

    private void CalendarScrollMonthHint_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_placeReady)
        {
            return;
        }

        _settings.Current.CalendarScrollMonthHintMode = Math.Clamp(CalendarScrollMonthHintBox.SelectedIndex, 0, 2);
        _settings.Save();
    }

    private void UpdatePlaceSourceVisibility()
    {
        PlaceAmapPanel.Visibility = PlaceSourceBox.SelectedIndex == (int)PlaceSource.Amap
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyPlaceSourceSettings()
    {
        _settings.Current.PlaceSearchProvider = Math.Clamp(PlaceSourceBox.SelectedIndex, 0, 3);
        _settings.Current.PlaceSearchAmapKey = PlaceAmapKeyBox.Text.Trim();
        _settings.Save();
        PlaceSearchService.Configure(_settings.Current.PlaceSearchProvider, _settings.Current.PlaceSearchAmapKey);
    }

    private void PlaceSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePlaceSourceVisibility();
        if (!_placeReady)
        {
            return;
        }

        ApplyPlaceSourceSettings();
        PlaceTestResult.Text = string.Empty;
    }

    private void PlaceAmapKeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_placeReady)
        {
            return;
        }

        ApplyPlaceSourceSettings();
    }

    private async void PlaceTest_Click(object sender, RoutedEventArgs e)
    {
        if (!_placeReady)
        {
            return;
        }

        ApplyPlaceSourceSettings();
        PlaceTestButton.IsEnabled = false;
        var original = PlaceTestButton.Content;
        PlaceTestButton.Content = "测试中…";
        PlaceTestResult.Text = "正在连接…";
        try
        {
            var result = await PlaceSearchService.TestAsync(
                Math.Clamp(PlaceSourceBox.SelectedIndex, 0, 3), PlaceAmapKeyBox.Text);
            PlaceTestResult.Text = result.Success ? $"✓ {result.Message}" : $"⚠ {result.Message}";
        }
        finally
        {
            PlaceTestButton.Content = original;
            PlaceTestButton.IsEnabled = true;
        }
    }

    private async void OpenAmapConsole_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://console.amap.com/dev/key/app"));
        }
        catch
        {
            // 打不开浏览器就算了，用户可以自己搜「高德开放平台」
        }
    }

    private void LoadCalendarSettings()
    {
        if (_calendarSync is null)
        {
            return;
        }

        _calendarReady = false;
        var fontSize = Math.Clamp(_settings.Current.CalendarFontSize, 8.0, 16.0);
        CalendarFontSizeSlider.Value = fontSize;
        CalendarFontSizeText.Text = $"{fontSize:0.#}";
        CalendarSyncToggle.IsOn = _settings.Current.CalendarSyncEnabled;
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

    /// <summary>
    /// 同步总开关。关掉就切回纯离线：隐藏所有配置项，也不再发起任何网络请求；
    /// 本地已有的日历数据照常可以查看和编辑。
    /// </summary>
    private async void CalendarSyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_calendarReady || _calendarSync is null)
        {
            return;
        }

        _settings.Current.CalendarSyncEnabled = CalendarSyncToggle.IsOn;
        _settings.Save();
        UpdateCalendarStatus();
        _settingsChanged();

        // 刚打开同步、本地又有待裁决的日程：先同步一次拿到云端全貌，再问用户怎么合并。
        if (CalendarSyncToggle.IsOn)
        {
            await RunFirstMergeIfNeededAsync();
        }
    }

    /// <summary>
    /// 首次合并：先把云端同步下来，再让用户逐个本地日历决定「保留 / 合入云端 / 丢弃」。
    /// 合并口径固定为云端优先，本地不会覆盖云端。
    /// </summary>
    private async Task RunFirstMergeIfNeededAsync()
    {
        if (_calendarSync is null || !_calendarSync.IsConfigured)
        {
            return;
        }

        var merge = new CalendarMergeService(_calendarSync.Repository, _calendarSync);
        if (!merge.HasLocalDataToReconcile())
        {
            return;
        }

        var choices = merge.BuildChoices();
        var cloudSources = _calendarSync.Repository.GetSources()
            .Where(source => !CalendarRepository.IsLocalHref(source.Href))
            .ToList();

        // 先同步一次，本地才有云端日程可用来判断「同一条谁赢」。
        var now = DateTime.Today;
        var (from, to) = CalendarSyncService.DefaultSyncWindow(DateTime.Today);
        var sync = await _calendarSync.SyncAsync(from, to);

        var dialog = new CalendarMergeDialog(_settings, merge, choices, cloudSources);
        dialog.ShowOver(_appWindow);
        dialog.Activate();
        var merged = await dialog.Result;

        RebuildCalendarSources();
        UpdateSyncStatusText();
        _settingsChanged();

        if (merged && dialog.Outcome is { } outcome)
        {
            CalendarStatusText.Text =
                $"合并完成：上传 {outcome.UploadedCount} 条，采用云端版本 {outcome.CloudWonCount} 条，" +
                $"保留本地 {outcome.KeptLocalCount} 条，丢弃 {outcome.DiscardedCount} 条。" +
                (outcome.FailedCount > 0 ? $" {outcome.FailedCount} 条上传失败，本地日历未删除。" : string.Empty);
        }
        else if (!sync.Succeeded)
        {
            CalendarStatusText.Text = "已开启同步，但拉取云端失败：" + sync.Message;
        }
    }

    private void UpdateCalendarStatus()
    {
        if (_calendarSync is null)
        {
            return;
        }

        var enabled = _settings.Current.CalendarSyncEnabled;
        CalendarOnlinePanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        var connected = _calendarSync.IsConfigured;

        // 状态卡：圆点 + 一句话标题说清连没连上，细节放第二行。
        CalendarHeadlineText.Text = connected ? $"已连接 {AccountMask.Apply(_calendarSync.AppleId)}" : "iCloud 日历尚未连接";
        CalendarStatusText.Text = connected
            ? "日程会与 iCloud 双向同步。待办只存在本机。"
            : "按下面三步操作，大约两分钟。";
        CalendarStatusDot.Fill = (Brush)Application.Current.Resources[
            connected ? "AccentBrush" : "TextMutedBrush"];

        // 教程和登录表单只在没连接时出现：连上以后收起来，换账号走「注销」——点了表单再回来。
        CalendarSetupPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        CalendarFormCard.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        CalendarClearButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
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
                Text = "还没有日历。点右上角「新建」建一个本地日历；开启同步后也会列出 iCloud 里的日历。",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
            return;
        }

        foreach (var source in sources)
        {
            var local = CalendarRepository.IsLocalHref(source.Href);
            var check = new CheckBox
            {
                Content = local ? $"{source.DisplayName}（本地）" : source.DisplayName,
                IsChecked = source.IsVisible,
                Tag = source.Href,
                MinWidth = 0,
            };
            check.Click += (_, _) =>
            {
                // 这一处改动由本页自己维护勾选状态，不必再整体重建一次（会打断点击）。
                _rebuildingSources = true;
                try
                {
                    _calendarSync.Repository.SetSourceVisible(source.Href, check.IsChecked == true);
                }
                finally
                {
                    _rebuildingSources = false;
                }
            };

            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(check);

            // 本地日历可改名；云端日历名称由服务器决定，不在这里改。
            var rename = new Button
            {
                Style = (Style)Application.Current.Resources["LinkButtonStyle"],
                Content = new FontIcon { Glyph = "", FontSize = 10 },
                Width = 28,
                Height = 28,
                MinWidth = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = local ? Visibility.Visible : Visibility.Collapsed,
                Tag = source,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rename, "重命名日历");
            rename.Click += (_, _) => RenameLocalSource((CalendarSource)rename.Tag);
            Grid.SetColumn(rename, 1);
            row.Children.Add(rename);

            var delete = new Button
            {
                Style = (Style)Application.Current.Resources["LinkButtonStyle"],
                Content = new FontIcon { Glyph = "", FontSize = 10 },
                Width = 28,
                Height = 28,
                MinWidth = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = source,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, "删除日历");
            delete.Click += (_, _) => _ = DeleteSourceAsync((CalendarSource)delete.Tag);
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);

            CalendarSourceList.Children.Add(row);
        }
    }

    private async void AddLocalCalendar_Click(object sender, RoutedEventArgs e)
    {
        if (_calendarSync is null)
        {
            return;
        }

        var box = new TextBox
        {
            PlaceholderText = "日历名称，例如 工作",
            MinWidth = 240,
        };
        var dialog = new ContentDialog
        {
            Title = "新建本地日历",
            Content = box,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = box.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        // 本地日历给一个固定色环里的颜色，按已有数量轮换，避免都一个颜色。
        var colors = new[] { "#4D8FE3", "#4CAF6D", "#F08A3C", "#E34D4D", "#9B6BE8", "#2DB7A3" };
        var color = colors[_calendarSync.Repository.GetSources().Count % colors.Length];
        _calendarSync.Repository.AddLocalSource(name, color);

        RebuildCalendarSources();
        _settingsChanged();
    }

    private async void RenameLocalSource(CalendarSource source)
    {
        if (_calendarSync is null || !CalendarRepository.IsLocalHref(source.Href))
        {
            return;
        }

        var box = new TextBox { Text = source.DisplayName, MinWidth = 240 };
        var dialog = new ContentDialog
        {
            Title = "重命名日历",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = box.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        _calendarSync.Repository.RenameSource(source.Href, name);
        RebuildCalendarSources();
        _settingsChanged();
    }

    /// <summary>
    /// 删日历：本地日历连数据一起删；云端日历只从本机移除显示，
    /// 绝不在服务器上删集合（避免一个误点把整本 iCloud 日历清空）。
    /// </summary>
    private async Task DeleteSourceAsync(CalendarSource source)
    {
        if (_calendarSync is null)
        {
            return;
        }

        var local = CalendarRepository.IsLocalHref(source.Href);
        var dialog = new ContentDialog
        {
            Title = local ? "删除本地日历" : "从本机移除日历",
            Content = local
                ? $"「{source.DisplayName}」以及里面的日程都会从本机删除，无法恢复。"
                : $"「{source.DisplayName}」只会从本机移除，iCloud 上的日历和日程不受影响。",
            PrimaryButtonText = local ? "删除" : "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _calendarSync.Repository.DeleteSource(source.Href);
        RebuildCalendarSources();
        _settingsChanged();
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
            var (from, to) = CalendarSyncService.DefaultSyncWindow(DateTime.Today);
            var sync = await _calendarSync.SyncAsync(from, to);
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

        // 注销后登录表单重新出现，直接带到眼前，省得还要自己往下翻。
        // 布局要等这一帧结束才更新，所以延到下一轮再滚。
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => CalendarFormCard.StartBringIntoView());
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
            var (from, to) = CalendarSyncService.DefaultSyncWindow(DateTime.Today);
            var result = await _calendarSync.SyncAsync(from, to);
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