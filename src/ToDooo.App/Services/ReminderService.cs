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

    public ReminderService(TodoRepository repository, Notifier notifier, DispatcherQueue dispatcher)
    {
        _repository = repository;
        _notifier = notifier;
        _dispatcher = dispatcher;
    }

    public Func<IReadOnlyList<TodoItem>>? Source { get; set; }

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
        var missed = new List<TodoItem>();
        foreach (var item in Source())
        {
            item.RaiseDerived();

            if (item.IsDone || item.DueAt is not { } due || item.DueNotified)
            {
                continue;
            }

            // 点了“推迟”的，到推迟时间之前不再提醒。
            if (item.SnoozeUntil is { } snooze && now < snooze)
            {
                continue;
            }

            if (now < due.AddMinutes(-Math.Max(0, LeadMinutes)))
            {
                continue;
            }

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
                missed.Add(item);
            }
        }

        if (_firstTick && missed.Count > 0)
        {
            var titles = string.Join("、", missed.Take(3).Select(item => item.Title));
            var suffix = missed.Count > 3 ? $" 等 {missed.Count} 条" : string.Empty;
            _notifier.Show("有已到期的待办", $"{titles}{suffix}");
        }

        _firstTick = false;
    }
}
