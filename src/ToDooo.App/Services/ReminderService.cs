using Microsoft.UI.Dispatching;
using ToDooo.Models;

namespace ToDooo.Services;

public sealed class ReminderService : IDisposable
{
    private readonly TodoRepository _repository;
    private readonly Notifier _notifier;
    private readonly DispatcherQueue _dispatcher;
    private DispatcherQueueTimer? _timer;

    public ReminderService(TodoRepository repository, Notifier notifier, DispatcherQueue dispatcher)
    {
        _repository = repository;
        _notifier = notifier;
        _dispatcher = dispatcher;
    }

    public Func<IReadOnlyList<TodoItem>>? Source { get; set; }

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
        foreach (var item in Source())
        {
            item.RaiseDerived();

            if (item.IsDone || item.DueAt is not { } due || item.DueNotified)
            {
                continue;
            }

            if (due > now)
            {
                continue;
            }

            item.DueNotified = true;
            _repository.Update(item);

            // 超过一小时才发现的到期项只做标记，避免开机后集中轰炸
            if ((now - due).TotalHours <= 1)
            {
                _notifier.Show("待办到期", $"{item.Title}（截止 {due:HH:mm}）");
            }
        }
    }
}
