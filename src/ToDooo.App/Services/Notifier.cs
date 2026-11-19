using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ToDooo.Services;

public sealed class Notifier
{
    private AppNotificationManager? _manager;

    public Action<string, string>? Fallback { get; set; }

    /// <summary>用户点了通知上的“推迟”，参数是待办 Id；回调可能来自后台线程。</summary>
    public Action<long>? SnoozeRequested { get; set; }

    public void Initialize()
    {
        try
        {
            _manager = AppNotificationManager.Default;
            _manager.NotificationInvoked += (_, args) =>
            {
                if (TryReadSnooze(args.Argument, out var id))
                {
                    SnoozeRequested?.Invoke(id);
                }
            };
            _manager.Register();
        }
        catch
        {
            _manager = null;
        }
    }

    public void Show(string title, string message, long? todoId = null)
    {
        if (_manager is not null)
        {
            try
            {
                var builder = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message);

                if (todoId is { } id)
                {
                    builder.AddButton(new AppNotificationButton("推迟 10 分钟")
                        .AddArgument("snooze", id.ToString()));
                }

                _manager.Show(builder.BuildNotification());
                return;
            }
            catch
            {
                // 系统通知不可用时退回托盘气泡
            }
        }

        Fallback?.Invoke(title, message);
    }

    /// <summary>通知参数形如 “snooze=12”；没点按钮直接点通知时参数为空。</summary>
    private static bool TryReadSnooze(string? argument, out long id)
    {
        id = 0;
        if (string.IsNullOrEmpty(argument) || argument == "default")
        {
            return false;
        }

        foreach (var pair in argument.Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (pair[..separator] == "snooze" &&
                long.TryParse(pair[(separator + 1)..], out var parsed))
            {
                id = parsed;
                return true;
            }
        }

        return false;
    }
}