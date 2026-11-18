using System.IO;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private readonly List<Border> _tagRings = new();

    private NativeMethods.SUBCLASSPROC? _subclassProc;
    private DesktopAcrylicController? _acrylic;
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
        ViewModel.Load();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        BuildTagDots();
        UpdateFilterVisuals();

        // XAML 的 LostFocus 在窗口整体失活时不会触发，这里补一次全局失焦提交。
        Activated += MainWindow_Activated;

        _reminder = new ReminderService(repository, notifier, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread())
        {
            Source = () => ViewModel.AllItems,
        };
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

        ApplyAcrylicMaterial();

        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        // 不再手动剥掉 WS_THICKFRAME：那会让上面的 IsResizable 失效、边缘拖不动。
        // 最小化/最大化按钮已由 OverlappedPresenter 关掉，这里只处理扩展样式。

        ApplyCornerMask();
        var dark = 1;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

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

        var config = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark,
        };

        _acrylic = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
            TintColor = Color.FromArgb(255, 0x2A, 0x30, 0x38),
            // tint 太厚会把模糊的桌面纹理糊成一片纯色，调到能透出磨砂颗粒感。
            TintOpacity = 0.42f,
            LuminosityOpacity = 0.64f,
            FallbackColor = Color.FromArgb(255, 0x20, 0x25, 0x2C),
        };
        _acrylic.AddSystemBackdropTarget(
            ((object)this).As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(config);
    }

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
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, IntPtr.Zero);
                NativeMethods.SetWindowPos(
                    _hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
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
    public void ShowTrayMenu()
    {
        // 内容树还没挂上 XamlRoot 时（启动瞬间）飞来托盘消息，直接跳过，避免 ShowAt 抛异常。
        if (Root.XamlRoot is null)
        {
            return;
        }

        NativeMethods.SetForegroundWindow(_hwnd);

        NativeMethods.GetCursorPos(out var cursor);
        NativeMethods.ScreenToClient(_hwnd, ref cursor);
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;

        var flyout = new MenuFlyout();

        var showItem = new MenuFlyoutItem
        {
            Text = "显示待办",
            Icon = new FontIcon { Glyph = "" },
        };
        showItem.Click += (_, _) => ShowFromTray();

        var addItem = new MenuFlyoutItem
        {
            Text = "添加待办",
            Icon = new FontIcon { Glyph = "" },
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
            Icon = new FontIcon { Glyph = "" },
        };
        startupItem.Click += (_, _) => StartupService.SetEnabled(startupItem.IsChecked);

        var exitItem = new MenuFlyoutItem
        {
            Text = "退出 ToDooo",
            Icon = new FontIcon { Glyph = "" },
        };
        exitItem.Click += (_, _) => ExitApp();

        flyout.Items.Add(showItem);
        flyout.Items.Add(addItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(startupItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(exitItem);

        flyout.ShowAt(Root, new FlyoutShowOptions
        {
            Position = new Windows.Foundation.Point(cursor.X / scale, cursor.Y / scale),
        });
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
        => ViewModel.SetSearch(SearchBox.Text);

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

    private async void ExportMenuItem_Click(object sender, RoutedEventArgs e)
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
                if ((wParam.ToInt64() & 0xFFFF) != 0)
                {
                    _dispatcher.TryEnqueue(BedToDesktop);
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

            case NativeMethods.WM_NCHITTEST:
                return HitTestResizeBorder(hWnd, msg, wParam, lParam);

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

            case NativeMethods.WM_EXITSIZEMOVE:
                SavePosition();
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

    /// <summary>
    /// 无标题栏窗口的系统尺寸边框命中区很窄，这里再给客户区最外圈 6px 做一次边缘/角落判定，
    /// 保证四条边和四个角都能拖动缩放；非边缘区域交回系统原来的命中结果。
    /// </summary>
    private IntPtr HitTestResizeBorder(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var hit = NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
        if (hit.ToInt64() != NativeMethods.HTCLIENT ||
            !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return hit;
        }

        // lParam 低/高 16 位是鼠标的屏幕坐标，副屏可能为负，用 short 还原符号。
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        const int edge = 6;

        var left = x - rect.Left < edge;
        var right = rect.Right - x <= edge;
        var top = y - rect.Top < edge;
        var bottom = rect.Bottom - y <= edge;

        var code = (left, right, top, bottom) switch
        {
            (true, _, true, _) => NativeMethods.HTTOPLEFT,
            (_, true, true, _) => NativeMethods.HTTOPRIGHT,
            (true, _, _, true) => NativeMethods.HTBOTTOMLEFT,
            (_, true, _, true) => NativeMethods.HTBOTTOMRIGHT,
            (true, _, _, _) => NativeMethods.HTLEFT,
            (_, true, _, _) => NativeMethods.HTRIGHT,
            (_, _, true, _) => NativeMethods.HTTOP,
            (_, _, _, true) => NativeMethods.HTBOTTOM,
            _ => NativeMethods.HTCLIENT,
        };

        return code == NativeMethods.HTCLIENT ? hit : new IntPtr(code);
    }

    private void DragRegion_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
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
            _hwnd, _pinned ? NativeMethods.HWND_BOTTOM : NativeMethods.HWND_TOPMOST,
            screenX, screenY, width, height, NativeMethods.SWP_NOACTIVATE);
    }

    private void BuildTagDots()
    {
        var style = (Style)Application.Current.Resources["TagDotButtonStyle"];
        for (var i = 0; i < MainViewModel.TagColors.Length; i++)
        {
            var hex = MainViewModel.TagColors[i];
            // DeskBox 的颜色筛选是短横条，选中态用一圈中性描边表示。
            var swatch = new Border
            {
                Width = 14,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(
                    255,
                    Convert.ToByte(hex.Substring(1, 2), 16),
                    Convert.ToByte(hex.Substring(3, 2), 16),
                    Convert.ToByte(hex.Substring(5, 2), 16))),
            };

            var ring = new Border
            {
                Width = 20,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                Child = swatch,
            };

            var button = new Button
            {
                Style = style,
                Content = ring,
                Tag = i,
            };
            button.Click += TagDot_Click;

            _tagRings.Add(ring);
            TagPanel.Children.Add(button);
        }
    }

    private void TagDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            ViewModel.ToggleTag(index);
            UpdateTagVisuals();
        }
    }

    private void UpdateTagVisuals()
    {
        for (var i = 0; i < _tagRings.Count; i++)
        {
            ((SolidColorBrush)_tagRings[i].BorderBrush).Color = ViewModel.SelectedTag == i
                ? Color.FromArgb(255, 233, 236, 240)
                : Color.FromArgb(0, 0, 0, 0);
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
    }

    // 分段筛选的选中态是一块中性高亮，而不是强调色或透明度差异。
    private static void SetStatVisual(Button button, bool active)
    {
        var resources = Application.Current.Resources;
        button.Background = (Brush)resources[active ? "SegmentSelectedBrush" : "TransparentBrush"];
        button.Foreground = (Brush)resources[active ? "TextPrimaryBrush" : "TextMutedBrush"];
    }

    private void AllFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetFilter(TodoFilter.All);
        UpdateFilterVisuals();
    }

    private void ActiveFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetFilter(TodoFilter.Active);
        UpdateFilterVisuals();
    }

    private void ImportantFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetFilter(TodoFilter.Important);
        UpdateFilterVisuals();
    }

    private void DoneFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetFilter(TodoFilter.Done);
        UpdateFilterVisuals();
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
        var dialog = new TodoEditorDialog(initialDue is null ? draft : cleanedDraft, initialDue);

        if (await ShowEditorAsync(dialog))
        {
            ViewModel.AddItem(new TodoItem
            {
                Title = dialog.TodoTitle,
                DueAt = dialog.DueAt,
                Recurrence = dialog.Recurrence,
                IsImportant = dialog.IsImportant,
                TagIndex = dialog.TagIndex,
            });
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

    private void TodoList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TodoItem item)
        {
            _ = EditItemAsync(item);
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

    private void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
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
            if (_menuItem is not null && ((MenuFlyout)sender).Items.Count > 1 &&
                ((MenuFlyout)sender).Items[1] is MenuFlyoutItem important)
            {
                important.Text = _menuItem.ImportantMenuText;
            }
        }
    }

    private void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            _ = EditItemAsync(item);
        }
    }

    private void RowImportant_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            ViewModel.ToggleImportant(item);
            UpdateFilterVisuals();
        }
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            ViewModel.Delete(item);
            UpdateFilterVisuals();
            ShowUndo($"已删除「{item.Title}」", new List<TodoItem> { item });
        }
    }

    private async Task EditItemAsync(TodoItem item)
    {
        var dialog = new TodoEditorDialog(item);

        if (await ShowEditorAsync(dialog))
        {
            ViewModel.ApplyEdit(
                item, dialog.TodoTitle, dialog.DueAt, dialog.Recurrence, dialog.IsImportant, dialog.TagIndex);
            UpdateFilterVisuals();
        }
    }

    private void TodoList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var ordered = ViewModel.Items.ToList();
        DispatcherQueue.TryEnqueue(() => ViewModel.PersistReorder(ordered));
    }

    private void MoreFlyout_Opening(object sender, object e)
    {
        PinMenuItem.IsChecked = _pinned;
        StartupMenuItem.IsChecked = StartupService.IsEnabled;
    }

    private void PinMenuItem_Click(object sender, RoutedEventArgs e)
        => ApplyPin(PinMenuItem.IsChecked, persist: true);

    private void StartupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StartupService.SetEnabled(StartupMenuItem.IsChecked);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => ExitApp();
}
