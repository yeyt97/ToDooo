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
    private readonly Func<IReadOnlyList<IntPtr>>? _extraFollowerProvider;
    private readonly Action? _ownerMoved;
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
    private readonly List<(IntPtr Hwnd, int OffsetX, int OffsetY)> _dragFollowers = new();

    private int _resizeHit;
    private NativeMethods.POINT _resizeStartCursor;
    private NativeMethods.RECT _resizeStartRect;
    private bool _resizeOwnerAttached;
    private NativeMethods.RECT _resizeOwnerStartRect;
    private bool _resizeOwnerOnRight;
    private int _resizeGap;

    public CalendarHostWindow(
        MainViewModel viewModel,
        SettingsService settings,
        IntPtr ownerHwnd,
        Action<TodoItem> editRequested,
        Action<TodoItem, DateTime> rescheduleRequested,
        Action<DateTime> createRequested,
        Action collapseRequested,
        Func<IReadOnlyList<IntPtr>>? extraFollowerProvider = null,
        Action? ownerMoved = null)
    {
        _settings = settings;
        _collapseRequested = collapseRequested;
        _ownerHwnd = ownerHwnd;
        _extraFollowerProvider = extraFollowerProvider;
        _ownerMoved = ownerMoved;

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
            shape => Root.SetCursor(shape));

        CalendarPane.HeaderDragPressed += HeaderDragPressed;
        CalendarPane.HeaderDragMoved += HeaderDragMoved;
        CalendarPane.HeaderDragReleased += HeaderDragReleased;
        CalendarPane.HeaderDragCaptureLost += HeaderDragCaptureLost;
        CalendarPane.RightPanelCollapsedChanged += OnDrawerCollapsedChanged;

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
            _acrylic = null;
        };
    }

    public CalendarWindow Calendar => CalendarPane;

    public AppWindow WindowInfo => _appWindow;

    public IntPtr Hwnd => _hwnd;

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
        var width = Math.Min((int)Math.Round(logicalWidth * scale), area.Width);
        var ownerHeight = ownerRect.Bottom - ownerRect.Top;
        var minH = (int)Math.Round(MinLogicalHeight * scale);
        var height = Math.Clamp(
            ownerHeight > 0 ? ownerHeight : (int)Math.Round(Math.Max(MinLogicalHeight, _settings.Current.CalendarHeight) * scale),
            Math.Min(minH, area.Height),
            area.Height);
        var (x, y, placedOnLeft) = WindowSnap.PlaceNextTo(_hwnd, owner, width, height, preferLeft: true);
        var gapPx = (int)Math.Round(WindowSnap.DefaultGapLogical * scale);
        var ownerW = ownerRect.Right - ownerRect.Left;
        var overlapsOwner = ownerHwnd != IntPtr.Zero &&
            x < ownerRect.Right && x + width > ownerRect.Left &&
            y < ownerRect.Bottom && y + height > ownerRect.Top;
        if (overlapsOwner && width + gapPx + ownerW <= area.Width)
        {
            var newOwnerX = Math.Clamp(area.X + width + gapPx, area.X, area.X + Math.Max(0, area.Width - ownerW));
            var ownerY = Math.Clamp(ownerRect.Top, area.Y, area.Y + Math.Max(0, area.Height - height));
            NativeMethods.SetWindowPos(
                ownerHwnd, IntPtr.Zero, newOwnerX, ownerY, ownerW, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            x = newOwnerX - gapPx - width;
            y = ownerY;
            placedOnLeft = true;
            _ownerMoved?.Invoke();
        }
        else if (ownerHwnd != IntPtr.Zero && ownerHeight < height &&
            NativeMethods.IsWindowVisible(ownerHwnd))
        {
            var ownerY = Math.Clamp(ownerRect.Top, area.Y, area.Y + Math.Max(0, area.Height - height));
            NativeMethods.SetWindowPos(
                ownerHwnd, IntPtr.Zero, ownerRect.Left, ownerY, ownerW, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            y = ownerY;
            _ownerMoved?.Invoke();
        }

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
                NativeMethods.SetWindowPos(
                    _hwnd, IntPtr.Zero, x, y, width, height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
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

        if (ownerHwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, ownerHwnd);
        }

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
                this, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark, _backdropConfig);
            return;
        }

        WindowAcrylic.ApplyTint(_acrylic, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark);
        if (_backdropConfig is not null)
        {
            _backdropConfig.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        }
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
        var currentW = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        var area = DisplayArea.GetFromPoint(
            new PointInt32((rect.Left + rect.Right) / 2, rect.Top + 20),
            DisplayAreaFallback.Nearest).WorkArea;

        var ownerAttached = false;
        var ownerOnRight = true;
        var gap = (int)Math.Round(WindowSnap.DefaultGapLogical * scale);
        NativeMethods.RECT mainRect = default;

        if (_ownerHwnd != IntPtr.Zero &&
            NativeMethods.IsWindowVisible(_ownerHwnd) &&
            NativeMethods.GetWindowRect(_ownerHwnd, out mainRect) &&
            WindowSnap.IsAttachedTo(rect, mainRect, scale))
        {
            ownerAttached = true;
            ownerOnRight = rect.Right <= mainRect.Left + (int)Math.Round(24 * scale);
            gap = ownerOnRight
                ? Math.Max(0, mainRect.Left - rect.Right)
                : Math.Max(0, rect.Left - mainRect.Right);
        }

        var minW = (int)Math.Round((collapsed ? MinCollapsedLogicalWidth : MinLogicalWidth) * scale);
        var maxW = ownerAttached
            ? Math.Max(minW, area.Width - gap - (mainRect.Right - mainRect.Left))
            : area.Width;
        var targetW = collapsed
            ? Math.Max(minW, currentW - drawerPx)
            : Math.Min(maxW, Math.Max(minW, currentW + drawerPx));

        if (!ownerAttached)
        {
            var left = Math.Clamp(rect.Right - targetW, area.X, area.X + Math.Max(0, area.Width - targetW));
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, left, rect.Top, targetW, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            Root.UpdateLayout();
            SaveCalendarSize();
            return;
        }

        var mainW = mainRect.Right - mainRect.Left;
        var mainH = mainRect.Bottom - mainRect.Top;
        int calLeft;
        var newMainLeft = mainRect.Left;
        var moveMain = false;

        if (ownerOnRight)
        {
            calLeft = mainRect.Left - gap - targetW;
            if (calLeft < area.X)
            {
                calLeft = area.X;
                newMainLeft = Math.Clamp(calLeft + targetW + gap, area.X, area.X + Math.Max(0, area.Width - mainW));
                calLeft = newMainLeft - gap - targetW;
                moveMain = true;
            }
        }
        else
        {
            calLeft = mainRect.Right + gap;
            if (calLeft + targetW > area.X + area.Width)
            {
                newMainLeft = Math.Max(area.X, area.X + area.Width - targetW - gap - mainW);
                calLeft = newMainLeft + mainW + gap;
                moveMain = true;
            }
        }

        if (moveMain)
        {
            var hdwp = NativeMethods.BeginDeferWindowPos(2);
            if (hdwp != IntPtr.Zero)
            {
                hdwp = NativeMethods.DeferWindowPos(
                    hdwp, _hwnd, IntPtr.Zero,
                    calLeft, rect.Top, targetW, height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                if (hdwp != IntPtr.Zero)
                {
                    hdwp = NativeMethods.DeferWindowPos(
                        hdwp, _ownerHwnd, IntPtr.Zero,
                        newMainLeft, mainRect.Top, mainW, mainH,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }

                if (hdwp != IntPtr.Zero)
                {
                    NativeMethods.EndDeferWindowPos(hdwp);
                }
            }
            else
            {
                NativeMethods.SetWindowPos(
                    _ownerHwnd, IntPtr.Zero, newMainLeft, mainRect.Top, mainW, mainH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                NativeMethods.SetWindowPos(
                    _hwnd, IntPtr.Zero, calLeft, rect.Top, targetW, height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }

            _ownerMoved?.Invoke();
        }
        else
        {
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, calLeft, rect.Top, targetW, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        Root.UpdateLayout();
        SaveCalendarSize();
    }

    private void HeaderDragPressed(object? sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed ||
            !NativeMethods.GetCursorPos(out _dragStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _dragStartRect))
        {
            return;
        }

        _dragFollowers.Clear();
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (_ownerHwnd != IntPtr.Zero &&
            NativeMethods.IsWindowVisible(_ownerHwnd) &&
            NativeMethods.GetWindowRect(_ownerHwnd, out var ownerRect) &&
            WindowSnap.IsAttachedTo(_dragStartRect, ownerRect, scale))
        {
            _dragFollowers.Add((_ownerHwnd, ownerRect.Left - _dragStartRect.Left, ownerRect.Top - _dragStartRect.Top));

            if (_extraFollowerProvider is not null)
            {
                foreach (var subHwnd in _extraFollowerProvider())
                {
                    if (subHwnd != IntPtr.Zero &&
                        subHwnd != _hwnd &&
                        subHwnd != _ownerHwnd &&
                        NativeMethods.IsWindowVisible(subHwnd) &&
                        NativeMethods.GetWindowRect(subHwnd, out var subRect) &&
                        WindowSnap.IsAttachedTo(subRect, ownerRect, scale))
                    {
                        _dragFollowers.Add((subHwnd, subRect.Left - _dragStartRect.Left, subRect.Top - _dragStartRect.Top));
                    }
                }
            }
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

        if (_dragFollowers.Count == 0)
        {
            var (x, y) = WindowSnap.SnapDragged(
                _hwnd, candidateX, candidateY, width, height, _ownerHwnd);
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, x, y, width, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            e.Handled = true;
            return;
        }

        var hdwp = NativeMethods.BeginDeferWindowPos(1 + _dragFollowers.Count);
        if (hdwp == IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, candidateX, candidateY, width, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            e.Handled = true;
            return;
        }

        hdwp = NativeMethods.DeferWindowPos(
            hdwp, _hwnd, IntPtr.Zero,
            candidateX, candidateY, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        foreach (var (subHwnd, offsetX, offsetY) in _dragFollowers)
        {
            if (hdwp == IntPtr.Zero)
            {
                break;
            }

            if (NativeMethods.IsWindowVisible(subHwnd))
            {
                hdwp = NativeMethods.DeferWindowPos(
                    hdwp, subHwnd, IntPtr.Zero,
                    candidateX + offsetX, candidateY + offsetY, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
        }

        if (hdwp != IntPtr.Zero)
        {
            NativeMethods.EndDeferWindowPos(hdwp);
        }

        e.Handled = true;
    }

    private void HeaderDragReleased(object? sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        var movedOwner = _dragFollowers.Count > 0;
        _dragFollowers.Clear();
        Root.ReleasePointerCapture(e.Pointer);
        if (movedOwner)
        {
            _ownerMoved?.Invoke();
        }

        e.Handled = true;
    }

    private void HeaderDragCaptureLost(object? sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        var movedOwner = _dragFollowers.Count > 0;
        _dragFollowers.Clear();
        if (movedOwner)
        {
            _ownerMoved?.Invoke();
        }
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        UpdateResizeCursor(e.GetCurrentPoint(Root).Position);

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHit == 0)
        {
            Root.SetCursor(null);
        }
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
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

        if (!NativeMethods.GetCursorPos(out _resizeStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _resizeStartRect))
        {
            return;
        }

        _resizeOwnerAttached = false;
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (_ownerHwnd != IntPtr.Zero &&
            NativeMethods.IsWindowVisible(_ownerHwnd) &&
            NativeMethods.GetWindowRect(_ownerHwnd, out _resizeOwnerStartRect) &&
            WindowSnap.IsAttachedTo(_resizeStartRect, _resizeOwnerStartRect, scale))
        {
            _resizeOwnerAttached = true;
            _resizeOwnerOnRight = _resizeStartRect.Right <= _resizeOwnerStartRect.Left + (int)Math.Round(24 * scale);
            _resizeGap = _resizeOwnerOnRight
                ? _resizeOwnerStartRect.Left - _resizeStartRect.Right
                : _resizeStartRect.Left - _resizeOwnerStartRect.Right;
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

            if (!_resizeOwnerAttached || _ownerHwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(_ownerHwnd))
            {
                NativeMethods.SetWindowPos(
                    _hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                e.Handled = true;
                return;
            }

            var ownerW = _resizeOwnerStartRect.Right - _resizeOwnerStartRect.Left;
            var ownerLeft = _resizeOwnerOnRight
                ? right + _resizeGap
                : left - _resizeGap - ownerW;
            var ownerH = bottom - top;

            var hdwp = NativeMethods.BeginDeferWindowPos(2);
            if (hdwp != IntPtr.Zero)
            {
                hdwp = NativeMethods.DeferWindowPos(
                    hdwp, _hwnd, IntPtr.Zero,
                    left, top, right - left, ownerH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                if (hdwp != IntPtr.Zero)
                {
                    hdwp = NativeMethods.DeferWindowPos(
                        hdwp, _ownerHwnd, IntPtr.Zero,
                        ownerLeft, top, ownerW, ownerH,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }

                if (hdwp != IntPtr.Zero)
                {
                    NativeMethods.EndDeferWindowPos(hdwp);
                }
            }
            else
            {
                NativeMethods.SetWindowPos(
                    _hwnd, IntPtr.Zero, left, top, right - left, ownerH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                NativeMethods.SetWindowPos(
                    _ownerHwnd, IntPtr.Zero, ownerLeft, top, ownerW, ownerH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
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

        var syncOwner = _resizeOwnerAttached;
        _resizeHit = 0;
        _resizeOwnerAttached = false;
        Root.ReleasePointerCapture(e.Pointer);
        SaveCalendarSize();
        if (syncOwner)
        {
            _ownerMoved?.Invoke();
        }

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

        var syncOwner = _resizeOwnerAttached;
        _resizeHit = 0;
        _resizeOwnerAttached = false;
        SaveCalendarSize();
        if (syncOwner)
        {
            _ownerMoved?.Invoke();
        }

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
