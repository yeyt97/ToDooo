using System.Diagnostics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ToDooo.Interop;
using ToDooo.Services;
using Windows.Graphics;

namespace ToDooo.Views;

/// <summary>外观 / 行为 / 数据设置。改动即时生效，窗口本身也跟随主题。</summary>
public sealed partial class SettingsWindow : Window
{
    private const int LogicalWidth = 420;
    private const int LogicalHeight = 520;

    private static readonly int[] LeadOptions = { 0, 5, 15, 30, 60 };

    private readonly SettingsService _settings;
    private readonly Action _settingsChanged;
    private readonly Func<Task> _exportRequested;
    private readonly Action<bool> _pinChanged;
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
        Action<bool> pinChanged)
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _exportRequested = exportRequested;
        _pinChanged = pinChanged;

        InitializeComponent();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();

        AboutText.Text = "ToDooo 1.0.0 · WinUI 3\n数据目录：%LocalAppData%\\ToDooo";

        ThemeBox.SelectedIndex = settings.Current.ThemeMode;
        OpacitySlider.Value = Math.Clamp(settings.Current.WindowOpacity, 0.4, 1.0);
        OpacityValue.Text = $"{Math.Round(OpacitySlider.Value * 100)}%";
        StartupToggle.IsOn = StartupService.IsEnabled;
        PinToggle.IsOn = settings.Current.PinnedToDesktop;
        LeadBox.SelectedIndex = Math.Max(0, Array.IndexOf(LeadOptions, settings.Current.ReminderLeadMinutes));

        _ready = true;
        Closed += (_, _) => _acrylic = null;
    }

    public void ShowOver(AppWindow? owner)
    {
        _appWindow.Show();
        MoveNextTo(owner);
        Activate();
    }

    private void ConfigureWindow()
    {
        _appWindow.Title = "设置";
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, true);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalWidth * scale),
            (int)Math.Round(LogicalHeight * scale)));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(HeaderBar);
        _appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        HeaderBar.Padding = new Thickness(18, 0, Math.Max(12, _appWindow.TitleBar.RightInset), 0);

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

    private void MoveNextTo(AppWindow? owner)
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

        _settings.Current.WindowOpacity = e.NewValue;
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

    private void PinToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _pinChanged(PinToggle.IsOn);
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

    private async void ExportButton_Click(object sender, RoutedEventArgs e) => await _exportRequested();

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