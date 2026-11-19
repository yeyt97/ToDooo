using System.IO;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ToDooo.Interop;
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
    private readonly ReminderService _reminder;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly List<Button> _tagButtons = new();
    private readonly List<Button> _smartViewButtons = new();
    private readonly List<FontIcon> _tagChecks = new();
    private Button? _tagClearButton;

    private NativeMethods.SUBCLASSPROC? _subclassProc;
    private DesktopAcrylicController? _acrylic;
    private Microsoft.UI.Composition.SystemBackdrops.SystemBackdropConfiguration? _backdropConfig;
    private Views.SettingsWindow? _settingsWindow;
    private TrayIcon? _tray;
    private uint _taskbarCreatedMessage;
    private bool _pinned;
    private bool _reparenting;
    private bool _started;
    private bool _exiting;
    private bool _dragging;
    private bool _suppressAddCommit;
    private TodoEditorDialog? _editor;
    private IntPtr _hostHwnd;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;

    public MainWindow(TodoRepository repository, SettingsService settings, Notifier notifier)
    {
        _repository = repository;
        _settings = settings;
        _notifier = notifier;
        ViewModel = new MainViewModel(repository);

        InitializeComponent();
        ThemePalette.ApplyTextControlPalette(Root.Resources);
        ViewModel.Load();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        BuildTagDots();
        BuildSmartViewChips();
        UpdateFilterVisuals();

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

        _reminder = new ReminderService(repository, notifier, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread())
        {
            Source = () => ViewModel.AllItems,
        };

        // 系统通知是后台线程回调的，这里转回 UI 线程处理。
        notifier.SnoozeRequested = id => _dispatcher.TryEnqueue(() => SnoozeReminder(id));
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

        _pinned = false;
        if (_settings.Current.PinnedToDesktop)
        {
            ApplyPin(true, persist: false);
        }

        CreateTray();
        StartupService.EnsureMatchesCurrentProcess();
        _reminder.LeadMinutes = _settings.Current.ReminderLeadMinutes;
        _reminder.Start();
    }

    public void ShowFromTray()
    {
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        if (!_pinned)
        {
            Activate();
            NativeMethods.SetForegroundWindow(_hwnd);
        }
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
        ThemePalette.Apply(Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode), _settings.Current.WindowOpacity);
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
        WindowAcrylic.ApplyTint(_acrylic, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark);
        _acrylic.AddSystemBackdropTarget(
            ((object)this).As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
    }

    /// <summary>设置页改动后重刷外观：主题、亚克力浓淡、代码里写死的颜色。</summary>
    public void ApplyAppearance()
    {
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode);
        var dark = Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        ThemePalette.Apply(dark, _settings.Current.WindowOpacity);
        ApplyDwmTheme(dark);

        if (_acrylic is not null)
        {
            WindowAcrylic.ApplyTint(_acrylic, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark);
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
    }

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

    private void PositionWindow()
    {
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var width = (int)Math.Round(_settings.Current.WindowWidth * scale);
        var height = (int)Math.Round(_settings.Current.WindowHeight * scale);

        var fallback = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var x = _settings.Current.WindowX ?? fallback.X + fallback.Width - width - (int)(24 * scale);
        var y = _settings.Current.WindowY ?? fallback.Y + fallback.Height - height - (int)(28 * scale);

        // 按保存位置所在的显示器夹取：放到副屏的小组件重启后不会被拉回主屏。
        var area = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest).WorkArea;
        x = Math.Clamp(x, area.X, area.X + Math.Max(0, area.Width - width));
        y = Math.Clamp(y, area.Y, area.Y + Math.Max(0, area.Height - height));

        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // 落到目标显示器后 DPI 可能变了（两个屏缩放不同），用新 DPI 重算一次尺寸。
        var landedScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (Math.Abs(landedScale - scale) > 0.001)
        {
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, 0, 0,
                (int)Math.Round(_settings.Current.WindowWidth * landedScale),
                (int)Math.Round(_settings.Current.WindowHeight * landedScale),
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
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

    private void BedToDesktop()
    {
        if (!_pinned || _hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void ApplyPin(bool pinned, bool persist)
    {
        _reparenting = true;
        try
        {
            if (pinned)
            {
                var iconView = DesktopHost.FindIconView();
                if (iconView == IntPtr.Zero)
                {
                    _pinned = false;
                    return;
                }

                // 只把桌面图标层设为 owner，窗口仍是顶层窗口：DWM 原生圆角与系统材质才能生效。
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, iconView);
                NativeMethods.SetWindowPos(
                    _hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                    | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                _hostHwnd = iconView;
                _pinned = true;
            }
            else
            {
                // 取消钉住：脱离桌面层后是普通窗口，不置顶，可以被其他窗口盖住。
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, IntPtr.Zero);
                NativeMethods.SetWindowPos(
                    _hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                    | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                _hostHwnd = IntPtr.Zero;
                _pinned = false;
            }
        }
        finally
        {
            _reparenting = false;
        }

        ApplyCornerMask();

        if (persist)
        {
            _settings.Current.PinnedToDesktop = _pinned;
            _settings.Save();
        }
    }

    private void SavePosition()
    {
        if (_reparenting || !_started)
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        _settings.Current.WindowX = rect.Left;
        _settings.Current.WindowY = rect.Top;
        // 缩放后的尺寸也要持久化，PositionWindow() 会按 DPI 还原；否则重启又回到旧的固定大小。
        // 设置项存的是逻辑像素，所以这里要把物理像素除回去。
        _settings.Current.WindowWidth = Math.Max(320, (int)Math.Round((rect.Right - rect.Left) / scale));
        _settings.Current.WindowHeight = Math.Max(360, (int)Math.Round((rect.Bottom - rect.Top) / scale));
        _settings.Save();
    }

    private void CreateTray()
    {
        _tray = new TrayIcon(_hwnd);
        _tray.ShowRequested += ShowFromTray;
        _tray.MenuRequested += ShowTrayMenu;
        _tray.Add();
    }

    /// <summary>托盘右键菜单：用 WinUI MenuFlyout，跟窗口同一套暗色圆角菜单样式。</summary>
    private bool _trayMenuOpen;

    /// <summary>
    /// 托盘右键菜单：优先 WinUI MenuFlyout；它一旦抛异常（窗口未激活等），
    /// 直接退回深色 Win32 菜单，保证菜单一定能出来，顺便把异常记进日志。
    /// </summary>
    public void ShowTrayMenu()
    {
        // 托盘消息是在窗口过程里来的，直接在里面弹 XAML 菜单会重入消息循环（激活、弹窗都要
        // 等 UI 线程，而 UI 线程正卡在这个回调里），表现就是右键一下整个应用不动。
        // 先切回正常的 dispatcher 回调，再弹菜单。
        _dispatcher.TryEnqueue(ShowTrayMenuCore);
    }

    private void ShowTrayMenuCore()
    {
        // 内容树还没挂上 XamlRoot 时（启动瞬间）飞来托盘消息，直接跳过。
        if (Root.XamlRoot is null)
        {
            return;
        }

        _trayMenuOpen = true;
        try
        {
            Activate();
            NativeMethods.SetForegroundWindow(_hwnd);
            ShowWinUiTrayMenu();
            return;
        }
        catch (Exception exception)
        {
            _trayMenuOpen = false;
            LogMenuFailure(exception);
        }

        ShowNativeTrayMenu();
    }

    private void ShowWinUiTrayMenu()
    {
        NativeMethods.GetCursorPos(out var cursor);
        NativeMethods.ScreenToClient(_hwnd, ref cursor);
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;

        var flyout = new MenuFlyout();

        var showItem = new MenuFlyoutItem
        {
            Text = "显示待办",
            Icon = new FontIcon { Glyph = "\uE7B3" },
        };
        showItem.Click += (_, _) => ShowFromTray();

        var addItem = new MenuFlyoutItem
        {
            Text = "添加待办",
            Icon = new FontIcon { Glyph = "\uE710" },
        };
        addItem.Click += (_, _) =>
        {
            ShowFromTray();
            _ = AddDetailedAsync();
        };

        var startupItem = new ToggleMenuFlyoutItem
        {
            Text = "开机自启",
            IsChecked = StartupService.IsEnabled,
            Icon = new FontIcon { Glyph = "\uE768" },
        };
        startupItem.Click += (_, _) => StartupService.SetEnabled(startupItem.IsChecked);

        var exitItem = new MenuFlyoutItem
        {
            Text = "退出 ToDooo",
            Icon = new FontIcon { Glyph = "\uE7E8" },
        };
        exitItem.Click += (_, _) => ExitApp();

        flyout.Items.Add(showItem);
        flyout.Items.Add(addItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(exitItem);

        flyout.Closed += (_, _) =>
        {
            _trayMenuOpen = false;
            _dispatcher.TryEnqueue(BedToDesktop);
        };

        flyout.ShowAt(Root, new FlyoutShowOptions
        {
            Position = new Windows.Foundation.Point(cursor.X / scale, cursor.Y / scale),
        });
    }

    /// <summary>WinUI 菜单不可用时的兜底：系统深色弹出菜单（EnableDarkWin32Menus 已把主题设为 ForceDark）。</summary>
    private void ShowNativeTrayMenu()
    {
        NativeMethods.GetCursorPos(out var point);

        var menu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 1, "显示待办");
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 2, "添加待办");
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 3, "退出 ToDooo");

        var command = NativeMethods.TrackPopupMenuEx(
            menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_NONOTIFY,
            point.X, point.Y, _hwnd, IntPtr.Zero);

        NativeMethods.DestroyMenu(menu);
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        BedToDesktop();

        switch (command)
        {
            case 1:
                ShowFromTray();
                break;
            case 2:
                ShowFromTray();
                _ = AddDetailedAsync();
                break;
            case 3:
                ExitApp();
                break;
        }
    }

    private static void LogMenuFailure(Exception exception)
    {
        try
        {
            File.AppendAllText(AppPaths.LogPath, $"[{DateTime.Now:O}] tray menu failed: {exception}\r\n");
        }
        catch
        {
            // 日志失败忽略
        }
    }

    private List<TodoItem>? _undoItems;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _undoTimer;

    private void ShowUndo(string message, List<TodoItem> items)
    {
        _undoItems = items;
        UndoText.Text = message;
        UndoBar.Visibility = Visibility.Visible;

        _undoTimer ??= _dispatcher.CreateTimer();
        _undoTimer.Stop();
        _undoTimer.Interval = TimeSpan.FromSeconds(6);

        void OnTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            sender.Tick -= OnTick;
            HideUndo();
        }

        _undoTimer.Tick += OnTick;
        _undoTimer.Start();
    }

    private void HideUndo()
    {
        _undoTimer?.Stop();
        _undoItems = null;
        UndoBar.Visibility = Visibility.Collapsed;
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undoItems is { Count: > 0 } items)
        {
            ViewModel.Restore(items);
            UpdateFilterVisuals();
            UpdateTagVisuals();
        }

        HideUndo();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => ToggleSearch();

    private void ToggleSearch()
    {
        if (SearchBar.Visibility == Visibility.Visible)
        {
            SearchBox.Text = string.Empty;
            ViewModel.SetSearch(string.Empty);
            SearchBar.Visibility = Visibility.Collapsed;
            FocusAddBox();
        }
        else
        {
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Focus(FocusState.Programmatic);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.SetSearch(SearchBox.Text);
        ClearSearchButton.Visibility = SearchBox.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 没内容就收起搜索条，不占位置。
        if (SearchBox.Text.Length == 0 && SearchBar.Visibility == Visibility.Visible)
        {
            SearchBar.Visibility = Visibility.Collapsed;
            ViewModel.SetSearch(string.Empty);
        }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ToggleSearch();
        }
    }

    private void NewTodoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FocusAddBox();
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleSearch();
    }

    private void FilterAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.SetFilter(sender.Key switch
        {
            VirtualKey.Number1 => TodoFilter.All,
            VirtualKey.Number2 => TodoFilter.Active,
            VirtualKey.Number3 => TodoFilter.Important,
            VirtualKey.Number4 => TodoFilter.Done,
            _ => TodoFilter.All,
        });
        UpdateFilterVisuals();
    }

    public async Task ExportDataAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"ToDooo-{DateTime.Now:yyyyMMdd}",
            };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var items = ViewModel.AllItems.Select(item => new
            {
                item.Title,
                item.IsDone,
                item.IsImportant,
                DueAt = item.DueAt?.ToString("yyyy-MM-dd'T'HH:mm:ss"),
                Recurrence = item.Recurrence.ToString(),
                item.TagIndex,
                item.SortOrder,
                CreatedAt = item.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss"),
                CompletedAt = item.CompletedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss"),
                Subtasks = item.Subtasks.Select(subtask => new
                {
                    subtask.Title,
                    subtask.IsDone,
                    subtask.SortOrder,
                }),
            });

            var json = System.Text.Json.JsonSerializer.Serialize(
                items, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await Windows.Storage.FileIO.WriteTextAsync(file, json);
            ShowBalloon("已导出", file.Name);
        }
        catch
        {
            ShowBalloon("导出失败", "换个位置再试试");
        }
    }

    private void MoreFlyout_Opening(object sender, object e)
    {
        PinMenuItem.IsChecked = _pinned;
    }

    private void PinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyPin(PinMenuItem.IsChecked, persist: true);
        // 钉住失败（找不到桌面图标层）时回滚勾选状态。
        PinMenuItem.IsChecked = _pinned;
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new Views.SettingsWindow(
            _settings,
            OnSettingsChanged,
            ExportDataAsync);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.ShowOver(_appWindow);
    }

    private void OnSettingsChanged()
    {
        ApplyAppearance();
        _reminder.LeadMinutes = _settings.Current.ReminderLeadMinutes;
        BuildSmartViewChips();

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
        SavePosition();
        _tray?.Dispose();
        _reminder.Dispose();
        Application.Current.Exit();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        switch (msg)
        {
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
                // 点击激活会把窗口提到应用窗口之上，激活后立即沉回桌面层。
                // 托盘菜单展开期间例外：沉底会把 flyout 一起收掉，等菜单关了再沉。
                if ((wParam.ToInt64() & 0xFFFF) != 0)
                {
                    if (!_trayMenuOpen && !_sizing)
                    {
                        _dispatcher.TryEnqueue(BedToDesktop);
                    }
                }
                else
                {
                    // WA_INACTIVE = 0：真正的窗口级失焦（点了别的程序、桌面）。
                    // XAML 的 LostFocus 在这种场景下不触发，只能在这里补一次草稿提交。
                    _dispatcher.TryEnqueue(() => CommitAddDraft());
                }

                break;

            case NativeMethods.WM_SIZE:
                if (wParam.ToInt64() == 1) // SIZE_MINIMIZED：ShowWindow 等绕过 WM_SYSCOMMAND 的路径
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_RESTORE);
                        BedToDesktop();
                    });
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
                    var minWidth = (int)Math.Round(320 * scale);
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

                // Show Desktop 等外壳操作会试图把窗口隐藏/最小化，钉住时直接拦掉。
                if (_pinned && (pos.flags & NativeMethods.SWP_HIDEWINDOW) != 0)
                {
                    pos.flags &= ~NativeMethods.SWP_HIDEWINDOW;
                    pos.flags |= NativeMethods.SWP_SHOWWINDOW;
                    changed = true;
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
            if (_pinned)
            {
                ApplyPin(true, persist: false);
            }

            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void DragRegion_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 顶边 8px 留给缩放，别当成拖动窗口。
        if (e.GetCurrentPoint(Root).Position.Y < 8)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out _dragStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _dragStartRect))
        {
            return;
        }

        _dragging = ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void DragRegion_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            MoveWindowTo(
                _dragStartRect.Left + cursor.X - _dragStartCursor.X,
                _dragStartRect.Top + cursor.Y - _dragStartCursor.Y);
        }

        e.Handled = true;
    }

    private void DragRegion_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        SavePosition();
        e.Handled = true;
    }

    private void DragRegion_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        SavePosition();
    }

    private void MoveWindowTo(int screenX, int screenY)
    {
        var width = _dragStartRect.Right - _dragStartRect.Left;
        var height = _dragStartRect.Bottom - _dragStartRect.Top;

        NativeMethods.SetWindowPos(
            _hwnd, _pinned ? NativeMethods.HWND_BOTTOM : NativeMethods.HWND_NOTOPMOST,
            screenX, screenY, width, height, NativeMethods.SWP_NOACTIVATE);
    }

    private void BuildTagDots()
    {
        var style = (Style)Application.Current.Resources["TagDotButtonStyle"];

        for (var i = 0; i < MainViewModel.TagColors.Length; i++)
        {
            var hex = MainViewModel.TagColors[i];
            var color = Color.FromArgb(
                255,
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
            AddTagButton(style, i, color, $"标签 {i + 1}");
        }

        // 选中某个颜色后，末尾才出现的“清除颜色”按钮（圆圈 + 叉）。
        var clearIcon = new Grid
        {
            Width = 18,
            Height = 18,
        };
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

        _tagClearButton = new Button
        {
            Style = style,
            Content = clearIcon,
            Visibility = Visibility.Collapsed,
        };
        _tagClearButton.Click += (_, _) =>
        {
            ViewModel.SelectTag(-1);
            UpdateTagVisuals();
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_tagClearButton, "清除颜色筛选");
        TagPanel.Children.Add(_tagClearButton);
    }

    private void AddTagButton(Style style, int index, Color color, string name)
    {
        // 和编辑弹窗同一套：18px 圆点，选中时圆点里出现对勾。
        var dot = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(color),
        };

        var luminance = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        var check = new FontIcon
        {
            Glyph = "",
            FontSize = 11,
            Foreground = new SolidColorBrush(luminance > 150
                ? Color.FromArgb(255, 0x26, 0x2B, 0x33)
                : Color.FromArgb(255, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        var content = new Grid();
        content.Children.Add(dot);
        content.Children.Add(check);

        var button = new Button
        {
            Style = style,
            Content = content,
            Tag = index,
            Background = (Brush)Application.Current.Resources["TransparentBrush"],
        };
        button.Click += TagDot_Click;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        _tagButtons.Add(button);
        _tagChecks.Add(check);
        TagPanel.Children.Add(button);
    }

    private void TagDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            // 再点一次已选中的颜色即取消筛选。
            ViewModel.SelectTag(ViewModel.SelectedTag == index ? -1 : index);
            UpdateTagVisuals();
        }
    }

    private void UpdateTagVisuals()
    {
        // 选中只显示圆点里的对勾，不加灰底。
        var transparent = (Brush)Application.Current.Resources["TransparentBrush"];
        for (var i = 0; i < _tagButtons.Count; i++)
        {
            _tagChecks[i].Visibility = ViewModel.SelectedTag == i ? Visibility.Visible : Visibility.Collapsed;
            _tagButtons[i].Background = transparent;
        }

        if (_tagClearButton is not null)
        {
            _tagClearButton.Visibility = ViewModel.SelectedTag >= 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void BuildSmartViewChips()
    {
        SmartViewPanel.Children.Clear();
        _smartViewButtons.Clear();

        foreach (var setting in _settings.Current.SmartViews)
        {
            if (!setting.Enabled || !Enum.IsDefined(typeof(SmartView), setting.View))
            {
                continue;
            }

            var view = (SmartView)setting.View;
            var button = new Button
            {
                Content = SmartViewInfo.Title(view),
                Tag = view,
                Style = (Style)Application.Current.Resources["SmartChipStyle"],
            };
            button.Click += SmartView_Click;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button, $"视图 {SmartViewInfo.Title(view)}");
            _smartViewButtons.Add(button);
            SmartViewPanel.Children.Add(button);
        }

        SmartViewPanel.Visibility = _smartViewButtons.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSmartViewVisuals();
    }

    private void SmartView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SmartView view })
        {
            ViewModel.ToggleSmartView(view);
            UpdateSmartViewVisuals();
        }
    }

    private void UpdateSmartViewVisuals()
    {
        foreach (var button in _smartViewButtons)
        {
            var active = button.Tag is SmartView view && (int)view == ViewModel.SelectedSmartView;
            button.Background = active
                ? PaletteBrush("SegmentSelectedBrush")
                : PaletteBrush("TransparentBrush");
            button.Foreground = active
                ? PaletteBrush("TextPrimaryBrush")
                : PaletteBrush("TextMutedBrush");
        }
    }

    private void UpdateFilterVisuals()
    {
        SetStatVisual(AllFilterButton, ViewModel.Filter == TodoFilter.All);
        SetStatVisual(ActiveStat, ViewModel.Filter == TodoFilter.Active);
        SetStatVisual(ImportantStat, ViewModel.Filter == TodoFilter.Important);
        SetStatVisual(DoneStat, ViewModel.Filter == TodoFilter.Done);

        // 批量删除只在「已完成」视图里出现，避免在其他视图误触。
        ClearCompletedButton.Visibility = ViewModel.Filter == TodoFilter.Done
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 切换分段筛选时，智能视图的选中态也要跟着刷。
        UpdateSmartViewVisuals();
    }

    // 分段筛选的选中态是一块中性高亮，而不是强调色或透明度差异。
    private void SetStatVisual(Button button, bool active)
    {
        button.Background = active ? PaletteBrush("SegmentSelectedBrush") : PaletteBrush("TransparentBrush");
        button.Foreground = PaletteBrush(active ? "TextPrimaryBrush" : "TextMutedBrush");
    }

    private void AllFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSmartView();
        ViewModel.SetFilter(TodoFilter.All);
        UpdateFilterVisuals();
    }

    private void ActiveFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSmartView();
        ViewModel.SetFilter(TodoFilter.Active);
        UpdateFilterVisuals();
    }

    private void ImportantFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSmartView();
        ViewModel.SetFilter(TodoFilter.Important);
        UpdateFilterVisuals();
    }

    private void DoneFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSmartView();
        ViewModel.SetFilter(TodoFilter.Done);
        UpdateFilterVisuals();
    }

    private bool _sizing;
    private int _resizeHit;
    private NativeMethods.POINT _resizeStartCursor;
    private NativeMethods.RECT _resizeStartRect;

    // 鼠标离开窗口时淡出，移回恢复；设置里没开就保持原样。
    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AnimateRootOpacity(1);
        UpdateResizeCursor(e.GetCurrentPoint(Root).Position);
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        AnimateRootOpacity(_settings.Current.AutoHide ? 0.25 : 1);
        Root.SetCursor(null);
    }

    // 无边框窗口的内容层拿不到系统边缘命中，这里自己按位置判断贴边并手动缩放。
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pressOnSubtask = IsInsideSubtask(e.OriginalSource as DependencyObject);

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

        if (!NativeMethods.GetCursorPos(out _resizeStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _resizeStartRect))
        {
            return;
        }

        _resizeHit = hit;
        Root.CapturePointer(e.Pointer);
        UpdateResizeCursor(local);
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var local = e.GetCurrentPoint(Root).Position;

        if (_resizeHit == 0)
        {
            UpdateResizeCursor(local);
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            var minWidth = (int)Math.Round(320 * scale);
            var minHeight = (int)Math.Round(360 * scale);
            var dx = cursor.X - _resizeStartCursor.X;
            var dy = cursor.Y - _resizeStartCursor.Y;

            var left = _resizeStartRect.Left;
            var top = _resizeStartRect.Top;
            var right = _resizeStartRect.Right;
            var bottom = _resizeStartRect.Bottom;

            if (_resizeHit is NativeMethods.HTLEFT or NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMLEFT)
            {
                left = Math.Min(_resizeStartRect.Left + dx, _resizeStartRect.Right - minWidth);
            }

            if (_resizeHit is NativeMethods.HTRIGHT or NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMRIGHT)
            {
                right = Math.Max(_resizeStartRect.Right + dx, _resizeStartRect.Left + minWidth);
            }

            if (_resizeHit is NativeMethods.HTTOP or NativeMethods.HTTOPLEFT or NativeMethods.HTTOPRIGHT)
            {
                top = Math.Min(_resizeStartRect.Top + dy, _resizeStartRect.Bottom - minHeight);
            }

            if (_resizeHit is NativeMethods.HTBOTTOM or NativeMethods.HTBOTTOMLEFT or NativeMethods.HTBOTTOMRIGHT)
            {
                bottom = Math.Max(_resizeStartRect.Bottom + dy, _resizeStartRect.Top + minHeight);
            }

            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        e.Handled = true;
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHit == 0)
        {
            return;
        }

        _resizeHit = 0;
        Root.ReleasePointerCapture(e.Pointer);
        SavePosition();
        UpdateResizeCursor(e.GetCurrentPoint(Root).Position);
        e.Handled = true;
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHit == 0)
        {
            return;
        }

        _resizeHit = 0;
        SavePosition();
        Root.SetCursor(null);
    }

    private int EdgeHit(Windows.Foundation.Point point)
    {
        const double edge = 8;
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

    private void UpdateResizeCursor(Windows.Foundation.Point point)
    {
        var hit = _resizeHit != 0 ? _resizeHit : EdgeHit(point);
        Root.SetCursor(hit switch
        {
            NativeMethods.HTLEFT or NativeMethods.HTRIGHT => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
            NativeMethods.HTTOP or NativeMethods.HTBOTTOM => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
            NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMRIGHT => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
            NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMLEFT => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
            _ => null,
        });
    }

    private void AnimateRootOpacity(double target)
    {
        if (Math.Abs(Root.Opacity - target) < 0.01)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, Root);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void SnoozeReminder(long id)
    {
        var item = ViewModel.AllItems.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null || item.IsDone)
        {
            return;
        }

        ViewModel.Snooze(item, 10);
        ShowBalloon("已推迟", $"{item.Title}：10 分钟后再提醒");
    }

    private void FocusAddBox() => AddTextBox.Focus(FocusState.Programmatic);

    private void AddTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(AddTextBox.Text);
        ClearAddButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearAddButton_Click(object sender, RoutedEventArgs e)
    {
        AddTextBox.Text = string.Empty;
        FocusAddBox();
    }

    private void AddTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (CommitAddDraft())
            {
                // 输入框常驻，列表重建/滚动都可能抢走焦点；两帧后把焦点还回来以便连续添加。
                _dispatcher.TryEnqueue(() =>
                {
                    if (ViewModel.Items.Count > 0)
                    {
                        TodoList.ScrollIntoView(ViewModel.Items[0]);
                    }

                    _dispatcher.TryEnqueue(() => AddTextBox.Focus(FocusState.Programmatic));
                });
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            AddTextBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    // 窗口失活（切到别的程序、点桌面）时 TextBox 不会触发 LostFocus，得靠这里补。
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            CommitAddDraft();
        }
    }

    // 离焦即提交：点列表、点窗口别处、切走窗口，敲了一半的草稿都不会再悄悄丢掉。
    private void AddTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 点「清除」「详细添加」造成的离焦不算提交：
        // 前者是要丢弃草稿，后者要把草稿带进对话框，先提交就会重复添加两次。
        // 口径放宽到整个输入条：焦点只是跑到同一条里的按钮上，就还没离开这个「添加动作」。
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        if (focused is not null && IsWithin(focused, AddBar))
        {
            return;
        }

        CommitAddDraft();
    }

    // 回车和离焦共用的一套提交逻辑；返回是否真的加了一条。
    private bool CommitAddDraft()
    {
        // 编辑器弹窗打开期间不提交：草稿归弹窗管，主窗口这时的「失焦」只是被弹窗抢了前台。
        if (_suppressAddCommit)
        {
            return false;
        }

        var title = AddTextBox.Text;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        // 标题里带日期/时间就顺手识别：明天 10 点开会 → 截止明天 10:00，标题只留"开会"。
        if (ToDooo.Helpers.NaturalDateParser.TryParse(title, DateTime.Now, out var due, out var cleaned))
        {
            ViewModel.AddItem(new TodoItem { Title = cleaned, DueAt = due });
        }
        else
        {
            ViewModel.Add(title);
        }

        AddTextBox.Text = string.Empty;
        UpdateFilterVisuals();
        UpdateTagVisuals();
        return true;
    }

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void AddDetailButton_Click(object sender, RoutedEventArgs e) => _ = AddDetailedAsync();

    private async Task AddDetailedAsync()
    {
        // 把输入框里已经敲好的草稿带进详细新建，取消时原样保留。
        // 草稿里的日期/时间也一并识别好，弹窗里直接预填，用户可以再改。
        var draft = AddTextBox.Text;
        var initialDue = ToDooo.Helpers.NaturalDateParser.TryParse(draft, DateTime.Now, out var parsedDue, out var cleanedDraft)
            ? parsedDue
            : (DateTime?)null;
        var dialog = new TodoEditorDialog(_settings, initialDue is null ? draft : cleanedDraft, initialDue);

        if (await ShowEditorAsync(dialog))
        {
            var item = new TodoItem
            {
                Title = dialog.TodoTitle,
                DueAt = dialog.DueAt,
                Recurrence = dialog.Recurrence,
                IsImportant = dialog.IsImportant,
                TagIndex = dialog.TagIndex,
            };

            foreach (var subtaskDraft in dialog.Subtasks)
            {
                var title = subtaskDraft.Title.Trim();
                if (title.Length == 0)
                {
                    continue;
                }

                item.Subtasks.Add(new SubtaskItem
                {
                    Title = title,
                    IsDone = subtaskDraft.Done,
                    SortOrder = item.Subtasks.Count,
                });
            }

            ViewModel.AddItem(item);
            AddTextBox.Text = string.Empty;
            UpdateFilterVisuals();
            UpdateTagVisuals();

            // 保存后才把焦点收回添加框；取消时不去抢焦点，免得主窗口被提起来闪一下。
            FocusAddBox();
        }
    }

    /// <summary>
    /// 显示编辑窗口并等结果。同一时间只允许一个：已经开着就把那个提到前台，
    /// 返回 false（本次没有新建任何东西），避免叠出好几个窗口。
    /// </summary>
    private async Task<bool> ShowEditorAsync(TodoEditorDialog dialog)
    {
        if (_editor is not null)
        {
            _editor.Activate();
            return false;
        }

        _editor = dialog;

        // 弹窗是独立窗口，打开时主窗口会失活；关掉这段自动提交，
        // 否则「离焦即保存」会先把草稿存成一条，再被弹窗存第二条。
        _suppressAddCommit = true;
        try
        {
            dialog.ShowOver(_appWindow);
            return await dialog.Result;
        }
        finally
        {
            _suppressAddCommit = false;
            _editor = null;
        }
    }

    // DataTemplate 刚生成时 x:Bind 可能还没求值，IsChecked 会短暂停在 null（三态）。
    private void DoneCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TodoItem item } box)
        {
            box.IsChecked = item.IsDone;
        }
    }

    private void DoneCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TodoItem item } box)
        {
            ViewModel.SetDone(item, box.IsChecked == true);
            // SetDone 会重建筛选视图，先把当前控件状态锁回数据值，避免模板复用时状态漂移。
            box.IsChecked = item.IsDone;
            UpdateFilterVisuals();
        }
    }

    // 点整行只展开/收起子任务，不再打开编辑窗口。
    private void TodoList_ItemClick(object sender, ItemClickEventArgs e)
    {
        // 刚拖完子任务松手时，别把这次释放再当成「点行展开」。
        if (Environment.TickCount64 - _lastSubtaskDropTicks < 400)
        {
            return;
        }

        if (e.ClickedItem is TodoItem item && (item.HasSubtasks || item.SubtasksExpanded))
        {
            item.SubtasksExpanded = !item.SubtasksExpanded;
            ResetSubtaskDragGuardIfCollapsed(item);
        }
    }

    private void ResetSubtaskDragGuardIfCollapsed(TodoItem item)
    {
        if (item.SubtasksExpanded)
        {
            return;
        }

        item.SubtaskAddActive = false;

        // 子任务区随收起消失时 PointerExited 不一定会到，手动把拖动开关还原。
        _pointerOverSubtasks = false;
        if (!_subtaskDragActive)
        {
            TodoList.CanDragItems = true;
            TodoList.CanReorderItems = true;
        }
    }

    private void SubtaskProgress_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetSubtaskProgressMode(sender, adding: true);

    private void SubtaskProgress_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            var point = e.GetCurrentPoint(button).Position;
            if (point.X >= 0 && point.Y >= 0 &&
                point.X <= button.ActualWidth && point.Y <= button.ActualHeight)
            {
                return;
            }
        }

        SetSubtaskProgressMode(sender, adding: false);
    }

    private static void SetSubtaskProgressMode(object sender, bool adding)
    {
        if (sender is not DependencyObject root)
        {
            return;
        }

        if (FindTaggedChild<StackPanel>(root, "SubtaskProgressView") is { } progress)
        {
            progress.Visibility = adding ? Visibility.Collapsed : Visibility.Visible;
        }

        if (FindTaggedChild<StackPanel>(root, "SubtaskProgressAdd") is { } hint)
        {
            hint.Visibility = adding ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // 平时显示“子任务 2/5”，鼠标移上去变成“添加子任务”，点一下直接开始输入。
    private void SubtaskAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TodoItem item })
        {
            item.SubtasksExpanded = true;
            item.SubtaskAddActive = true;
            DispatcherQueue.TryEnqueue(() => FocusSubtaskAddBox(item));
        }
    }

    private void SubtaskCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.DataContext is SubtaskItem subtask)
        {
            if (FindOwnerTodo(box) is { } item)
            {
                ViewModel.SetSubtaskDone(item, subtask, box.IsChecked == true);
            }
        }
    }

    private SubtaskItem? _draggedSubtask;
    private TodoItem? _draggedOwner;
    private bool _pointerOverSubtasks;

    // 指针在子任务区域时先禁掉外层列表的拖动，否则会变成拖动整张卡片。
    private void SubtaskListArea_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverSubtasks = true;
        TodoList.CanDragItems = false;
        TodoList.CanReorderItems = false;
    }

    private void SubtaskListArea_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement area)
        {
            var point = e.GetCurrentPoint(area).Position;
            if (point.X >= 0 && point.Y >= 0 &&
                point.X <= area.RenderSize.Width && point.Y <= area.RenderSize.Height)
            {
                return;
            }
        }

        // 子任务拖到列表外时指针会离开这个区域，但拖动还没结束，别急着把列表拖动打开。
        if (_subtaskDragActive)
        {
            return;
        }

        _pointerOverSubtasks = false;
        TodoList.CanDragItems = true;
        TodoList.CanReorderItems = true;
    }

    private void TodoList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_pointerOverSubtasks || _subtaskDragActive || _pressOnSubtask)
        {
            e.Cancel = true;
        }
    }

    private const string SubtaskDragKey = "ToDoooSubtask";
    private long _lastSubtaskDropTicks;
    private bool _subtaskDragActive;
    private bool _pressOnSubtask;

    private void SubtaskRow_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: SubtaskItem subtask })
        {
            _draggedSubtask = subtask;
            _draggedOwner = FindOwnerTodo(sender);
            _subtaskDragActive = true;
            if (sender is UIElement element)
            {
                element.Opacity = 0.55;
            }

            args.Data.Properties[SubtaskDragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        }
    }

    // 子任务拖动时整个窗口都接受移动，避免指针划过行间空隙时闪“禁止”图标。
    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!_subtaskDragActive)
        {
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        if (_draggedOwner is { } owner && _draggedSubtask is not null)
        {
            ViewModel.CommitSubtaskOrder(owner);
        }
    }

    private void SubtaskRow_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(SubtaskDragKey))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;

        // 和主任务拖排序一样：拖到哪一行，行就实时让位。
        if (_draggedSubtask is not { } dragged ||
            sender is not Grid row ||
            row.DataContext is not SubtaskItem target ||
            dragged == target ||
            FindOwnerTodo(row) is not { } item)
        {
            return;
        }

        var list = item.Subtasks;
        var from = list.IndexOf(dragged);
        var to = list.IndexOf(target);
        if (from < 0 || to < 0)
        {
            return;
        }

        var point = e.GetPosition(row);
        var index = point.Y <= row.ActualHeight / 2 ? to : to + 1;
        if (from < index)
        {
            index--;
        }

        ViewModel.MoveSubtaskLive(item, dragged, index);
    }

    private void SubtaskRow_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        sender.Opacity = 1;
        _draggedSubtask = null;
        _draggedOwner = null;
        _subtaskDragActive = false;
        _pointerOverSubtasks = false;
        _lastSubtaskDropTicks = Environment.TickCount64;
        TodoList.CanDragItems = true;
        TodoList.CanReorderItems = true;
    }

    private void SubtaskRow_Drop(object sender, DragEventArgs e)
    {
        if (_draggedSubtask is not { } dragged ||
            sender is not Grid row ||
            !e.DataView.Properties.ContainsKey(SubtaskDragKey) ||
            row.DataContext is not SubtaskItem target ||
            dragged == target ||
            FindOwnerTodo(row) is not { } item)
        {
            return;
        }

        // 顺序已经在 DragOver 里实时排好，这里只落库。
        ViewModel.CommitSubtaskOrder(item);
        _draggedSubtask = null;
    }

    private static bool IsInsideSubtask(DependencyObject? start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: SubtaskItem })
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static TodoItem? FindOwnerTodo(DependencyObject start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: TodoItem item })
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private SubtaskItem? _menuSubtask;

    private void SubtaskFlyout_Opening(object sender, object e)
    {
        if (sender is MenuFlyout { Target: FrameworkElement target } &&
            target.DataContext is SubtaskItem subtask)
        {
            _menuSubtask = subtask;
            _menuItem = FindOwnerTodo(target);
        }
    }

    private async void SubtaskDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_menuSubtask is not { } subtask || _menuItem is not { } item)
        {
            return;
        }

        var dialog = CreateCardDialog("删除子任务", $"确定删除「{subtask.Title}」吗？", "删除");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteSubtask(item, subtask);
        }
    }

    /// <summary>弹框统一用卡片色板：和窗口主题、透明度保持一致。</summary>
    private ContentDialog CreateCardDialog(
        string title,
        object content,
        string primaryButtonText,
        bool primaryEnabled = true,
        ContentDialogButton defaultButton = ContentDialogButton.Close)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = defaultButton,
            IsPrimaryButtonEnabled = primaryEnabled,
        };

        // 弹框本身不继承主窗口的 RequestedTheme，浅色窗口在深色系统下会显示成白字。
        dialog.RequestedTheme = Root.RequestedTheme == ElementTheme.Default
            ? Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode)
            : Root.RequestedTheme;
        ThemePalette.ApplyTextControlPalette(dialog.Resources);

        dialog.Resources["ContentDialogBackground"] = Application.Current.Resources["DialogAcrylicBrush"];
        dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["CardStrokeBrush"];
        dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(8);
        dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        return dialog;
    }

    // 卡片悬停：提亮卡片，并让未标记重要的行浮出星标按钮（已标记的本来就在亮着）。
    private void TodoCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        card.Background = (Brush)Application.Current.Resources["ItemCardHoverBrush"];
        if (FindTaggedChild<Button>(card, "ItemActions") is { } star)
        {
            star.Opacity = 1;
            star.IsHitTestVisible = true;
        }
    }

    private void TodoCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        card.ClearValue(Border.BackgroundProperty);

        // 标记了重要的行，星标留在原位常亮；没标记的收回悬停态。
        if (FindTaggedChild<Button>(card, "ItemActions") is { } star)
        {
            var pinned = star.DataContext is TodoItem { IsImportant: true };
            star.Opacity = pinned ? 1 : 0;
            star.IsHitTestVisible = pinned;
        }
    }

    private static T? FindTaggedChild<T>(DependencyObject root, string tag) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Tag is string value && value == tag)
            {
                return typed;
            }

            if (FindTaggedChild<T>(child, tag) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // 星标按钮每次被模板复用时不用额外处理：Opacity / IsHitTestVisible 都绑在 IsImportant 上，
    // 上下文菜单里标记重要后也能立刻亮起，不依赖悬停。

    private void QuickImportant_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem item })
        {
            ViewModel.ToggleImportant(item);
            UpdateFilterVisuals();

            // 不等绑定/列表刷新，直接给当前按钮一个即时反馈。
            if (sender is Button star)
            {
                // 指针就在这一行上，标记与否都先保持可见，移出时由 PointerExited 收回去。
                star.Opacity = 1;
                star.IsHitTestVisible = true;

                if (star.Content is FontIcon icon)
                {
                    icon.Glyph = item.IsImportant ? "\uE735" : "\uE734";
                    icon.Foreground = (Brush)Application.Current.Resources[
                        item.IsImportant ? "AccentBrush" : "TextMutedBrush"];
                }
            }
        }
    }

    private async void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DoneCount == 0)
        {
            return;
        }

        var dialog = CreateCardDialog(
            "清除已完成",
            $"将删除 {ViewModel.DoneCount} 条已完成待办，删除后可在底部撤销。",
            "清除");

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var removed = ViewModel.ClearCompleted();
        UpdateFilterVisuals();
        if (removed.Count > 0)
        {
            ShowUndo($"已清除 {removed.Count} 条已完成", removed);
        }
    }

    private TodoItem? _menuItem;

    private void RowFlyout_Opening(object sender, object e)
    {
        if (sender is MenuFlyout { Target: FrameworkElement target })
        {
            _menuItem = target.DataContext as TodoItem;

            // 没有截止时间就没有提醒可推迟。
            if (((MenuFlyout)sender).Items.Count > 2 &&
                ((MenuFlyout)sender).Items[2] is MenuFlyoutItem snooze)
            {
                snooze.Visibility = _menuItem?.DueAt is null
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }
    }

    private void RowAddSubtask_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is not { } item)
        {
            return;
        }

        item.SubtasksExpanded = true;
        item.SubtaskAddActive = true;
        DispatcherQueue.TryEnqueue(() => FocusSubtaskAddBox(item));
    }

    private void FocusSubtaskAddBox(TodoItem item)
    {
        if (TodoList.ContainerFromItem(item) is not DependencyObject container)
        {
            return;
        }

        if (FindTaggedChild<TextBox>(container, "SubtaskAddBox") is { } box)
        {
            box.Focus(FocusState.Programmatic);
        }
    }

    private void SubtaskAddBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CommitSubtaskAdd(box, keepOpen: true);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            box.Text = string.Empty;
            if (box.DataContext is TodoItem item)
            {
                item.SubtaskAddActive = false;
            }
        }
    }

    private void SubtaskAddBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            CommitSubtaskAdd(box, keepOpen: false);
        }
    }

    private void CommitSubtaskAdd(TextBox box, bool keepOpen)
    {
        if (box.DataContext is not TodoItem item)
        {
            return;
        }

        var title = box.Text.Trim();
        if (title.Length > 0)
        {
            ViewModel.AddSubtask(item, title);
            box.Text = string.Empty;
        }

        // 回车提交后保留输入框，方便连续加；离焦或 Esc 才收起。
        item.SubtaskAddActive = keepOpen;
        if (keepOpen)
        {
            box.Focus(FocusState.Programmatic);
        }
    }

    private void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            _ = EditItemAsync(item);
        }
    }

    private void RowSnooze_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            ViewModel.Snooze(item, 10);
            ShowBalloon("已推迟", $"{item.Title}：10 分钟后再提醒");
        }
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        _ = ConfirmDeleteAsync();
    }

    private async Task ConfirmDeleteAsync()
    {
        if (_menuItem is not { } item)
        {
            return;
        }

        var dialog = CreateCardDialog("删除待办", $"确定删除「{item.Title}」吗？", "删除");

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ViewModel.Delete(item);
        UpdateFilterVisuals();
        ShowUndo($"已删除「{item.Title}」", new List<TodoItem> { item });
    }

    private async Task EditItemAsync(TodoItem item)
    {
        var dialog = new TodoEditorDialog(_settings, item);

        if (await ShowEditorAsync(dialog))
        {
            ViewModel.ApplyEdit(
                item, dialog.TodoTitle, dialog.DueAt, dialog.Recurrence, dialog.IsImportant, dialog.TagIndex);
            ViewModel.ApplySubtasks(item, dialog.Subtasks);
            UpdateFilterVisuals();
        }
    }

    private void TodoList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var ordered = ViewModel.Items.ToList();
        DispatcherQueue.TryEnqueue(() => ViewModel.PersistReorder(ordered));
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => ExitApp();
}
