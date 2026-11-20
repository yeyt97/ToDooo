using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using ToDooo.Interop;
using ToDooo.Services;

namespace ToDooo;

public partial class App : Application
{
    private DispatcherQueue? _dispatcher;
    private MainWindow? _window;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log(e.Exception);
    }

    public TodoRepository Repository { get; private set; } = null!;

    public SettingsService Settings { get; private set; } = null!;

    public Notifier Notifier { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!EnsureSingleInstance())
        {
            return;
        }

        AppPaths.Ensure();

        EnableDarkWin32Menus();
        ApplySystemAccent();
        _uiSettings.ColorValuesChanged += (_, _) => _dispatcher?.TryEnqueue(ApplySystemAccent);

        Repository = new TodoRepository(AppPaths.DatabasePath);
        Repository.Initialize();
        Settings = new SettingsService();
        Notifier = new Notifier();
        Notifier.Initialize();

        _window = new MainWindow(Repository, Settings, Notifier);
        Notifier.Fallback = _window.ShowBalloon;
        _window.Startup();
    }

    /// <summary>
    /// 把系统强调色写进 AccentBrush / AccentGradientBrush 这两个共享实例：
    /// 就地改颜色而不是换资源，已经引用它们的控件会跟着变，系统里改强调色也不用重启。
    /// </summary>
    private void ApplySystemAccent()
    {
        var accent = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        if (Application.Current.Resources["AccentBrush"] is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
        {
            brush.Color = accent;
        }

        if (Application.Current.Resources["AccentGradientBrush"] is Microsoft.UI.Xaml.Media.LinearGradientBrush gradient &&
            gradient.GradientStops.Count >= 2)
        {
            gradient.GradientStops[0].Color =
                _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight2);
            gradient.GradientStops[1].Color = accent;
        }
    }

    // Win32 弹出菜单（托盘右键菜单）跟随 App 的深色主题。
    // 不设置「首选应用模式」时，托盘菜单永远是经典亮色不透明样式，
    // 和界面里 WinUI 的深色亚克力 flyout 明显不是一套东西。
    private static void EnableDarkWin32Menus()
    {
        try
        {
            NativeMethods.SetPreferredAppMode(2); // 2 = ForceDark
            NativeMethods.FlushMenuThemes();
        }
        catch (Exception exception)
        {
            // 旧系统上这两个序号可能不存在，菜单退回系统默认样式，不影响功能。
            Log(exception);
        }
    }

    private bool EnsureSingleInstance()
    {
        try
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            var main = AppInstance.FindOrRegisterForKey("ToDooo-main");
            if (main.IsCurrent)
            {
                main.Activated += (_, _) =>
                    _dispatcher?.TryEnqueue(() => _window?.ShowFromTray());
                return true;
            }

            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            main.RedirectActivationToAsync(activation).AsTask().Wait(3000);
            Environment.Exit(0);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void Log(Exception exception)
    {
        try
        {
            AppPaths.Ensure();
            File.AppendAllText(AppPaths.LogPath, $"[{DateTime.Now:O}] {exception}\r\n");
        }
        catch
        {
            // 日志失败忽略
        }
    }
}
