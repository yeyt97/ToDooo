using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using ToDooo.Services;

namespace ToDooo;

public partial class App : Application
{
    private DispatcherQueue? _dispatcher;
    private MainWindow? _window;

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

        Repository = new TodoRepository(AppPaths.DatabasePath);
        Repository.Initialize();
        Settings = new SettingsService();
        Notifier = new Notifier();
        Notifier.Initialize();

        _window = new MainWindow(Repository, Settings, Notifier);
        Notifier.Fallback = _window.ShowBalloon;
        _window.Activate();
        _window.Startup();
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
