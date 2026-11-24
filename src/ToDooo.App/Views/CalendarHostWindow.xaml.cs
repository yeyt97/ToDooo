using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using ToDooo.Helpers;
using ToDooo.Interop;
using ToDooo.Models;
using ToDooo.Services;
using ToDooo.ViewModels;
using Windows.Graphics;

namespace ToDooo.Views;

/// <summary>贴在主窗口一侧的独立亚克力日历工具窗，保持主窗尺寸与视觉零抖动。</summary>
public sealed partial class CalendarHostWindow : Window
{
    private const int MinLogicalWidth = 580;
    private const int MinCollapsedLogicalWidth = 400;
    private const int MinLogicalHeight = 460;

    private readonly SettingsService _settings;
    private readonly Action _collapseRequested;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _ownerHwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;
    private bool _forceClosing;
    private bool _slideFromRight = true;

    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;

    private int _resizeHit;
    private NativeMethods.POINT _resizeStartCursor;
    private NativeMethods.RECT _resizeStartRect;

    public CalendarHostWindow(
        MainViewModel viewModel,
        SettingsService settings,
        CalendarSyncService calendarSync,
        IntPtr ownerHwnd,
        Action<TodoItem> editRequested,
        Action<TodoItem, DateTime> rescheduleRequested,
        Action<DateTime> createRequested,
        Action collapseRequested)
    {
        _settings = settings;
        _collapseRequested = collapseRequested;
        _ownerHwnd = ownerHwnd;

        InitializeComponent();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow(ownerHwnd);

        ElementCompositionPreview.SetIsTranslationEnabled(CalendarPane, true);
        var hostVisual = ElementCompositionPreview.GetElementVisual(ContentHost);
        hostVisual.Clip = hostVisual.Compositor.CreateInsetClip();

        CalendarPane.Attach(
            viewModel,
            settings,
            editRequested,
            rescheduleRequested,
            createRequested,
            shape => Root.SetCursor(shape),
            calendarSync,
            evt => _ = CalendarPane.ShowEventDetailAsync(evt));

        CalendarPane.HeaderDragPressed += HeaderDragPressed;
        CalendarPane.HeaderDragMoved += HeaderDragMoved;
        CalendarPane.HeaderDragReleased += HeaderDragReleased;
        CalendarPane.HeaderDragCaptureLost += HeaderDragCaptureLost;
        CalendarPane.RightPanelCollapsedChanged += OnDrawerCollapsedChanged;
        CalendarPane.SetCloseRequested(collapseRequested);
        CalendarPane.IsHostTopmost = () =>
            (NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64()
                & NativeMethods.WS_EX_TOPMOST) != 0;

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

        _appWindow.Closing += (_, args) =>
        {
            if (_forceClosing)
            {
                return;
            }

            args.Cancel = true;
            _collapseRequested();
        };

        Closed += (_, _) =>
        {
            WindowCloak.Cancel(_hwnd, reveal: false);
            WindowAcrylic.DetachAndDispose(ref _acrylic, ref _backdropConfig);
        };
    }

    public CalendarWindow Calendar => CalendarPane;

    public AppWindow WindowInfo => _appWindow;

    public IntPtr Hwnd => _hwnd;

    public bool IsDraggingOrResizing => _dragging || _resizeHit != 0;

    public event EventHandler? DockPointerEntered;
    public event EventHandler? DockPointerExited;
    public event EventHandler? DockPointerPressed;
    public event EventHandler? DockDragReleased;

    public void RefreshTheme()
    {
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);
        ApplyWindowMaterial();
        CalendarPane.RefreshTheme();
    }

    public void ShowNextTo(AppWindow owner, Action? onShown = null)
    {
        var scale = DpiScale.ForOwner(owner, _hwnd);
        var ownerHwnd = Win32Interop.GetWindowFromWindowId(owner.Id);
        NativeMethods.RECT ownerRect;
        if (!NativeMethods.GetWindowRect(ownerHwnd, out ownerRect))
        {
            var fallback = DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Primary).WorkArea;
            ownerRect = new NativeMethods.RECT
            {
                Left = fallback.X + fallback.Width - (int)Math.Round(340 * scale),
                Top = fallback.Y + 40,
                Right = fallback.X + fallback.Width,
                Bottom = fallback.Y + fallback.Height - 40,
            };
        }

        var area = DisplayArea.GetFromPoint(
            new PointInt32((ownerRect.Left + ownerRect.Right) / 2, ownerRect.Top + 20),
            DisplayAreaFallback.Nearest).WorkArea;

        var logicalWidth = CalendarPane.IsRightPanelCollapsed
            ? Math.Max(MinCollapsedLogicalWidth, _settings.Current.CalendarWidth - CalendarPane.CurrentDrawerWidthLogical)
            : Math.Max(MinLogicalWidth, _settings.Current.CalendarWidth);
        var width = (int)Math.Round(logicalWidth * scale);
        var ownerHeight = ownerRect.Bottom - ownerRect.Top;
        var minH = (int)Math.Round(MinLogicalHeight * scale);
        var height = Math.Clamp(
            ownerHeight > 0 ? ownerHeight : (int)Math.Round(Math.Max(MinLogicalHeight, _settings.Current.CalendarHeight) * scale),
            Math.Min(minH, area.Height),
            area.Height);
        int x;
        int y;
        bool placedOnLeft;
        var savedX = _settings.Current.CalendarX;
        var savedY = _settings.Current.CalendarY;
        if (savedX is { } sx && savedY is { } sy && IsPointOnAnyDisplay(sx + 40, sy + 20))
        {
            // 两个窗口不再捆绑：打开时回到上次的位置，而不是重新贴到主窗口旁。
            x = sx;
            y = sy;
            placedOnLeft = false;
        }
        else
        {
            (x, y, placedOnLeft) = WindowSnap.PlaceNextTo(_hwnd, owner, width, height, preferLeft: false, allowOffscreen: true);
        }
        // 只摆放日历自己，绝不移动主窗口。

        _slideFromRight = placedOnLeft;

        var calVisual = ElementCompositionPreview.GetElementVisual(CalendarPane);
        calVisual.StopAnimation("Translation");
        calVisual.StopAnimation("Opacity");
        calVisual.Opacity = 0f;
        calVisual.Properties.InsertVector3("Translation", new Vector3(_slideFromRight ? 36f : -36f, 0f, 0f));

        CalendarPane.ResetLayoutCache();
        CalendarPane.ApplyThemeOnly();

        WindowCloak.ShowCloaked(
            _hwnd,
            Root,
            DispatcherQueue,
            () =>
            {
                // 日历窗口不置顶：点回待办窗口时它应当能被盖住。
                NativeMethods.SetWindowPos(
                    _hwnd,
                    NativeMethods.HWND_NOTOPMOST,
                    x, y, width, height,
                    NativeMethods.SWP_NOACTIVATE);
                _appWindow.Show();
                Activate();
            },
            onRevealed: () =>
            {
                CalendarPane.EnsureRefreshed();
                AnimateSlideIn(onShown);
            });
    }

    public void HideAnimated(Action? onHidden = null)
    {
        SaveCalendarSize();
        var calVisual = ElementCompositionPreview.GetElementVisual(CalendarPane);
        var compositor = calVisual.Compositor;

        var ease = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.4f, 0f),
            new Vector2(1f, 1f));

        var slideAnim = compositor.CreateVector3KeyFrameAnimation();
        slideAnim.Duration = TimeSpan.FromMilliseconds(150);
        slideAnim.InsertKeyFrame(0f, Vector3.Zero);
        slideAnim.InsertKeyFrame(1f, new Vector3(_slideFromRight ? 36f : -36f, 0f, 0f), ease);

        var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.Duration = TimeSpan.FromMilliseconds(130);
        fadeAnim.InsertKeyFrame(0f, 1f);
        fadeAnim.InsertKeyFrame(1f, 0f, ease);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_forceClosing)
            {
                return;
            }

            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            calVisual.StopAnimation("Translation");
            calVisual.StopAnimation("Opacity");
            calVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            calVisual.Opacity = 1f;
            onHidden?.Invoke();
        });

        calVisual.StartAnimation("Translation", slideAnim);
        calVisual.StartAnimation("Opacity", fadeAnim);
        batch.End();
    }

    public void HideImmediate()
    {
        SaveCalendarSize();
        WindowCloak.Cancel(_hwnd, reveal: false);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    }

    public void ForceClose()
    {
        _forceClosing = true;
        WindowCloak.Cancel(_hwnd, reveal: false);
        Close();
    }

    private void AnimateSlideIn(Action? onCompleted)
    {
        var calVisual = ElementCompositionPreview.GetElementVisual(CalendarPane);
        var compositor = calVisual.Compositor;

        var ease = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));

        var slideAnim = compositor.CreateVector3KeyFrameAnimation();
        slideAnim.Duration = TimeSpan.FromMilliseconds(200);
        slideAnim.InsertKeyFrame(0f, new Vector3(_slideFromRight ? 36f : -36f, 0f, 0f));
        slideAnim.InsertKeyFrame(1f, Vector3.Zero, ease);

        var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.Duration = TimeSpan.FromMilliseconds(160);
        fadeAnim.InsertKeyFrame(0f, 0f);
        fadeAnim.InsertKeyFrame(1f, 1f, ease);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() => onCompleted?.Invoke());

        calVisual.StartAnimation("Translation", slideAnim);
        calVisual.StartAnimation("Opacity", fadeAnim);
        batch.End();
    }

    private void ConfigureWindow(IntPtr ownerHwnd)
    {
        _appWindow.Title = "日历";
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

        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        // 不把日历设成待办窗口的“拥有窗口”：被拥有的窗口在 Windows 里永远浮在拥有者之上，
        // 那样任何 z 序调整都无效，点待办窗口时日历也会一直压在上面。

        var round = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var noBorder = NativeMethods.DWMWA_COLOR_NONE;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

        ExtendsContentIntoTitleBar = false;
        ApplyWindowMaterial();
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

    /// <summary>记住日历窗口位置，下次打开回到原处。</summary>
    public void SaveCalendarPosition()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        _settings.Current.CalendarX = rect.Left;
        _settings.Current.CalendarY = rect.Top;
        _settings.Save();
    }

    /// <summary>
    /// 判断坐标是否落在虚拟桌面范围内。这里用 GetSystemMetrics 而不是 DisplayArea.FindAll：
    /// 后者在当前非打包环境下枚举会抛 InvalidCastException，导致打开日历直接闪退。
    /// </summary>
    private static bool IsPointOnAnyDisplay(int x, int y)
    {
        var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        return x >= left && x < left + width && y >= top && y < top + height;
    }

    public void SaveCalendarSize()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var width = (int)Math.Round((rect.Right - rect.Left) / scale);
        var height = (int)Math.Round((rect.Bottom - rect.Top) / scale);
        var minLogicalW = CalendarPane.IsRightPanelCollapsed ? MinCollapsedLogicalWidth : MinLogicalWidth;
        if (width >= minLogicalW && height >= MinLogicalHeight)
        {
            var fullWidth = CalendarPane.IsRightPanelCollapsed
                ? width + (int)Math.Round(CalendarPane.CurrentDrawerWidthLogical)
                : width;
            _settings.Current.CalendarWidth = Math.Max(MinLogicalWidth, fullWidth);
            _settings.Current.CalendarHeight = height;
            _settings.Save();
        }
    }

    private void OnDrawerCollapsedChanged(object? sender, bool collapsed)
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var drawerPx = (int)Math.Round(CalendarPane.CurrentDrawerWidthLogical * scale);
        var height = rect.Bottom - rect.Top;
        var currentW = rect.Right - rect.Left;

        // 抽屉在窗口左侧：保持左缘和上缘不动，只向右伸缩宽度，窗口绝不整体平移。
        var minW = (int)Math.Round((collapsed ? MinCollapsedLogicalWidth : MinLogicalWidth) * scale);
        var targetW = collapsed
            ? Math.Max(minW, currentW - drawerPx)
            : Math.Max(minW, currentW + drawerPx);

        ResizeForDrawer(rect.Left, rect.Top, targetW, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 抽屉开合的统一收口：左右两侧走完全相同的路径。
    /// 曾经给左缘移动单独加过 SWP_NOCOPYBITS，那会强制整窗重绘，正是左侧闪的来源。
    /// </summary>
    private void ResizeForDrawer(int left, int top, int width, int height, uint flags)
    {
        var moved = NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, left, top, width, height, flags);
        TraceDrawerWindowRect($"resize-return moved={moved}");

        // 用改完之后的客户区宽度作为「布局已到位」的目标值。
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var clientWidth = NativeMethods.GetClientRect(_hwnd, out var client)
            ? (client.Right - client.Left) / scale
            : width / scale;
        CalendarPane.AwaitDrawerResize(clientWidth);
        SaveCalendarSize();
    }

    private void TraceDrawerWindowRect(string stage)
    {
        if (NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            CalendarPane.TraceDrawerToggle($"{stage} rect={rect.Left},{rect.Top},{rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
        }
    }

    private void HeaderDragPressed(object? sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed ||
            !NativeMethods.GetCursorPos(out _dragStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _dragStartRect))
        {
            return;
        }

        // 标题栏压在最上面的 8px 上：按下点落在缩放边缘时要去缩放，不能开始拖窗口。
        // 这一步在根节点之前触发并会捕获指针，不在这里分流的话顶边和上面两个角永远拖不动。
        var hit = EdgeHit(e.GetCurrentPoint(Root).Position);
        if (hit != 0)
        {
            _resizeHit = hit;
            _resizeStartCursor = _dragStartCursor;
            _resizeStartRect = _dragStartRect;
            Root.CapturePointer(e.Pointer);
            UpdateResizeCursor(e.GetCurrentPoint(Root).Position);
            e.Handled = true;
            return;
        }

        _dragging = Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void HeaderDragMoved(object? sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var width = _dragStartRect.Right - _dragStartRect.Left;
        var height = _dragStartRect.Bottom - _dragStartRect.Top;
        var candidateX = _dragStartRect.Left + cursor.X - _dragStartCursor.X;
        var candidateY = _dragStartRect.Top + cursor.Y - _dragStartCursor.Y;

        // 窗口之间互不捆绑：拖动只保留屏幕边缘吸附，不吸附主窗口。
        // 允许把日历拖到屏幕外（多屏或临时遮挡时有用），只保留屏幕边缘吸附。
        var (x, y) = WindowSnap.SnapDragged(
            _hwnd, candidateX, candidateY, width, height, IntPtr.Zero, allowOffscreen: true);
        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        e.Handled = true;
    }

    private void HeaderDragReleased(object? sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);
        SaveCalendarPosition();
        e.Handled = true;
    }

    private void HeaderDragCaptureLost(object? sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        UpdateResizeCursor(e.GetCurrentPoint(Root).Position);
        DockPointerEntered?.Invoke(this, EventArgs.Empty);
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHit == 0)
        {
            Root.SetCursor(null);
        }

        DockPointerExited?.Invoke(this, EventArgs.Empty);
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        DockPointerPressed?.Invoke(this, EventArgs.Empty);
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
        if (_dragging)
        {
            HeaderDragMoved(sender, e);
            return;
        }

        if (_resizeHit == 0)
        {
            UpdateResizeCursor(local);
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            var minLogicalW = CalendarPane.IsRightPanelCollapsed ? MinCollapsedLogicalWidth : MinLogicalWidth;
            var minWidth = (int)Math.Round(minLogicalW * scale);
            var minHeight = (int)Math.Round(MinLogicalHeight * scale);
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
        if (_dragging)
        {
            HeaderDragReleased(sender, e);
            return;
        }

        if (_resizeHit == 0)
        {
            return;
        }

        _resizeHit = 0;
        Root.ReleasePointerCapture(e.Pointer);
        SaveCalendarSize();
        UpdateResizeCursor(e.GetCurrentPoint(Root).Position);
        e.Handled = true;
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        if (_resizeHit == 0)
        {
            return;
        }

        _resizeHit = 0;
        SaveCalendarSize();
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

    private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _collapseRequested();
    }
}
