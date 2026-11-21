using Microsoft.UI.Dispatching;
using ToDooo.Models;

namespace ToDooo.Services;

public sealed class ReminderService : IDisposable
{
    private readonly TodoRepository _repository;
    private readonly Notifier _notifier;
    private readonly DispatcherQueue _dispatcher;
    private DispatcherQueueTimer? _timer;
    private bool _firstTick = true;
    private DateTime _lastDate = DateTime.Today;

    public ReminderService(TodoRepository repository, Notifier notifier, DispatcherQueue dispatcher)
    {
        _repository = repository;
        _notifier = notifier;
        _dispatcher = dispatcher;
    }

    public Func<IReadOnlyList<TodoItem>>? Source { get; set; }

    /// <summary>跨午夜换日时回调，刷新今天/明天/本周/逾期和日历当天徽章。</summary>
    public Action? DayChanged { get; set; }

    /// <summary>提前多少分钟提醒；0 = 到期时提醒。</summary>
    public int LeadMinutes { get; set; }

    public void Start()
    {
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Tick()
    {
        if (Source is null)
        {
            return;
        }

        var now = DateTime.Now;
        if (now.Date != _lastDate)
        {
            _lastDate = now.Date;
            DayChanged?.Invoke();
        }

        var missed = new List<string>();
        foreach (var item in Source())
        {
            item.RaiseDerived();
            foreach (var subtask in item.Subtasks)
            {
                subtask.RaiseDerived();
            }

            if (item.IsDone)
            {
                continue;
            }

            if (item.DueAt is { } due && !item.DueNotified &&
                (item.SnoozeUntil is not { } snooze || now >= snooze) &&
                now >= due.AddMinutes(-Math.Max(0, LeadMinutes)))
            {
                item.DueNotified = true;
                _repository.Update(item);

                // 超过一小时才发现的到期项不逐条弹，攒起来在开机后汇总一条。
                if ((now - due).TotalHours <= 1)
                {
                    var message = now < due
                        ? $"{item.Title}（{Math.Ceiling((due - now).TotalMinutes)} 分钟后到期）"
                        : $"{item.Title}（截止 {due:HH:mm}）";
                    _notifier.Show("待办提醒", message, item.Id);
                }
                else
                {
                    missed.Add(item.Title);
                }
            }

            foreach (var subtask in item.Subtasks)
            {
                if (subtask.IsDone || subtask.DueAt is not { } subDue || subtask.DueNotified)
                {
                    continue;
                }

                if (now < subDue.AddMinutes(-Math.Max(0, LeadMinutes)))
                {
                    continue;
                }

                subtask.DueNotified = true;
                if (subtask.Id > 0)
                {
                    _repository.UpdateSubtask(subtask);
                }

                var label = $"{item.Title} › {subtask.Title}";
                if ((now - subDue).TotalHours <= 1)
                {
                    var message = now < subDue
                        ? $"{label}（{Math.Ceiling((subDue - now).TotalMinutes)} 分钟后到期）"
                        : $"{label}（截止 {subDue:HH:mm}）";
                    _notifier.Show("子任务提醒", message, item.Id, subtask.Id);
                }
                else
                {
                    missed.Add(label);
                }
            }
        }

        if (_firstTick && missed.Count > 0)
        {
            var titles = string.Join("、", missed.Take(3));
            var suffix = missed.Count > 3 ? $" 等 {missed.Count} 条" : string.Empty;
            _notifier.Show("有已到期的待办", $"{titles}{suffix}");
        }

        _firstTick = false;
    }
}
