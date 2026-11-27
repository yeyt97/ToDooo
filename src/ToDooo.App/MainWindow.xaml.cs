using System.IO;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ToDooo.Interop;
using ToDooo.Helpers;
using ToDooo.Models;
using ToDooo.Services;
using ToDooo.ViewModels;
using ToDooo.Views;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT;

namespace ToDooo;

public sealed partial class MainWindow : Window
{
    private readonly TodoRepository _repository;
    private readonly SettingsService _settings;
    private readonly Notifier _notifier;
    private readonly CalendarSyncService _calendarSync;
    private readonly ReminderService _reminder;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly List<Button> _tagButtons = new();
    private readonly List<Button> _smartViewButtons = new();
    private readonly List<FontIcon> _tagChecks = new();

    private NativeMethods.SUBCLASSPROC? _subclassProc;
    private DesktopAcrylicController? _acrylic;
    private Microsoft.UI.Composition.SystemBackdrops.SystemBackdropConfiguration? _backdropConfig;
    private Views.SettingsWindow? _settingsWindow;
    private Views.CalendarHostWindow? _calendarHostWindow;
    private TrayIcon? _tray;
    private bool _calendarExpanded;
    private bool _calendarAnimating;
    private uint _taskbarCreatedMessage;
    private bool _started;
    private bool _positioned;
    private bool _exiting;
    private bool _dragging;
    private bool _suppressAddCommit;
    private TodoEditorDialog? _editor;
    private SubtaskEditorDialog? _subtaskEditor;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;
    private readonly List<(IntPtr Hwnd, int OffsetX, int OffsetY)> _attachedFollowers = new();

    public MainWindow(TodoRepository repository, SettingsService settings, Notifier notifier,
        CalendarSyncService calendarSync)
    {
        _repository = repository;
        _settings = settings;
        _notifier = notifier;
        _calendarSync = calendarSync;
        ViewModel = new MainViewModel(repository);

        InitializeComponent();
        ThemePalette.ApplyTextControlPalette(Root.Resources);
        ViewModel.Load();
        if (Enum.IsDefined(typeof(TodoFilter), _settings.Current.DefaultTab))
        {
            ViewModel.SetFilter((TodoFilter)_settings.Current.DefaultTab);
        }

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        UpdateSubtasksToggleVisual();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.AreAllVisibleSubtasksExpanded)
                or nameof(MainViewModel.HasVisibleSubtasks))
            {
                UpdateSubtasksToggleVisual();
            }
        };
        // 颜色标签开关要在建卡片之前定好，否则首屏色条会按默认值画。
        Bind.ColorTagsVisible = _settings.Current.ColorTagsEnabled;
        BuildTagDots();
        BuildSmartViewChips();
        BuildDoneRangeChips();
        UpdateFilterVisuals();
        UpdateTagVisuals();

        // 列表/滚动区会先把按下事件标记为已处理，贴边缩放要能看见这些被吞掉的事件。
        Root.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(Root_PointerPressed),
            handledEventsToo: true);
        Root.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(Root_PointerMoved),
            handledEventsToo: true);
        Root.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(Root_PointerReleased),
            handledEventsToo: true);

        // XAML 的 LostFocus 在窗口整体失活时不会触发，这里补一次全局失焦提交。
        Activated += MainWindow_Activated;

        // 输入框的右键菜单是独立窗口，弹出时主窗口会失活；挂上开合事件才能区分「菜单」和「真离开」。
        AttachComposerMenu(AddTextBox);

        HookNetworkRecovery();

        _reminder = new ReminderService(repository, notifier, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread())
        {
            Source = () => ViewModel.AllItems,
            DayChanged = () => _dispatcher.TryEnqueue(() =>
            {
                ViewModel.RefreshForDayChange();
                UpdateFilterVisuals();
            }),
        };

        // 系统通知是后台线程回调的，这里转回 UI 线程处理。
        notifier.SnoozeRequested = id => _dispatcher.TryEnqueue(() => SnoozeReminder(id));
        notifier.CompleteRequested = id => _dispatcher.TryEnqueue(() => CompleteFromNotification(id));
        notifier.CompleteSubtaskRequested = (todoId, subId) =>
            _dispatcher.TryEnqueue(() => CompleteSubtaskFromNotification(todoId, subId));

        _dispatcher.TryEnqueue(() =>
        {
            RestoreVisibleStickyNotes();
            if (_settings.Current.CalendarOpen || _settings.Current.CalendarOpenOnStartup)
            {
                SetCalendarExpanded(true);
            }
        });
    }

    public MainViewModel ViewModel { get; }

    public void Startup()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        InstallSubclass();
        PositionWindow();
        ApplyCornerMask();
        WindowCloak.ShowCloaked(_hwnd, Root, _dispatcher, Activate);

        CreateTray();
        ApplyGlobalHotkey();
        StartupService.EnsureMatchesCurrentProcess();
        _reminder.LeadMinutes = _settings.Current.ReminderLeadMinutes;
        _reminder.Start();
        ScheduleUpdateCheck();
        ScheduleCalendarRefresh();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _calendarRefreshTimer;

    /// <summary>
    /// 按设置里的间隔后台刷新日历。间隔为 0 时只手动刷新。
    /// 定时器常驻，改设置只要重新调度即可，不必重启。
    /// </summary>
    private void ScheduleCalendarRefresh()
    {
        _calendarRefreshTimer ??= _dispatcher.CreateTimer();
        _calendarRefreshTimer.Stop();

        var configured = _settings.Current.CalendarRefreshMinutes;
        if (configured <= 0 || !_calendarSync.IsConfigured)
        {
            return;
        }

        // 连续失败时指数退避，避免断网期间空转；恢复后自动回到用户设定的间隔。
        var minutes = _calendarSync.SuggestedRefreshMinutes(configured);
        _calendarRefreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, minutes));
        _calendarRefreshTimer.Tick -= CalendarRefreshTimer_Tick;
        _calendarRefreshTimer.Tick += CalendarRefreshTimer_Tick;
        _calendarRefreshTimer.Start();

        // 启动时先拉一次，别让用户等到第一个间隔才有数据。
        _ = _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                var today = DateTime.Today;
                var (from, to) = CalendarSyncService.DefaultSyncWindow(DateTime.Today);
            await _calendarSync.SyncAsync(from, to);
            }
            catch
            {
                // 首轮失败静默，等定时器重试
            }
        });
    }

    private async void CalendarRefreshTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        if (!_calendarSync.IsConfigured)
        {
            return;
        }

        // 后台定时拉取：不依赖日历窗口存在，窗口没开也照常同步到本地缓存。
        var today = DateTime.Today;
        try
        {
            var (from, to) = CalendarSyncService.DefaultSyncWindow(DateTime.Today);
            await _calendarSync.SyncAsync(from, to);
        }
        catch
        {
            // 后台同步失败不影响使用，下一次 tick 再试。
        }

        // 每次结束后按最新结果重排下次间隔（成功回到设定值，失败退避）。
        ScheduleCalendarRefresh();
    }

    public void ShowFromTray()
    {
        // 远程桌面连接、拔掉外接屏之后，窗口可能留在已经消失的那块屏的坐标上：
        // 显示是“显示”了，但人看不见，点托盘也像没反应。唤出前先把窗口拉回可见区域。
        EnsureOnVisibleDisplay();

        if (!NativeMethods.IsWindowVisible(_hwnd))
        {
            WindowCloak.ShowCloaked(_hwnd, Root, _dispatcher, () =>
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
                ExpandDock();
                Activate();
                NativeMethods.SetForegroundWindow(_hwnd);
            });
            return;
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        ExpandDock();
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
        KeepCalendarBehind();
    }

    /// <summary>
    /// 窗口若已经落在所有显示器之外（或几乎没有可见部分），挪回主屏并居中。
    /// 判断依据是工作区交集面积：远程连接断开、副屏拔掉都属于这种情况。
    /// </summary>
    private void EnsureOnVisibleDisplay()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        if (IsRectVisibleOnAnyDisplay(rect))
        {
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var x = area.X + Math.Max(0, (area.Width - width) / 2);
        var y = area.Y + Math.Max(0, (area.Height - height) / 2);

        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // 位置变了，保存的坐标也要跟上，避免下次启动又回到屏幕外。
        _settings.Current.WindowX = x;
        _settings.Current.WindowY = y;
        _settings.Save();
    }

    /// <summary>
    /// 窗口和虚拟桌面至少要有 120×120 的重叠，才算「看得见」。
    /// 这里用 GetSystemMetrics 而不是 DisplayArea.FindAll：后者在非打包环境下枚举会抛
    /// InvalidCastException（见 CalendarHostWindow.IsPointOnAnyDisplay 的说明），
    /// 快捷键唤出会直接闪退。
    /// </summary>
    private static bool IsRectVisibleOnAnyDisplay(NativeMethods.RECT rect)
    {
        const int minVisible = 120;
        var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var right = left + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var bottom = top + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        var overlapX = Math.Min(rect.Right, right) - Math.Max(rect.Left, left);
        var overlapY = Math.Min(rect.Bottom, bottom) - Math.Max(rect.Top, top);
        return overlapX >= minVisible && overlapY >= minVisible;
    }

    /// <summary>网络恢复时补一次同步：离线期间排队的增删改不必等下一个定时点。</summary>
    private void HookNetworkRecovery()
    {
        try
        {
            Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += _ =>
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                if (profile is null || profile.GetNetworkConnectivityLevel() !=
                    Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess)
                {
                    return;
                }

                _dispatcher.TryEnqueue(async () =>
                {
                    if (!_calendarSync.IsConfigured)
                    {
                        return;
                    }

                    var today = DateTime.Today;
                    var (from, to) = CalendarSyncService.DefaultSyncWindow(DateTime.Today);
                    await _calendarSync.SyncAsync(from, to);
                });
            };
        }
        catch
        {
            // 拿不到网络状态不影响其它功能
        }
    }

    /// <summary>从托盘唤出待办时，日历即使开着也不应跑到前面：只把它压回待办下方。</summary>
    private void KeepCalendarBehind()
    {
        if (_calendarHostWindow is null ||
            !NativeMethods.IsWindowVisible(_calendarHostWindow.Hwnd))
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _calendarHostWindow.Hwnd, _hwnd, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public void ShowBalloon(string title, string text) => _tray?.ShowBalloon(title, text);

    public void SaveState() => SavePosition();

    private void ConfigureWindow()
    {
        _appWindow.Title = "ToDooo";
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            // 有系统尺寸边框：窗口外轮廓的圆角、边缘缩放都交给系统处理，
            // 无边框的 popup 窗口在 Win11 上不裁圆角、还会露出系统默认的白边。
            // XAML 侧圆角已对齐到 8，和系统圆角重合。
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.PreferredMinimumWidth = 320;
            presenter.PreferredMinimumHeight = 360;
        }

        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode);
        ThemePalette.Apply(Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode), _settings.Current.WindowOpacity,
            (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, Appearance.TintCount - 1), _settings.Current.CustomTintColor);
        ApplyAcrylicMaterial();

        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        // 不再手动剥掉 WS_THICKFRAME：那会让上面的 IsResizable 失效、边缘拖不动。
        // 最小化/最大化按钮已由 OverlappedPresenter 关掉，这里只处理扩展样式。

        ApplyCornerMask();
        ApplyDwmTheme(Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode));

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon))
        {
            _appWindow.SetIcon(icon);
        }
    }

    private void ApplyAcrylicMaterial()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            return;
        }

        var dark = Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        _backdropConfig = new Microsoft.UI.Composition.SystemBackdrops.SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = dark
                ? Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Dark
                : Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Light,
        };

        _acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        WindowAcrylic.ApplyTint(_acrylic, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark,
            (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, Appearance.TintCount - 1), _settings.Current.CustomTintColor);
        _acrylic.AddSystemBackdropTarget(
            ((object)this).As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
    }

    /// <summary>设置页改动后重刷外观：主题、亚克力浓淡、代码里写死的颜色。</summary>
    public void ApplyAppearance()
    {
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode);
        var dark = Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        ThemePalette.Apply(dark, _settings.Current.WindowOpacity,
            (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, Appearance.TintCount - 1), _settings.Current.CustomTintColor);
        ApplyDwmTheme(dark);

        if (_acrylic is not null)
        {
            WindowAcrylic.ApplyTint(_acrylic, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark,
                (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, Appearance.TintCount - 1), _settings.Current.CustomTintColor);
            if (_backdropConfig is not null)
            {
                _backdropConfig.Theme = dark
                    ? Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Dark
                    : Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Light;
            }
        }

        UpdateFilterVisuals();
        UpdateTagVisuals();
        _settingsWindow?.RefreshTheme();
        _calendarHostWindow?.RefreshTheme();
    }

    /// <summary>当前底色方案，转给弹窗磨砂等处用。</summary>
    private Appearance.TintMode CurrentTintMode() =>
        (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, Appearance.TintCount - 1);

    private void ApplyDwmTheme(bool dark)
    {
        var value = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));

        // 系统原生菜单（托盘的兜底菜单等）也跟着深/浅色走。
        try
        {
            NativeMethods.SetPreferredAppMode(dark ? 2 : 3);
            NativeMethods.FlushMenuThemes();
        }
        catch
        {
        }
    }

    /// <summary>按当前窗口实际主题取主题字典里的画刷，供代码创建/设置的视觉使用。</summary>
    private static Brush PaletteBrush(string key) => (Brush)Application.Current.Resources[key];

    private void InstallSubclass()
    {
        _subclassProc = WndProc;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, 1, IntPtr.Zero);
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
    }

    // 首次启动的默认尺寸（逻辑像素）；之后一律以保存的物理像素为准。
    private const int DefaultLogicalWidth = 440;
    private const int DefaultLogicalHeight = 716;

    private void PositionWindow()
    {
        // 尺寸按物理像素存；从没保存过（0）时用默认逻辑尺寸乘当前 DPI 换算一次。
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var width = _settings.Current.WindowWidth > 0
            ? _settings.Current.WindowWidth
            : (int)Math.Round(DefaultLogicalWidth * scale);
        var height = _settings.Current.WindowHeight > 0
            ? _settings.Current.WindowHeight
            : (int)Math.Round(DefaultLogicalHeight * scale);

        var fallback = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var x = _settings.Current.WindowX ?? fallback.X + fallback.Width - width - (int)(24 * scale);
        var y = _settings.Current.WindowY ?? fallback.Y + fallback.Height - height - (int)(28 * scale);

        // 按保存位置所在的显示器夹取：放到副屏的小组件重启后不会被拉回主屏；
        // 若副屏已拔掉或分辨率/DPI 变化，自动夹取到最近可用显示器的可见工作区内。
        var area = DisplayArea.GetFromPoint(new PointInt32(x + width / 2, y + height / 2), DisplayAreaFallback.Nearest).WorkArea;
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        x = Math.Clamp(x, area.X, area.X + Math.Max(0, area.Width - width));
        y = Math.Clamp(y, area.Y, area.Y + Math.Max(0, area.Height - height));

        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // 尺寸是物理像素，落到另一块不同缩放的屏也保持像素大小，无需再按 DPI 重算。

        _positioned = true;
    }

    private void ApplyCornerMask()
    {
        NativeMethods.SetWindowRgn(_hwnd, IntPtr.Zero, true);
        var round = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        // 关掉系统绘制的窗口边框：Win11 深色主题下它是一条浅色线，
        // 叠在 XAML 卡片描边外面看着就是「一圈白边」。
        var noBorder = NativeMethods.DWMWA_COLOR_NONE;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }

    private void SavePosition()
    {
        if (!_started)
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        _settings.Current.WindowY = rect.Top;
        _settings.Current.WindowX = rect.Left;
        // 尺寸按物理像素存：改系统缩放后窗口的像素大小不变，
        // 若换算成逻辑像素再按新 DPI 还原，重启就会跟着缩放一起变大。
        _settings.Current.WindowWidth = Math.Max(1, rect.Right - rect.Left);
        _settings.Current.WindowHeight = Math.Max(1, rect.Bottom - rect.Top);
        _settings.Save();
    }

    /// <summary>DPI 变化（跨屏、改缩放）已在消息里处理完尺寸，这里把依赖物理像素的状态同步过去。</summary>
    private void OnDpiChanged()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        if (_dockEdge != EdgeDock.None)
        {
            // 收边窗口是屏幕外的整宽薄条：按新 DPI 的尺寸重算展开矩形，再贴回原来的边。
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            var area = DisplayArea.GetFromPoint(
                new PointInt32(rect.Left + width / 2, rect.Top + Math.Min(20, height / 2)),
                DisplayAreaFallback.Nearest).WorkArea;
            var left = _dockEdge switch
            {
                EdgeDock.Left => area.X,
                EdgeDock.Right => area.X + area.Width - width,
                _ => rect.Left,
            };
            var top = _dockEdge == EdgeDock.Top ? area.Y : rect.Top;
            _dockRect = ClampRectToWorkArea(new NativeMethods.RECT
            {
                Left = left,
                Top = top,
                Right = left + width,
                Bottom = top + height,
            });

            if (_dockRetracted)
            {
                SlideTo(GetRetractPoint(), animate: false);
            }
            else
            {
                NativeMethods.SetWindowPos(
                    _hwnd, NativeMethods.HWND_TOPMOST,
                    _dockRect.Left, _dockRect.Top,
                    _dockRect.Right - _dockRect.Left, _dockRect.Bottom - _dockRect.Top,
                    NativeMethods.SWP_NOACTIVATE);
            }
        }
        else if (_dragging && NativeMethods.GetCursorPos(out var dragCursor))
        {
            // 拖过屏时物理尺寸变过，重设拖动基准，否则下一步会按旧尺寸跳一下。
            _dragStartRect = rect;
            _dragStartCursor = dragCursor;
        }
        else if (_resizeHit != 0 && NativeMethods.GetCursorPos(out var resizeCursor))
        {
            _resizeStartRect = rect;
            _resizeStartCursor = resizeCursor;
        }

        if (!_dragging && _resizeHit == 0)
        {
            SavePosition();
        }
    }

    private void CalendarButton_Click(object sender, RoutedEventArgs e) =>
        SetCalendarExpanded(!_calendarExpanded);

    private void SetCalendarExpanded(bool expanded)
    {
        if (_calendarAnimating || _calendarExpanded == expanded || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (expanded)
        {
            if (_dockEdge != EdgeDock.None)
            {
                ExpandDock();
            }

            if (_calendarHostWindow is null)
            {
                _calendarHostWindow = new Views.CalendarHostWindow(
                    ViewModel,
                    _settings,
                    _calendarSync,
                    _hwnd,
                    item => _ = EditItemAsync(item, _calendarHostWindow?.WindowInfo),
                    (item, date) => ViewModel.Reschedule(item, date),
                    date => _ = AddTaskAtAsync(date, _calendarHostWindow?.WindowInfo),
                    () => SetCalendarExpanded(false));
            }

            _calendarAnimating = true;
            _calendarExpanded = true;
            _settings.Current.CalendarOpen = true;
            _settings.Save();
            UpdateCalendarButtonVisual();

            _calendarHostWindow.ShowNextTo(_appWindow, () =>
            {
                _calendarAnimating = false;
                SyncDockedRects();
            });
            return;
        }

        _calendarAnimating = true;
        _calendarExpanded = false;
        _settings.Current.CalendarOpen = false;
        _settings.Save();
        UpdateCalendarButtonVisual();

        if (_calendarHostWindow is null)
        {
            _calendarAnimating = false;
            return;
        }

        _calendarHostWindow.HideAnimated(() =>
        {
            _calendarAnimating = false;
        });
    }

    private void UpdateCalendarButtonVisual()
    {
        CalendarButton.Foreground = (Brush)Application.Current.Resources[
            _calendarExpanded ? "AccentBrush" : "TextSecondaryBrush"];
        AutomationProperties.SetName(CalendarButton, _calendarExpanded ? "收起日历" : "日历");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new Views.SettingsWindow(
            _settings,
            OnSettingsChanged,
            ExportDataAsync,
            ImportDataAsync,
            _calendarSync,
            ExitApp);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.ShowOver(_appWindow);
        TopmostDialogIfDocked(_settingsWindow);
    }

    private const int GlobalHotkeyId = 0x544F;
    private const int QuickAddHotkeyId = 0x5450;

    private void ApplyGlobalHotkey()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_hwnd, GlobalHotkeyId);
        var (mods, vk) = _settings.Current.GlobalHotkeyMode switch
        {
            1 => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x54u),
            2 => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, 0x54u),
            3 => (NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x51u),
            _ => (0u, 0u),
        };

        if (vk != 0)
        {
            NativeMethods.RegisterHotKey(_hwnd, GlobalHotkeyId, mods, vk);
        }

        NativeMethods.UnregisterHotKey(_hwnd, QuickAddHotkeyId);
        var (quickMods, quickVk) = _settings.Current.QuickAddHotkeyMode switch
        {
            1 => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x4Eu),
            2 => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, 0x4Eu),
            3 => (NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x4Eu),
            _ => (0u, 0u),
        };

        if (quickVk != 0)
        {
            NativeMethods.RegisterHotKey(_hwnd, QuickAddHotkeyId, quickMods, quickVk);
        }
    }

    private void QuickAddFromHotkey()
    {
        ShowFromTray();
        OpenAddBar();
    }

    private void ToggleFromHotkey()
    {
        if (_dockEdge != EdgeDock.None)
        {
            if (_dockRetracted)
            {
                ShowFromTray();
            }
            else
            {
                RetractDock();
            }

            return;
        }

        ShowFromTray();
    }

    private void OnSettingsChanged()
    {
        ApplyAppearance();
        ApplyGlobalHotkey();
        RefreshTagButtonLabels();

        // 颜色标签开关会影响卡片色条（x:Bind 读静态属性），需要让卡片重建一次才生效。
        Bind.ColorTagsVisible = _settings.Current.ColorTagsEnabled;
        UpdateTagVisuals();
        ViewModel.RefreshCardVisuals();

        _reminder.LeadMinutes = _settings.Current.ReminderLeadMinutes;
        BuildSmartViewChips();
        ScheduleCalendarRefresh();
        _calendarHostWindow?.Calendar.Refresh();
        RefreshStickyThemes();

        // 关掉自动隐藏时把可能已经变淡的窗口拉回来。
        if (!_settings.Current.AutoHide)
        {
            AnimateRootOpacity(1);
        }
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        WindowCloak.Cancel(_hwnd, reveal: false);
        _calendarAnimating = false;
        _calendarHostWindow?.ForceClose();
        CloseAllStickyWindows();
        NativeMethods.UnregisterHotKey(_hwnd, GlobalHotkeyId);
        NativeMethods.UnregisterHotKey(_hwnd, QuickAddHotkeyId);
        ExpandDock();
        SavePosition();
        _tray?.Dispose();
        _reminder.Dispose();
        WindowAcrylic.DetachAndDispose(ref _acrylic, ref _backdropConfig);
        Application.Current.Exit();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        switch (msg)
        {
            case NativeMethods.WM_HOTKEY:
                if (wParam.ToInt32() == GlobalHotkeyId)
                {
                    _dispatcher.TryEnqueue(ToggleFromHotkey);
                    return IntPtr.Zero;
                }

                if (wParam.ToInt32() == QuickAddHotkeyId)
                {
                    _dispatcher.TryEnqueue(QuickAddFromHotkey);
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WM_TRAY:
                _tray?.HandleMessage(lParam);
                return IntPtr.Zero;

            case NativeMethods.WM_CLOSE:
                if (_exiting)
                {
                    break; // 退出流程里放行，别再拦一次
                }

                ExitApp();
                return IntPtr.Zero;

            case NativeMethods.WM_SYSCOMMAND:
                if ((wParam.ToInt64() & 0xFFF0) == NativeMethods.SC_MINIMIZE)
                {
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WM_ACTIVATE:
                if ((wParam.ToInt64() & 0xFFFF) == 0)
                {
                    // WA_INACTIVE = 0：真正的窗口级失焦（点了别的程序、桌面）。
                    // XAML 的 LostFocus 在这种场景下不触发，只能在这里补一次草稿提交。
                    _dispatcher.TryEnqueue(() => CommitAddDraft());
                }

                break;

            case NativeMethods.WM_SIZE:
                if (wParam.ToInt64() == 1) // SIZE_MINIMIZED：ShowWindow 等绕过 WM_SYSCOMMAND 的路径
                {
                    _dispatcher.TryEnqueue(() => NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_RESTORE));
                }

                break;

            case NativeMethods.WM_NCCALCSIZE:
                // 保留系统缩放边框（可以拖边 resize），但让客户区铺满整个窗口，
                // 否则内容会内缩 3px，和 DWM 圆角叠出「台阶」。
                if (wParam != IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WM_NCHITTEST:
                // 边框区域的命中不让系统接管：无边框窗口进系统缩放循环会卡住，
                // 统一当客户区处理，边缘缩放交给 XAML 的 Root_PointerPressed。
                return new IntPtr(NativeMethods.HTCLIENT);

            case NativeMethods.WM_WINDOWPOSCHANGING:
            {
                var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                var changed = false;

                // 最小尺寸：避免缩到内容被压坏，与 XAML Root 的 MinWidth/MinHeight 保持一致。
                if ((pos.flags & NativeMethods.SWP_NOSIZE) == 0 && pos.cx > 0 && pos.cy > 0)
                {
                    var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
                    var minWidth = (int)Math.Round(360 * scale);
                    var minHeight = (int)Math.Round(360 * scale);
                    if (pos.cx < minWidth)
                    {
                        pos.cx = minWidth;
                        changed = true;
                    }

                    if (pos.cy < minHeight)
                    {
                        pos.cy = minHeight;
                        changed = true;
                    }
                }

                if (changed)
                {
                    System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
                }

                break;
            }

            case NativeMethods.WM_ENTERSIZEMOVE:
                _sizing = true;
                break;

            case NativeMethods.WM_EXITSIZEMOVE:
                _sizing = false;
                SavePosition();
                break;

            case NativeMethods.WM_DPICHANGED:
            {
                // 保持像素大小不变：系统建议矩形是按新 DPI 等比放大后的尺寸，
                // 照单全收会让窗口在屏幕上明显变大。这里沿用原来的物理宽高，只把位置收回到屏幕内。
                if (_positioned && _dockEdge == EdgeDock.None && !_dragging && _resizeHit == 0 &&
                    NativeMethods.GetWindowRect(_hwnd, out var current))
                {
                    var suggested = System.Runtime.InteropServices.Marshal
                        .PtrToStructure<NativeMethods.RECT>(lParam);
                    var width = current.Right - current.Left;
                    var height = current.Bottom - current.Top;
                    NativeMethods.SetWindowPos(
                        _hwnd, IntPtr.Zero, suggested.Left, suggested.Top, width, height,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }

                _dispatcher.TryEnqueue(OnDpiChanged);
                break;
            }

            case NativeMethods.WM_DISPLAYCHANGE:
                // 显示器增减（远程桌面接管/断开、插拔外接屏）后，窗口可能落在已消失的屏上。
                // 等系统排完布局再判断一次，不在任何屏幕上就拉回主屏。
                _dispatcher.TryEnqueue(EnsureOnVisibleDisplay);
                break;

            case NativeMethods.WM_SETTINGCHANGE:
                // 系统深/浅色切换时刷新；固定深/浅色的设置不受影响。
                if (lParam != IntPtr.Zero
                    && _settings.Current.ThemeMode == (int)ThemeMode.System
                    && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                {
                    _dispatcher.TryEnqueue(ApplyAppearance);
                }

                break;
        }

        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            _tray?.ReAdd();
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void DragRegion_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (WindowDragHelper.TryBeginTouchDrag(_hwnd, Root, (UIElement)sender, e))
        {
            _attachedFollowers.Clear();
            return;
        }

        if (!WindowDragHelper.IsPrimaryPointerPressed((UIElement)sender, e))
        {
            return;
        }

        // 顶边 8px 留给缩放，别当成拖动窗口。
        if (e.GetCurrentPoint(Root).Position.Y < 8)
        {
            return;
        }

        if (!WindowDragHelper.TryGetScreenPoint(_hwnd, Root, e, out _dragStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _dragStartRect))
        {
            return;
        }

        // 两个窗口互不捆绑：拖动待办窗口不再带动日历窗口。
        _attachedFollowers.Clear();
        _dragging = ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void DragRegion_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (WindowDragHelper.TryMoveTouchDrag(_hwnd, Root, e, MoveWindowTo))
        {
            return;
        }

        if (!_dragging)
        {
            return;
        }

        if (WindowDragHelper.TryGetScreenPoint(_hwnd, Root, e, out var cursor))
        {
            MoveWindowTo(
                _dragStartRect.Left + cursor.X - _dragStartCursor.X,
                _dragStartRect.Top + cursor.Y - _dragStartCursor.Y);
        }

        e.Handled = true;
    }

    private void DragRegion_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (WindowDragHelper.TryEndTouchDrag(_hwnd, e))
        {
            return;
        }

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _attachedFollowers.Clear();
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        SavePosition();
        TryDockToEdge();

        e.Handled = true;
    }

    private void DragRegion_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _attachedFollowers.Clear();
        SavePosition();
    }

    private void MoveWindowTo(int screenX, int screenY)
    {
        var width = _dragStartRect.Right - _dragStartRect.Left;
        var height = _dragStartRect.Bottom - _dragStartRect.Top;

        if (_attachedFollowers.Count == 0)
        {
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_NOTOPMOST,
                screenX, screenY, width, height, NativeMethods.SWP_NOACTIVATE);
            return;
        }

        var hdwp = NativeMethods.BeginDeferWindowPos(1 + _attachedFollowers.Count);
        if (hdwp == IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_NOTOPMOST,
                screenX, screenY, width, height, NativeMethods.SWP_NOACTIVATE);
            return;
        }

        hdwp = NativeMethods.DeferWindowPos(
            hdwp, _hwnd, NativeMethods.HWND_NOTOPMOST,
            screenX, screenY, width, height, NativeMethods.SWP_NOACTIVATE);

        foreach (var (subHwnd, offsetX, offsetY) in _attachedFollowers)
        {
            if (hdwp == IntPtr.Zero)
            {
                break;
            }

            if (NativeMethods.IsWindowVisible(subHwnd))
            {
                hdwp = NativeMethods.DeferWindowPos(
                    hdwp, subHwnd, IntPtr.Zero,
                    screenX + offsetX, screenY + offsetY, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
        }

        if (hdwp != IntPtr.Zero)
        {
            NativeMethods.EndDeferWindowPos(hdwp);
        }
    }

}
