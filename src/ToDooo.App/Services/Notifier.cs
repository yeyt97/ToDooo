using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ToDooo.Services;

public sealed class Notifier
{
    private AppNotificationManager? _manager;

    public Action<string, string>? Fallback { get; set; }

    /// <summary>用户点了通知上的“推迟”，参数是待办 Id；回调可能来自后台线程。</summary>
    public Action<long>? SnoozeRequested { get; set; }

    /// <summary>用户点了通知上的“标记完成”，参数是待办 Id（或子任务所属待办 Id + 子任务 Id）。</summary>
    public Action<long>? CompleteRequested { get; set; }

    public Action<long, long>? CompleteSubtaskRequested { get; set; }

    public void Initialize()
    {
        try
        {
            _manager = AppNotificationManager.Default;
            _manager.NotificationInvoked += (_, args) =>
            {
                if (TryReadSnooze(args.Argument, out var snoozeId))
                {
                    SnoozeRequested?.Invoke(snoozeId);
                    return;
                }

                if (TryReadCompleteSubtask(args.Argument, out var parentId, out var subtaskId))
                {
                    CompleteSubtaskRequested?.Invoke(parentId, subtaskId);
                    return;
                }

                if (TryReadComplete(args.Argument, out var completeId))
                {
                    CompleteRequested?.Invoke(completeId);
                }
            };
            _manager.Register();
        }
        catch
        {
            _manager = null;
        }
    }

    public void Show(string title, string message, long? todoId = null, long? subtaskId = null)
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
                    if (subtaskId is { } subId)
                    {
                        builder.AddButton(new AppNotificationButton("标记完成")
                            .AddArgument("complete_sub", $"{id}:{subId}"));
                    }
                    else
                    {
                        builder.AddButton(new AppNotificationButton("标记完成")
                            .AddArgument("complete", id.ToString()));
                    }

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
    private static bool TryReadSnooze(string? argument, out long id) =>
        TryReadLongArg(argument, "snooze", out id);

    private static bool TryReadComplete(string? argument, out long id) =>
        TryReadLongArg(argument, "complete", out id);

    private static bool TryReadCompleteSubtask(string? argument, out long parentId, out long subtaskId)
    {
        parentId = 0;
        subtaskId = 0;
        if (!TryReadArg(argument, "complete_sub", out var value))
        {
            return false;
        }

        var parts = value.Split(':');
        return parts.Length == 2
            && long.TryParse(parts[0], out parentId)
            && long.TryParse(parts[1], out subtaskId);
    }

    private static bool TryReadLongArg(string? argument, string key, out long id)
    {
        id = 0;
        return TryReadArg(argument, key, out var raw) && long.TryParse(raw, out id);
    }

    private static bool TryReadArg(string? argument, string key, out string value)
    {
        value = string.Empty;
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

            if (pair[..separator] == key)
            {
                value = pair[(separator + 1)..];
                return true;
            }
        }

        return false;
    }
}