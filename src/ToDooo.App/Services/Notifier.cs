using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ToDooo.Services;

public sealed class Notifier
{
    private AppNotificationManager? _manager;

    public Action<string, string>? Fallback { get; set; }

    public void Initialize()
    {
        try
        {
            _manager = AppNotificationManager.Default;
            _manager.Register();
        }
        catch
        {
            _manager = null;
        }
    }

    public void Show(string title, string message)
    {
        if (_manager is not null)
        {
            try
            {
                _manager.Show(new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message)
                    .BuildNotification());
                return;
            }
            catch
            {
                // 系统通知不可用时退回托盘气泡
            }
        }

        Fallback?.Invoke(title, message);
    }
}
