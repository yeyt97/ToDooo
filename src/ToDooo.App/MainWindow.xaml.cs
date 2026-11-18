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
    private bool _dragging;
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
        UpdatePinVisual();

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

        UpdatePinVisual();
        CreateTray();
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
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        ApplyAcrylicMaterial();

        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX
            | NativeMethods.WS_THICKFRAME | NativeMethods.WS_DLGFRAME
            | NativeMethods.WS_CAPTION | NativeMethods.WS_BORDER);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

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
            TintOpacity = 0.65f,
            LuminosityOpacity = 0.6f,
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
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        var x = _settings.Current.WindowX ?? area.X + area.Width - width - (int)(24 * scale);
        var y = _settings.Current.WindowY ?? area.Y + area.Height - height - (int)(28 * scale);
        x = Math.Clamp(x, area.X, area.X + Math.Max(0, area.Width - width));
        y = Math.Clamp(y, area.Y, area.Y + Math.Max(0, area.Height - height));

        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void ApplyCornerMask()
    {
        NativeMethods.SetWindowRgn(_hwnd, IntPtr.Zero, true);
        var round = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
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
        UpdatePinVisual();

        if (persist)
        {
            _settings.Current.PinnedToDesktop = _pinned;
            _settings.Save();
        }
    }

    private void UpdatePinVisual()
    {
        PinGlyph.Foreground = new SolidColorBrush(
            _pinned ? Color.FromArgb(255, 77, 143, 227) : Color.FromArgb(255, 147, 157, 169));
        PinButton.Opacity = _pinned ? 1 : 0.85;
    }

    private void SavePosition()
    {
        if (_reparenting || !_started)
        {
            return;
        }

        NativeMethods.GetWindowRect(_hwnd, out var rect);
        _settings.Current.WindowX = rect.Left;
        _settings.Current.WindowY = rect.Top;
        _settings.Save();
    }

    private void CreateTray()
    {
        _tray = new TrayIcon(_hwnd);
        _tray.ShowRequested += ShowFromTray;
        _tray.AddRequested += () =>
        {
            ShowFromTray();
            ShowAddEditor();
        };
        _tray.ExitRequested += ExitApp;
        _tray.StartupToggled += enabled => StartupService.SetEnabled(enabled);
        _tray.Add();
    }

    private void ExitApp()
    {
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

            case NativeMethods.WM_WINDOWPOSCHANGING:
                // Show Desktop 等外壳操作会试图把窗口隐藏/最小化，钉住时直接拦掉。
                if (_pinned)
                {
                    var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                    if ((pos.flags & NativeMethods.SWP_HIDEWINDOW) != 0)
                    {
                        pos.flags &= ~NativeMethods.SWP_HIDEWINDOW;
                        pos.flags |= NativeMethods.SWP_SHOWWINDOW;
                        System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
                    }
                }

                break;

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

    private void AddButton_Click(object sender, RoutedEventArgs e) => ShowAddEditor();

    private void ShowAddEditor()
    {
        AddButton.Visibility = Visibility.Collapsed;
        AddEditor.Visibility = Visibility.Visible;
        AddTextBox.Focus(FocusState.Programmatic);
    }

    private void CloseAddEditor()
    {
        AddTextBox.Text = string.Empty;
        AddEditor.Visibility = Visibility.Collapsed;
        AddButton.Visibility = Visibility.Visible;
    }

    private void AddTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var title = AddTextBox.Text;
            if (!string.IsNullOrWhiteSpace(title))
            {
                ViewModel.Add(title);
                AddTextBox.Text = string.Empty;
                UpdateFilterVisuals();
                UpdateTagVisuals();
                if (ViewModel.Items.Count > 0)
                {
                    TodoList.ScrollIntoView(ViewModel.Items[0]);
                }
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CloseAddEditor();
            e.Handled = true;
        }
    }

    private void AddTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AddTextBox.Text))
        {
            CloseAddEditor();
        }
    }

    private void DoneToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem item })
        {
            ViewModel.SetDone(item, !item.IsDone);
            UpdateFilterVisuals();
        }
    }

    // 卡片悬停：提亮卡片并浮出快捷操作，和 DeskBox 的行为一致。
    private void TodoCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        card.Background = (Brush)Application.Current.Resources["ItemCardHoverBrush"];
        if (FindTaggedChild<Border>(card, "ItemActions") is { } host)
        {
            host.Opacity = 1;
            host.IsHitTestVisible = true;
        }
    }

    private void TodoCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        card.ClearValue(Border.BackgroundProperty);
        if (FindTaggedChild<Border>(card, "ItemActions") is { } host)
        {
            host.Opacity = 0;
            host.IsHitTestVisible = false;
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

    private void QuickImportant_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem item })
        {
            ViewModel.ToggleImportant(item);
            UpdateFilterVisuals();
        }
    }

    private void QuickEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem item })
        {
            _ = EditItemAsync(item);
        }
    }

    private void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearCompleted();
        UpdateFilterVisuals();
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
        }
    }

    private async Task EditItemAsync(TodoItem item)
    {
        var dialog = new TodoEditorDialog(item)
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.TodoTitle.Length > 0)
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

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyPin(!_pinned, persist: true);
        if (!_pinned)
        {
            Activate();
            NativeMethods.SetForegroundWindow(_hwnd);
        }
    }

    private void MoreFlyout_Opening(object sender, object e)
    {
        StartupMenuItem.IsChecked = StartupService.IsEnabled;
    }

    private void StartupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StartupService.SetEnabled(StartupMenuItem.IsChecked);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => ExitApp();
}
