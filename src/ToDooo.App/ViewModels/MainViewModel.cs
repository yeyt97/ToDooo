using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ToDooo.Models;
using ToDooo.Services;

namespace ToDooo.ViewModels;

public enum TodoFilter
{
    All,
    Active,
    Important,
    Done,
}

public sealed partial class MainViewModel : ObservableObject
{
    // DeskBox-style color markers: red / orange / yellow / green / blue / purple / pink / teal
    public static readonly string[] TagColors =
    {
        "#E34D4D", "#F08A3C", "#F2C94C", "#4CAF6D", "#4D8FE3", "#9B6BE8", "#E66AA2", "#2DB7A3",
    };

    private readonly TodoRepository _repository;
    private readonly List<TodoItem> _all = new();

    [ObservableProperty]
    private int activeCount;

    [ObservableProperty]
    private int importantCount;

    [ObservableProperty]
    private int doneCount;

    public MainViewModel(TodoRepository repository)
    {
        _repository = repository;
    }

    public ObservableCollection<TodoItem> Items { get; } = new();

    public IReadOnlyList<TodoItem> AllItems => _all;

    public TodoFilter Filter { get; private set; } = TodoFilter.All;

    public int SelectedTag { get; private set; } = -1;

    public bool IsEmpty => Items.Count == 0;

    public bool HasCompleted => DoneCount > 0;

    public int AllCount => _all.Count;

    public string FooterText => ActiveCount > 0 ? $"还剩 {ActiveCount} 项" : "全部完成";

    public void Load()
    {
        _all.Clear();
        _all.AddRange(_repository.GetAll());
        ApplyFilter();
    }

    public void Add(string title)
    {
        title = title.Trim();
        if (title.Length == 0)
        {
            return;
        }

        AddItem(new TodoItem { Title = title });
    }

    /// <summary>详细新建：标题之外还可带截止时间、重复、重要与标签。</summary>
    public void AddItem(TodoItem item)
    {
        item.Title = item.Title.Trim();
        if (item.Title.Length == 0)
        {
            return;
        }

        item.SortOrder = NextTopOrder();
        item.CreatedAt = DateTime.Now;
        _repository.Add(item);
        _all.Add(item);
        Filter = TodoFilter.All;
        SelectedTag = -1;
        ApplyFilter();
    }

    public void SetDone(TodoItem item, bool done)
    {
        if (item.IsDone == done)
        {
            return;
        }

        item.IsDone = done;
        item.CompletedAt = done ? DateTime.Now : null;
        _repository.Update(item);

        if (done && item.Recurrence != TodoRecurrence.None)
        {
            var next = item.CreateNextOccurrence(AdvanceDue(item));
            _repository.Add(next);
            _all.Add(next);
        }

        ApplyFilter();
    }

    public void ToggleImportant(TodoItem item)
    {
        item.IsImportant = !item.IsImportant;
        _repository.Update(item);

        // 只有「重要」视图里取消标记才会让条目离开列表。其余情况原地刷新计数即可，
        // 避免重建整个列表把悬停操作浮层一起冲掉，导致点击后看不到星形变化。
        if (Filter == TodoFilter.Important && !item.IsImportant)
        {
            ApplyFilter();
        }
        else
        {
            RefreshCounts();
        }
    }

    public void Delete(TodoItem item)
    {
        _repository.Delete(item.Id);
        _all.Remove(item);
        ApplyFilter();
    }

    public void ClearCompleted()
    {
        _repository.ClearCompleted();
        _all.RemoveAll(item => item.IsDone);
        ApplyFilter();
    }

    public void ApplyEdit(
        TodoItem item,
        string title,
        DateTime? dueAt,
        TodoRecurrence recurrence,
        bool important,
        int tagIndex)
    {
        title = title.Trim();
        if (title.Length == 0)
        {
            title = item.Title;
        }

        if (item.DueAt != dueAt)
        {
            item.DueNotified = false;
        }

        item.Title = title;
        item.DueAt = dueAt;
        item.Recurrence = recurrence;
        item.IsImportant = important;
        item.TagIndex = tagIndex;
        _repository.Update(item);
        ApplyFilter();
    }

    public void SetFilter(TodoFilter filter)
    {
        Filter = Filter == filter && filter != TodoFilter.All ? TodoFilter.All : filter;
        ApplyFilter();
    }

    public void ToggleTag(int index)
    {
        SelectedTag = SelectedTag == index ? -1 : index;
        ApplyFilter();
    }

    public void PersistReorder(IReadOnlyList<TodoItem> ordered)
    {
        var visible = new HashSet<TodoItem>(ordered);
        var slots = new List<int>(ordered.Count);
        for (var i = 0; i < _all.Count; i++)
        {
            if (visible.Contains(_all[i]))
            {
                slots.Add(i);
            }
        }

        for (var i = 0; i < ordered.Count && i < slots.Count; i++)
        {
            _all[slots[i]] = ordered[i];
        }

        for (var i = 0; i < _all.Count; i++)
        {
            _all[i].SortOrder = i;
            _repository.Update(_all[i]);
        }
    }

    public void RefreshTime()
    {
        foreach (var item in _all)
        {
            item.RaiseDerived();
        }
    }

    private double NextTopOrder()
    {
        var min = double.MaxValue;
        foreach (var item in _all)
        {
            if (item.SortOrder < min)
            {
                min = item.SortOrder;
            }
        }

        return min == double.MaxValue ? 0 : min - 1;
    }

    private static DateTime AdvanceDue(TodoItem item)
    {
        var due = item.DueAt ?? DateTime.Now;
        var next = NextDue(due, item.Recurrence);
        while (next <= DateTime.Now)
        {
            next = NextDue(next, item.Recurrence);
        }

        return next;
    }

    private static DateTime NextDue(DateTime from, TodoRecurrence recurrence) => recurrence switch
    {
        TodoRecurrence.Daily => from.AddDays(1),
        TodoRecurrence.Weekly => from.AddDays(7),
        TodoRecurrence.Monthly => from.AddMonths(1),
        _ => from.AddDays(1),
    };

    private void ApplyFilter()
    {
        _all.Sort((left, right) => left.SortOrder != right.SortOrder
            ? left.SortOrder.CompareTo(right.SortOrder)
            : left.Id.CompareTo(right.Id));

        Items.Clear();
        foreach (var item in _all)
        {
            if (Matches(item))
            {
                Items.Add(item);
            }
        }

        RefreshCounts();
    }

    private void RefreshCounts()
    {
        ActiveCount = _all.Count(item => !item.IsDone);
        ImportantCount = _all.Count(item => item.IsImportant && !item.IsDone);
        DoneCount = _all.Count(item => item.IsDone);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(AllCount));
        OnPropertyChanged(nameof(FooterText));
    }

    private bool Matches(TodoItem item)
    {
        if (SelectedTag >= 0 && item.TagIndex != SelectedTag)
        {
            return false;
        }

        return Filter switch
        {
            TodoFilter.Active => !item.IsDone,
            TodoFilter.Important => item.IsImportant && !item.IsDone,
            TodoFilter.Done => item.IsDone,
            _ => true,
        };
    }
}
