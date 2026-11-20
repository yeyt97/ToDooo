using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ToDooo.Helpers;
using ToDooo.Interop;
using ToDooo.Services;
using Windows.Graphics;

namespace ToDooo.Views;

/// <summary>子任务编辑窗口：设置名称、备注、截止时间与完成状态。</summary>
public sealed partial class SubtaskEditorDialog : Window
{
    private const int LogicalWidth = 400;
    private const int CollapsedHeight = 368;
    private const int ExpandedHeight = 458;

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private bool _finished;
    private bool _ready;
    private bool _dueTouched;
    private bool _saved;

    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;

    public SubtaskEditorDialog(
        SettingsService settings,
        string title,
        string note,
        DateTime? dueAt,
        bool isDone)
    {
        InitializeComponent();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        var dark = Appearance.IsDark((ThemeMode)settings.Current.ThemeMode);
        _acrylic = WindowAcrylic.Apply(
            this, Appearance.TintForOpacity(settings.Current.WindowOpacity), dark);
        var darkFlag = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        TitleBox.Text = title ?? string.Empty;
        NoteBox.Text = note ?? string.Empty;
        DoneToggle.IsOn = isDone;
        DueTime.SelectedTime = RoundToFiveMinutes(DateTime.Now.TimeOfDay);

        if (dueAt is { } due)
        {
            DueToggle.IsOn = true;
            DueDate.Date = new DateTimeOffset(due.Date);
            DueTime.SelectedTime = due.TimeOfDay;
        }

        ApplyDueVisibility();
        _ready = true;
        Closed += OnClosed;
    }

    public Task<bool> Result => _result.Task;

    public string SubtaskTitle => TitleBox.Text.Trim();

    public string SubtaskNote => NoteBox.Text.Trim();

    public bool IsDone => DoneToggle.IsOn;

    public DateTime? DueAt
    {
        get
        {
            if (!DueToggle.IsOn || DueDate.Date is not { } date)
            {
                return null;
            }

            return date.Date + (DueTime.SelectedTime ?? TimeSpan.Zero);
        }
    }

    public void ShowOver(AppWindow? owner)
    {
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
        _appWindow.Title = "编辑子任务";
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
        HeaderBar.Padding = new Thickness(18, 0, 10, 0);
    }

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

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _finished = true;
        WindowCloak.Cancel(_hwnd, reveal: false);
        _result.TrySetResult(_saved);
        _acrylic = null;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => Finish(true);

    private void TitleBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
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

        if (saved && SubtaskTitle.Length == 0)
        {
            TitleBox.Focus(FocusState.Programmatic);
            return;
        }

        _saved = saved;
        _finished = true;
        Close();
    }

    private void DueToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (DueToggle.IsOn)
        {
            if (DueDate.Date is null)
            {
                DueDate.Date = DateTimeOffset.Now.Date;
            }

            if (!_dueTouched && DueTime.SelectedTime is null)
            {
                DueTime.SelectedTime = RoundToFiveMinutes(DateTime.Now.TimeOfDay);
            }
        }

        _dueTouched = true;
        ApplyDueVisibility();
        if (_ready)
        {
            ApplyLogicalSize(NativeMethods.GetDpiForWindow(_hwnd) / 96.0);
        }
    }

    private void ApplyDueVisibility() =>
        DueSection.Visibility = DueToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyLogicalSize(double scale)
    {
        var height = DueToggle.IsOn ? ExpandedHeight : CollapsedHeight;
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalWidth * scale),
            (int)Math.Round(height * scale)));
    }

    private static TimeSpan RoundToFiveMinutes(TimeSpan value)
    {
        var minutes = (int)Math.Round(value.TotalMinutes / 5.0) * 5;
        return TimeSpan.FromMinutes(minutes % (24 * 60));
    }
}
