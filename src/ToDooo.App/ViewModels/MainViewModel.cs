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

    private string _searchQuery = string.Empty;

    public string SearchQuery => _searchQuery;

    public bool IsEmpty => Items.Count == 0;

    public string EmptyTitle => _searchQuery.Length > 0 ? "没有找到匹配的待办" : "还没有待办";

    public string EmptyDescription => _searchQuery.Length > 0
        ? "换个关键词，或清空搜索看全部"
        : "回车或离焦即保存 · 点右侧日历设置时间 · 点任务编辑详情";

    public bool HasCompleted => DoneCount > 0;

    public int AllCount => _all.Count;

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

        // 新建会把视图切回「全部」，只有原本就不在「全部」视图时才需要重建。
        var viewChanged = Filter != TodoFilter.All || SelectedTag != -1;
        Filter = TodoFilter.All;
        SelectedTag = -1;

        if (viewChanged)
        {
            ApplyFilter();
            return;
        }

        // 已经显示全部：把新条目按排序位置插进去就好。
        // 不要走 Clear + 重新填充——那会让 ListView 拆掉所有容器重建，列表闪一下、滚动位置也归零。
        SyncVisible(item);
        RefreshCounts();
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

        // 整条完成/取消完成时，子任务跟着同步，避免出现「父任务完成了子项还空着」。
        foreach (var subtask in item.Subtasks)
        {
            if (subtask.IsDone == done)
            {
                continue;
            }

            subtask.IsDone = done;
            if (subtask.Id > 0)
            {
                _repository.UpdateSubtask(subtask);
            }
        }

        item.RaiseSubtasksChanged();

        if (done && item.Recurrence != TodoRecurrence.None)
        {
            var next = item.CreateNextOccurrence(AdvanceDue(item));
            _repository.Add(next);
            _all.Add(next);
            SyncVisible(next);
        }

        // 原地增删这一条即可：勾选完成不该把整个列表重建一遍。
        SyncVisible(item);
        RefreshCounts();
    }

    public void ToggleImportant(TodoItem item)
    {
        item.IsImportant = !item.IsImportant;
        _repository.Update(item);

        // 只有「重要」视图里取消标记才会让条目离开列表，交给 SyncVisible 单独摘掉那一行，
        // 其余情况列表不动，避免重建把悬停状态一起冲掉。
        SyncVisible(item);
        RefreshCounts();
    }

    public TodoItem Delete(TodoItem item)
    {
        _repository.Delete(item.Id);
        _all.Remove(item);
        Items.Remove(item);
        RefreshCounts();
        return item;
    }

    /// <summary>清空已完成并返回被删的条目，供界面做「撤销」。</summary>
    public List<TodoItem> ClearCompleted()
    {
        var removed = _all.Where(item => item.IsDone).ToList();
        if (removed.Count == 0)
        {
            return removed;
        }

        _repository.ClearCompleted();
        _all.RemoveAll(item => item.IsDone);
        ApplyFilter();
        return removed;
    }

    /// <summary>撤销删除：重新落库（分配新 Id），排序位置和其余字段原样保留。</summary>
    public void Restore(IEnumerable<TodoItem> items)
    {
        foreach (var item in items)
        {
            item.Id = 0;
            _repository.Add(item);
            _all.Add(item);
        }

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

        // 编辑只可能让这一条变显或变隐（标签/重要变化），按需增删一行，不重建列表。
        SyncVisible(item);
        RefreshCounts();
    }

    public void AddSubtask(TodoItem item, string title)
    {
        title = title.Trim();
        if (title.Length == 0)
        {
            return;
        }

        var subtask = new SubtaskItem { Title = title, SortOrder = item.Subtasks.Count };
        if (item.Id > 0)
        {
            _repository.AddSubtask(item.Id, subtask);
        }

        item.Subtasks.Add(subtask);
        item.RaiseSubtasksChanged();
    }

    public void SetSubtaskDone(TodoItem item, SubtaskItem subtask, bool done)
    {
        if (subtask.IsDone == done)
        {
            return;
        }

        subtask.IsDone = done;
        if (subtask.Id > 0)
        {
            _repository.UpdateSubtask(subtask);
        }

        item.RaiseSubtasksChanged();
    }

    public void DeleteSubtask(TodoItem item, SubtaskItem subtask)
    {
        if (subtask.Id > 0)
        {
            _repository.DeleteSubtask(subtask.Id);
        }

        item.Subtasks.Remove(subtask);
        item.RaiseSubtasksChanged();
    }

    /// <summary>编辑保存时按草稿对齐子任务：已有的更新、新的补插、草稿里没有的删掉。</summary>
    public void ApplySubtasks(TodoItem item, IReadOnlyList<SubtaskDraft> drafts)
    {
        var existing = item.Subtasks.ToDictionary(subtask => subtask.Id);
        var ordered = new List<SubtaskItem>(drafts.Count);
        var index = 0;

        foreach (var draft in drafts)
        {
            var title = draft.Title.Trim();
            if (title.Length == 0)
            {
                continue;
            }

            if (draft.Id > 0 && existing.Remove(draft.Id, out var subtask))
            {
                subtask.Title = title;
                subtask.IsDone = draft.Done;
                subtask.SortOrder = index++;
                _repository.UpdateSubtask(subtask);
                ordered.Add(subtask);
                continue;
            }

            var created = new SubtaskItem
            {
                Title = title,
                IsDone = draft.Done,
                SortOrder = index++,
            };
            _repository.AddSubtask(item.Id, created);
            ordered.Add(created);
        }

        foreach (var removed in existing.Values)
        {
            _repository.DeleteSubtask(removed.Id);
        }

        item.Subtasks.Clear();
        foreach (var subtask in ordered)
        {
            item.Subtasks.Add(subtask);
        }

        item.RaiseSubtasksChanged();
    }

    public void SetSearch(string? query)
    {
        var next = (query ?? string.Empty).Trim();
        if (next == _searchQuery)
        {
            return;
        }

        _searchQuery = next;
        OnPropertyChanged(nameof(SearchQuery));
        ApplyFilter();
    }

    public void SetFilter(TodoFilter filter)
    {
        Filter = Filter == filter && filter != TodoFilter.All ? TodoFilter.All : filter;
        ApplyFilter();
    }

    /// <summary>点颜色只选中，不再点掉；取消筛选走末尾的“清除颜色”按钮（传 -1）。</summary>
    public void SelectTag(int index)
    {
        SelectedTag = index;
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
        }

        // 一次拖动只写一次库（单事务批量更新），条目多的时候也不会卡。
        _repository.UpdateOrders(_all);
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

    /// <summary>
    /// 只针对单个条目做增量更新：该显示的按顺序插进去，不该显示的就摘掉。
    /// 相比 ApplyFilter 的 Clear + 重新填充，列表不会整片重建，也就不会闪。
    /// </summary>
    private void SyncVisible(TodoItem item)
    {
        var index = Items.IndexOf(item);
        var shouldShow = Matches(item);

        if (shouldShow && index < 0)
        {
            Items.Insert(IndexFor(item), item);
        }
        else if (!shouldShow && index >= 0)
        {
            Items.RemoveAt(index);
        }
    }

    // 按 ApplyFilter 用的同一套排序键（SortOrder，其次 Id）算插入位置。
    private int IndexFor(TodoItem item)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            var other = Items[i];
            var before = item.SortOrder != other.SortOrder
                ? item.SortOrder < other.SortOrder
                : item.Id.CompareTo(other.Id) < 0;

            if (before)
            {
                return i;
            }
        }

        return Items.Count;
    }

    private void RefreshCounts()
    {
        ActiveCount = _all.Count(item => !item.IsDone);
        ImportantCount = _all.Count(item => item.IsImportant && !item.IsDone);
        DoneCount = _all.Count(item => item.IsDone);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(AllCount));
    }

    private bool Matches(TodoItem item)
    {
        if (_searchQuery.Length > 0 &&
            !item.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) &&
            !item.Subtasks.Any(subtask =>
                subtask.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

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
