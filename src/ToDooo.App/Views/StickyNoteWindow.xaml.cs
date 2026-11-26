using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ToDooo.Interop;
using ToDooo.Models;
using ToDooo.Services;
using Windows.Graphics;
using Windows.UI;
using WinRT;

namespace ToDooo.Views;

/// <summary>
/// 独立悬浮纯文本便签窗：无边框 WinUI 3 圆角毛玻璃窗体，6 色明暗双套色板，
/// 支持置顶、自由拖拽缩放、独立毛玻璃透明度、300ms 防抖自动落盘。
/// </summary>
public sealed partial class StickyNoteWindow : Window
{
    private readonly StickyNote _note;
    private readonly StickyNoteStore _store;
    private readonly SettingsService _settings;
    private readonly Action<StickyNote> _onCreateNew;
    private readonly Action _onOpenManager;
    private readonly Action<string, string> _onConvertToTodo;
    private readonly Action<StickyNote> _onDelete;
    private readonly Action<string> _onClosed;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;
    private bool _dirty;
    private bool _closing;

    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;

    private bool _isTopDocked;
    private bool _isCollapsed;
    private int _dockedWorkTop;
    private int _leaveTicks;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dockPollTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slideTimer;
    private int _slideStartY;
    private int _slideTargetY;
    private long _slideStartTicks;
    private const double SlideDurationMs = 180.0;

    public string NoteId => _note.Id;

    public StickyNoteWindow(
        StickyNote note,
        StickyNoteStore store,
        SettingsService settings,
        Action<StickyNote> onCreateNew,
        Action onOpenManager,
        Action<string, string> onConvertToTodo,
        Action<StickyNote> onDelete,
        Action<string> onClosed)
    {
        _note = note;
        _store = store;
        _settings = settings;
        _onCreateNew = onCreateNew;
        _onOpenManager = onOpenManager;
        _onConvertToTodo = onConvertToTodo;
        _onDelete = onDelete;
        _onClosed = onClosed;

        InitializeComponent();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        BuildColorSwatches();
        ApplyVisualTheme();

        EditorBox.FontSize = Math.Clamp(_note.FontSize, 11.0, 22.0);
        EditorBox.Text = _note.Content ?? string.Empty;
        UpdateWordCount();

        Activated += (_, e) =>
            HeaderBar.Opacity = e.WindowActivationState == WindowActivationState.Deactivated ? 0.45 : 1.0;
        HeaderBar.PointerEntered += (_, _) => HeaderBar.Opacity = 1.0;

        _appWindow.Changed += AppWindow_Changed;
        Closed += StickyNoteWindow_Closed;
    }

    public void RefreshTheme()
    {
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode);
        ApplyVisualTheme();
        if (!_settings.Current.StickyAutoDockTop && _isTopDocked)
        {
            ExitTopDock(restorePosition: true);
        }
    }

    public void RefreshPinState()
    {
        if (!_isTopDocked && _appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _note.Pinned;
        }

        ApplyVisualTheme();
    }

    public void FocusEditor()
    {
        Activate();
        EditorBox.Focus(FocusState.Programmatic);
        EditorBox.SelectionStart = EditorBox.Text.Length;
    }

    /// <summary>在不抢占当前窗口/浮层焦点的前提下，将便签提到最前（若已贴边收起则一并展开）。</summary>
    public void BringToFrontNoActivate()
    {
        if (_isTopDocked && _isCollapsed)
        {
            _isCollapsed = false;
            _leaveTicks = 0;
            AnimateWindowTop(_dockedWorkTop);
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            new IntPtr(0), // HWND_TOP
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void ConfigureWindow()
    {
        _appWindow.Title = "便签";
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = _note.Pinned;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var w = Math.Max(240, (int)Math.Round(_note.Width * scale));
        var h = Math.Max(220, (int)Math.Round(_note.Height * scale));

        if (_note.X is int x && _note.Y is int y)
        {
            // 按保存位置定位最近可用显示器：屏幕拔掉或分辨率/DPI 变化时自动夹取回可见工作区。
            var work = DisplayArea.GetFromPoint(
                new PointInt32(x + w / 2, y + h / 2),
                DisplayAreaFallback.Nearest).WorkArea;
            w = Math.Min(w, work.Width);
            h = Math.Min(h, work.Height);
            _appWindow.Resize(new SizeInt32(w, h));

            var clampedX = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - w));
            var clampedY = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - h));
            _appWindow.Move(new PointInt32(clampedX, clampedY));
        }
        else
        {
            var work = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            w = Math.Min(w, work.Width);
            h = Math.Min(h, work.Height);
            _appWindow.Resize(new SizeInt32(w, h));

            var cx = work.X + Math.Max(40, (work.Width - w) / 2);
            var cy = work.Y + Math.Max(40, (work.Height - h) / 3);
            _appWindow.Move(new PointInt32(cx, cy));
        }

        var preference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private void BuildColorSwatches()
    {
        ColorSwatchPanel.Children.Clear();
        foreach (var theme in StickyColorTheme.All)
        {
            var isSelected = string.Equals(theme.Key, _note.Color, StringComparison.OrdinalIgnoreCase);
            var dot = new Grid { Width = 22, Height = 22 };
            dot.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(theme.Swatch),
                BorderBrush = isSelected
                    ? new SolidColorBrush(Colors.White)
                    : new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
            });

            if (isSelected)
            {
                dot.Children.Add(new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var btn = new Button
            {
                Width = 26,
                Height = 26,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Content = dot,
            };
            ToolTipService.SetToolTip(btn, theme.Name);
            btn.Click += (_, _) =>
            {
                _note.Color = theme.Key;
                _store.SaveMeta();
                BuildColorSwatches();
                ApplyVisualTheme();
                ColorButton.Flyout?.Hide();
            };
            ColorSwatchPanel.Children.Add(btn);
        }
    }

    private void ApplyVisualTheme()
    {
        var dark = Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        var palette = StickyColorTheme.Resolve(_note.Color);
        var surfaceColor = dark ? palette.DarkSurface : palette.LightSurface;

        if (_settings.Current.StickyAcrylicEnabled && DesktopAcrylicController.IsSupported())
        {
            var tint = Color.FromArgb(255, surfaceColor.R, surfaceColor.G, surfaceColor.B);
            var tintOpacity = (float)Math.Clamp(_settings.Current.StickyOpacity, 0.1, 1.0);
            var effectiveTint = dark ? Math.Max(tintOpacity, 0.24f) : tintOpacity;
            var luminosity = Math.Clamp((dark ? 0.08f : 0.16f) + effectiveTint * 0.85f, dark ? 0.15f : 0.25f, 0.96f);

            if (_acrylic is null)
            {
                _backdropConfig = new SystemBackdropConfiguration
                {
                    IsInputActive = true,
                    Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
                };
                _acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
                _acrylic.AddSystemBackdropTarget(
                    ((object)this).As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
                _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
            }
            else if (_backdropConfig is not null)
            {
                _backdropConfig.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
            }

            _acrylic.TintColor = tint;
            _acrylic.FallbackColor = tint;
            _acrylic.TintOpacity = effectiveTint;
            _acrylic.LuminosityOpacity = luminosity;

            SurfaceBorder.Background = new SolidColorBrush(Colors.Transparent);
        }
        else
        {
            WindowAcrylic.DetachAndDispose(ref _acrylic, ref _backdropConfig);
            SurfaceBorder.Background = new SolidColorBrush(Color.FromArgb(255, surfaceColor.R, surfaceColor.G, surfaceColor.B));
        }

        ColorIndicatorDot.Background = new SolidColorBrush(palette.Swatch);

        PinIcon.Glyph = _note.Pinned ? "\uE840" : "\uE718";
        var pinBrush = _note.Pinned
            ? new SolidColorBrush(palette.Swatch)
            : (Brush)Application.Current.Resources["TextSecondaryBrush"];
        PinButton.Foreground = pinBrush;
        PinIcon.Foreground = pinBrush;
        ToolTipService.SetToolTip(PinButton, _note.Pinned ? "取消置顶" : "置顶");
    }

    private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _note.Content = EditorBox.Text;
        _dirty = true;
        UpdateWordCount();

        _saveTimer ??= DispatcherQueue.CreateTimer();
        _saveTimer.Stop();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick -= SaveTimer_Tick;
        _saveTimer.Tick += SaveTimer_Tick;
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        FlushSave();
    }

    private void FlushSave()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        _store.SaveContent(_note);
        if (!_closing)
        {
            ShowStatus("已保存");
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Opacity = 0.85;

        _statusTimer ??= DispatcherQueue.CreateTimer();
        _statusTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromSeconds(1.8);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick -= StatusTimer_Tick;
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
    }

    private void StatusTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        StatusText.Opacity = 0;
    }

    private void AdjustFontSize(double delta)
    {
        var next = Math.Clamp(Math.Round((EditorBox.FontSize + delta) * 2) / 2, 11.0, 22.0);
        EditorBox.FontSize = next;
        _note.FontSize = next;
        _store.SaveMeta(notify: false);
        ShowStatus($"字号 {next:0.#}");
    }

    private void UpdateWordCount()
    {
        var len = EditorBox.Text.Replace("\r", string.Empty).Replace("\n", string.Empty).Length;
        WordCountText.Text = len == 0 ? "空白" : $"{len} 字";
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_closing)
        {
            return;
        }

        if (args.DidPositionChange && !_isCollapsed && _slideTimer?.IsRunning != true)
        {
            _note.X = sender.Position.X;
            _note.Y = sender.Position.Y;
            _store.SaveMeta(notify: false);
        }

        if (args.DidSizeChange)
        {
            var scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(_hwnd) / 96.0);
            _note.Width = Math.Max(240, (int)Math.Round(sender.Size.Width / scale));
            _note.Height = Math.Max(220, (int)Math.Round(sender.Size.Height / scale));
            _store.SaveMeta(notify: false);
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _note.Pinned = !_note.Pinned;
        // 处于顶部收起/滑出状态时强制保持置顶，等窗口移离顶边后再按 _note.Pinned 生效。
        if (!_isTopDocked && _appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _note.Pinned;
        }

        ApplyVisualTheme();
        _store.SaveMeta();
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e) => _onCreateNew(_note);

    private void ConvertToTodo_Click(object sender, RoutedEventArgs e)
    {
        var selected = EditorBox.SelectedText?.Trim();
        var source = !string.IsNullOrEmpty(selected) ? selected : EditorBox.Text;
        var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            ShowStatus("便签为空");
            return;
        }

        var title = lines[0].Trim();
        var note = lines.Length > 1 ? string.Join("\n", lines.Skip(1)).Trim() : string.Empty;
        if (title.Length > 0)
        {
            _onConvertToTodo(title, note);
            ShowStatus("已添加到待办");
        }
    }

    private void OpenManager_Click(object sender, RoutedEventArgs e) => _onOpenManager();

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        _closing = true;
        _saveTimer?.Stop();
        _onDelete(_note);
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EditorBox.Text))
        {
            DeleteNote_Click(sender, e);
            return;
        }

        _note.Visible = false;
        _store.SaveMeta();
        Close();
    }

    private void NewNoteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _onCreateNew(_note);
    }

    private void CloseNoteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CloseButton_Click(this, new RoutedEventArgs());
    }

    private void ZoomInAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        AdjustFontSize(1.0);
    }

    private void ZoomOutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        AdjustFontSize(-1.0);
    }

    private void ZoomResetAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        AdjustFontSize(14.0 - EditorBox.FontSize);
    }

    private void StickyNoteWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        _saveTimer?.Stop();
        _dockPollTimer?.Stop();
        _slideTimer?.Stop();
        FlushSave();
        WindowAcrylic.DetachAndDispose(ref _acrylic, ref _backdropConfig);
        _onClosed(_note.Id);
    }

    private void HeaderBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _slideTimer?.Stop();
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
            EvaluateTopDockAfterDrag();
            return;
        }

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        EvaluateTopDockAfterDrag();
        e.Handled = true;
    }

    private void HeaderBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var wasDragging = _dragging;
        _dragging = false;
        Helpers.WindowDragHelper.CancelTouchDrag(_hwnd);
        if (wasDragging)
        {
            EvaluateTopDockAfterDrag();
        }
    }

    private void EvaluateTopDockAfterDrag()
    {
        if (_closing || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workTop = display.WorkArea.Y;

        if (_settings.Current.StickyAutoDockTop && rect.Top <= workTop + 8)
        {
            EnterTopDock(workTop);
        }
        else if (_isTopDocked && rect.Top > workTop + 12)
        {
            ExitTopDock(restorePosition: false);
        }
    }

    private void EnterTopDock(int workTop)
    {
        _isTopDocked = true;
        _isCollapsed = false;
        _dockedWorkTop = workTop;
        _leaveTicks = 0;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
        }

        SetWindowTop(workTop);

        _dockPollTimer ??= DispatcherQueue.CreateTimer();
        _dockPollTimer.Interval = TimeSpan.FromMilliseconds(80);
        _dockPollTimer.IsRepeating = true;
        _dockPollTimer.Tick -= DockPollTimer_Tick;
        _dockPollTimer.Tick += DockPollTimer_Tick;
        _dockPollTimer.Start();
    }

    private void ExitTopDock(bool restorePosition)
    {
        _isTopDocked = false;
        _dockPollTimer?.Stop();

        if (_isCollapsed && restorePosition)
        {
            _isCollapsed = false;
            AnimateWindowTop(_dockedWorkTop);
        }
        else
        {
            _isCollapsed = false;
            _slideTimer?.Stop();
        }

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _note.Pinned;
        }
    }

    private void DockPollTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_isTopDocked || _dragging || _closing)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var pt) || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        const int PeekStripPx = 6;
        if (_isCollapsed)
        {
            if (pt.X >= rect.Left && pt.X <= rect.Right && pt.Y <= _dockedWorkTop + 10)
            {
                _isCollapsed = false;
                _leaveTicks = 0;
                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = true;
                }

                AnimateWindowTop(_dockedWorkTop);
            }
        }
        else
        {
            var flyoutOpen = ColorButton.Flyout?.IsOpen == true;
            var inside = pt.X >= rect.Left && pt.X <= rect.Right &&
                         pt.Y >= _dockedWorkTop && pt.Y <= rect.Bottom;
            if (inside || flyoutOpen)
            {
                _leaveTicks = 0;
            }
            else
            {
                _leaveTicks++;
                if (_leaveTicks >= 3)
                {
                    _isCollapsed = true;
                    _leaveTicks = 0;
                    var height = rect.Bottom - rect.Top;
                    AnimateWindowTop(_dockedWorkTop - Math.Max(0, height - PeekStripPx));
                }
            }
        }
    }

    private void AnimateWindowTop(int targetY)
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        if (rect.Top == targetY)
        {
            _slideTimer?.Stop();
            return;
        }

        _slideStartY = rect.Top;
        _slideTargetY = targetY;
        _slideStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        _slideTimer ??= DispatcherQueue.CreateTimer();
        _slideTimer.Interval = TimeSpan.FromMilliseconds(10);
        _slideTimer.IsRepeating = true;
        _slideTimer.Tick -= SlideTimer_Tick;
        _slideTimer.Tick += SlideTimer_Tick;
        _slideTimer.Start();
    }

    private void SlideTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_closing || _dragging)
        {
            sender.Stop();
            return;
        }

        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _slideStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var t = Math.Clamp(elapsedMs / SlideDurationMs, 0.0, 1.0);
        // Cubic ease-out：起步快、落点轻柔。
        var eased = 1.0 - Math.Pow(1.0 - t, 3);
        var currentY = (int)Math.Round(_slideStartY + (_slideTargetY - _slideStartY) * eased);
        SetWindowTop(currentY);

        if (t >= 1.0)
        {
            sender.Stop();
        }
    }

    private void SetWindowTop(int y)
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero,
            rect.Left, y,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }
}
