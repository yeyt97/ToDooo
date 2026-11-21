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
    private const int LogicalWidth = 420;
    private const int LogicalHeight = 640;

    private static readonly int[] LeadOptions = { 0, 5, 15, 30, 60 };

    private readonly SettingsService _settings;
    private readonly Action _settingsChanged;
    private readonly Func<Task> _exportRequested;
    private readonly Func<Task>? _importRequested;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private SystemBackdropConfiguration? _backdropConfig;
    private DesktopAcrylicController? _acrylic;
    private bool _ready;
    private bool _closing;

    public SettingsWindow(
        SettingsService settings,
        Action settingsChanged,
        Func<Task> exportRequested,
        Func<Task>? importRequested = null)
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _exportRequested = exportRequested;
        _importRequested = importRequested;

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
        StartupToggle.IsOn = StartupService.IsEnabled;
        AutoHideToggle.IsOn = settings.Current.AutoHide;
        DefaultTabBox.SelectedIndex = Math.Clamp(settings.Current.DefaultTab, 0, 3);
        HotkeyBox.SelectedIndex = Math.Clamp(settings.Current.GlobalHotkeyMode, 0, 3);
        QuickAddHotkeyBox.SelectedIndex = Math.Clamp(settings.Current.QuickAddHotkeyMode, 0, 3);
        BuildSmartViewList();
        BuildTagNameEditors();
        LeadBox.SelectedIndex = Math.Max(0, Array.IndexOf(LeadOptions, settings.Current.ReminderLeadMinutes));

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
        ApplyWindowMaterial();
    }

    private void ConfigureWindow()
    {
        _appWindow.Title = "设置";
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