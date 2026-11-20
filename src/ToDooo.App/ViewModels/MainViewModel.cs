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

    /// <summary>当前选中的智能视图；-1 = 不按智能视图筛。</summary>
    [ObservableProperty]
    private int selectedSmartView = -1;

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
        : "点颜色栏右侧 + 新建待办 · 标题里可以直接写时间";

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
    partial void OnSelectedSmartViewChanged(int value) => ApplyFilter();

    public void ToggleSmartView(SmartView view)
    {
        // 再点一次同一视图即取消。
        SelectedSmartView = SelectedSmartView == (int)view ? -1 : (int)view;
    }

    public void ClearSmartView()
    {
        if (SelectedSmartView >= 0)
        {
            SelectedSmartView = -1;
        }
    }

    public void Snooze(TodoItem item, int minutes)
    {
        item.SnoozeUntil = DateTime.Now.AddMinutes(minutes);
        item.DueNotified = false;
        _repository.Update(item);
    }

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
        var viewChanged = Filter != TodoFilter.All || SelectedTag != -1 || SelectedSmartView != -1;
        Filter = TodoFilter.All;
        SelectedTag = -1;
        if (SelectedSmartView != -1)
        {
            SelectedSmartView = -1;
        }

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
        item.SnoozeUntil = null;
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

    /// <summary>主窗口双击标题就地改名；空内容视为放弃修改。</summary>
    public void Rename(TodoItem item, string title)
    {
        title = title.Trim();
        if (title.Length == 0 || title == item.Title)
        {
            return;
        }

        item.Title = title;
        _repository.Update(item);
        SyncVisible(item);
    }

    public void SetTag(TodoItem item, int tagIndex)
    {
        if (item.TagIndex == tagIndex)
        {
            return;
        }

        item.TagIndex = tagIndex;
        _repository.Update(item);
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
            item.SnoozeUntil = null;
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

    /// <summary>主窗口子任务就地改名；空内容视为放弃修改。</summary>
    public void RenameSubtask(TodoItem item, SubtaskItem subtask, string title)
    {
        title = title.Trim();
        if (title.Length == 0 || title == subtask.Title)
        {
            return;
        }

        subtask.Title = title;
        if (subtask.Id > 0)
        {
            _repository.UpdateSubtask(subtask);
        }

        // 搜索会匹配子任务标题，改完要重新判断这条主任务是否还该留在当前视图里。
        SyncVisible(item);
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
        if (item.Subtasks.Count == 0)
        {
            item.SubtasksExpanded = false;
            item.SubtaskAddActive = false;
        }

        item.RaiseSubtasksChanged();
        // 搜索命中子任务时，删掉这一条可能让整条待办不再匹配。
        SyncVisible(item);
    }

    /// <summary>改到另一天：保留原来的时分，清掉提醒/推迟状态，按需重新过滤。</summary>
    public void Reschedule(TodoItem item, DateTime date)
    {
        if (item.DueAt is not { } due)
        {
            return;
        }

        var next = date.Date + due.TimeOfDay;
        if (next == due)
        {
            return;
        }

        item.DueAt = next;
        item.DueNotified = false;
        item.SnoozeUntil = null;
        _repository.Update(item);
        SyncVisible(item);
    }

    /// <summary>拖动过程中的实时换位：只动内存里的顺序，松手时才落库。</summary>
    public void MoveSubtaskLive(TodoItem item, SubtaskItem subtask, int index)
    {
        var from = item.Subtasks.IndexOf(subtask);
        if (from < 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, item.Subtasks.Count - 1);
        if (from != index)
        {
            item.Subtasks.Move(from, index);
        }
    }

    public void CommitSubtaskOrder(TodoItem item) => PersistSubtaskOrders(item);

    public void MoveSubtaskTo(TodoItem item, SubtaskItem subtask, int index)
    {
        var from = item.Subtasks.IndexOf(subtask);
        if (from < 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, item.Subtasks.Count - 1);
        if (from == index)
        {
            return;
        }

        item.Subtasks.Move(from, index);
        PersistSubtaskOrders(item);
    }

    private void PersistSubtaskOrders(TodoItem item)
    {
        for (var i = 0; i < item.Subtasks.Count; i++)
        {
            var current = item.Subtasks[i];
            if (Math.Abs(current.SortOrder - i) < 0.001)
            {
                continue;
            }

            current.SortOrder = i;
            if (current.Id > 0)
            {
                _repository.UpdateSubtask(current);
            }
        }
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

        if (item.Subtasks.Count == 0)
        {
            item.SubtasksExpanded = false;
            item.SubtaskAddActive = false;
        }

        item.RaiseSubtasksChanged();
        SyncVisible(item);
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
                // 搜索命中的是子任务时，直接把这行展开，别让人看不到命中位置。
                if (_searchQuery.Length > 0 && item.HasSubtasks)
                {
                    item.SubtasksExpanded = item.Subtasks.Any(subtask =>
                        subtask.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
                }

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

    private bool MatchesSmartView(TodoItem item)
    {
        // 智能视图只看未完成的；已完成的不再占日期。
        if (item.IsDone)
        {
            return false;
        }

        var now = DateTime.Now;
        var today = now.Date;
        return (SmartView)SelectedSmartView switch
        {
            SmartView.Today => item.DueAt is { } due && due.Date == today,
            SmartView.Tomorrow => item.DueAt is { } due && due.Date == today.AddDays(1),
            SmartView.ThisWeek => item.DueAt is { } due &&
                due.Date >= today &&
                due.Date < WeekStart(today).AddDays(7),
            SmartView.Overdue => item.DueAt is { } due && due < now,
            _ => true,
        };
    }

    // 周一为一周起点。
    private static DateTime WeekStart(DateTime day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

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

        if (SelectedSmartView >= 0 && !MatchesSmartView(item))
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
