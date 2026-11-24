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

    public CalendarRepository CalendarCache { get; private set; } = null!;

    public CalendarSyncService CalendarSync { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!EnsureSingleInstance())
        {
            return;
        }

        EnsureAppUserModelId();

        AppPaths.Ensure();

        EnableDarkWin32Menus();
        ApplySystemAccent();
        _uiSettings.ColorValuesChanged += (_, _) => _dispatcher?.TryEnqueue(ApplySystemAccent);

        Repository = new TodoRepository(AppPaths.DatabasePath, AppPaths.BackupDir);
        Repository.Initialize();
        Settings = new SettingsService();
        Notifier = new Notifier();
        Notifier.Initialize();

        // 通知图标要在 SDK 注册完之后覆盖：注册动作会重新生成一张 709 字节的占位 PNG，
        // 提前写会被它盖掉。放到注册之后，并留一点时间让它落盘。
        ScheduleNotificationIconRefresh();

        CalendarCache = new CalendarRepository(AppPaths.DatabasePath);
        CalendarCache.Initialize();
        CalendarSync = new CalendarSyncService(CalendarCache, Settings);

        _window = new MainWindow(Repository, Settings, Notifier, CalendarSync);
        Notifier.Fallback = _window.ShowBalloon;
        _window.Startup();
    }

    /// <summary>
    /// 非打包应用的 AppUserModelID 默认是 exe 路径，系统通知会因此落到一个没有图标注册的身份上，
    /// 左上角显示成默认占位图。固定一个稳定 ID，并在注册表里给这个 ID 挂上应用图标，
    /// 通知左上角、任务栏就都和托盘用同一张图。
    /// </summary>
    private static void EnsureAppUserModelId()
    {
        const string appId = "ToDooo.App";
        try
        {
            NativeMethods.SetCurrentProcessExplicitAppUserModelID(appId);

            var exePath = Environment.ProcessPath;
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            var displayIcon = File.Exists(icoPath) ? icoPath : exePath;

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + appId);
            key?.SetValue("DisplayName", "ToDooo");
            if (!string.IsNullOrEmpty(displayIcon))
            {
                key?.SetValue("DisplayIcon", displayIcon);
            }

        }
        catch
        {
            // 注册失败只影响通知图标，不影响通知本身
        }
    }

    /// <summary>
    /// SDK 注册通知后会异步生成占位图标 PNG。这里等它一会儿再覆盖成应用真实图标，
    /// 之后再重试一次，避免和 SDK 的写入抢先后顺序。
    /// </summary>
    private void ScheduleNotificationIconRefresh()
    {
        var pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
        if (!File.Exists(pngPath))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var delayMs in new[] { 1200, 3000, 6000 })
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                CopyNotificationIconPng("ToDooo.App", pngPath);
            }
        });
    }

    /// <summary>
    /// 把随包发布的 AppIcon.png 复制到 SDK 约定的通知图标位置。
    /// 这张 PNG 是构建时由同一个 ico 转出来的，和托盘、exe 图标同源。
    /// </summary>
    private static void CopyNotificationIconPng(string appId, string pngPath)
    {
        try
        {
            var target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsAppSDK", appId + ".png");

            // 内容一致就不重写：SDK 每次启动都会在这里生成占位图，比较大小能快速判断是不是我们的。
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(pngPath).Length)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(pngPath, target, overwrite: true);
        }
        catch
        {
            // 复制失败就沿用系统占位图，不影响通知
        }
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
            // key 跟着数据目录走：默认目录仍是 ToDooo-main，截图/调试用的独立目录互不干扰。
            var main = AppInstance.FindOrRegisterForKey(AppPaths.InstanceKey);
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
