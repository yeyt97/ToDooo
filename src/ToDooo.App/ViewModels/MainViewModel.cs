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

/// <summary>「已完成」视图里按完成时间筛选。</summary>
public enum DoneRange
{
    All = 0,
    Today = 1,
    Yesterday = 2,
    ThisWeek = 3,
    ThisMonth = 4,
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

    /// <summary>「已完成」视图的完成时间范围；只在 Filter = Done 时生效。</summary>
    [ObservableProperty]
    private int selectedDoneRange;

    /// <summary>智能视图只在未完成类视图里有意义，「已完成」里一律不应用。</summary>
    public bool IsDoneView => Filter == TodoFilter.Done;

    public MainViewModel(TodoRepository repository)
    {
        _repository = repository;
    }

    public ObservableCollection<TodoItem> Items { get; } = new();

    /// <summary>可替换的当前时间来源：单元测试注入固定时钟，生产默认真实时间。</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public IReadOnlyList<TodoItem> AllItems => _all;

    public TodoFilter Filter
    {
        get => _filter;
        private set
        {
            if (_filter == value)
            {
                return;
            }

            _filter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDoneView));
        }
    }

    private TodoFilter _filter = TodoFilter.All;

    public int SelectedTag { get; private set; } = -1;

    private string _searchQuery = string.Empty;

    public string SearchQuery => _searchQuery;

    public bool IsEmpty => Items.Count == 0;

    public string EmptyTitle => _searchQuery.Length > 0 ? "没有找到匹配的待办" : "还没有待办";

    public string EmptyDescription => _searchQuery.Length > 0
        ? "换个关键词，或清空搜索看全部"
        : "点颜色栏右侧 + 新建待办 · 标题里可以直接写时间";

    public bool HasCompleted => DoneCount > 0;

    public bool HasOverdue => _all.Any(item =>
        !item.IsDone &&
        (item.IsOverdue || item.Subtasks.Any(subtask => subtask.IsOverdue)));

    public int AllCount => _all.Count;

    public bool HasVisibleSubtasks => Items.Any(item => item.HasSubtasks);

    public bool AreAllVisibleSubtasksExpanded =>
        HasVisibleSubtasks && Items.Where(item => item.HasSubtasks).All(item => item.SubtasksExpanded);

    public string SubtasksToggleText => AreAllVisibleSubtasksExpanded ? "收起子任务" : "展开子任务";

    public event EventHandler? DataChanged;

    private void NotifyDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);

    public void RefreshForDayChange()
    {
        ApplyFilter();
        NotifyDataChanged();
    }

    /// <summary>让列表整体重建一次，用于卡片外部样式（如颜色标签开关）变化后重新求值绑定。</summary>
    public void RefreshCardVisuals()
    {
        ApplyFilter();
    }

    public void NotifySubtaskExpansionChanged()
    {
        OnPropertyChanged(nameof(HasVisibleSubtasks));
        OnPropertyChanged(nameof(AreAllVisibleSubtasksExpanded));
        OnPropertyChanged(nameof(SubtasksToggleText));
    }

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

    partial void OnSelectedDoneRangeChanged(int value) => ApplyFilter();

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
            NotifyDataChanged();
            return;
        }

        // 已经显示全部：把新条目按排序位置插进去就好。
        // 不要走 Clear + 重新填充——那会让 ListView 拆掉所有容器重建，列表闪一下、滚动位置也归零。
        SyncVisible(item);
        RefreshCounts();
        NotifyDataChanged();
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

        // 原地增删或按完成状态重排位置：勾选完成自动沉底，取消完成浮回原序。
        var existingIndex = Items.IndexOf(item);
        if (existingIndex >= 0 && Matches(item))
        {
            Items.RemoveAt(existingIndex);
            Items.Insert(IndexFor(item), item);
        }
        else
        {
            SyncVisible(item);
        }

        RefreshCounts();
        NotifyDataChanged();
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
        NotifyDataChanged();
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
        NotifyDataChanged();
    }

    public void ToggleImportant(TodoItem item)
    {
        item.IsImportant = !item.IsImportant;
        _repository.Update(item);

        // 只有「重要」视图里取消标记才会让条目离开列表，交给 SyncVisible 单独摘掉那一行，
        // 其余情况列表不动，避免重建把悬停状态一起冲掉。
        SyncVisible(item);
        RefreshCounts();
        NotifyDataChanged();
    }

    public TodoItem Delete(TodoItem item)
    {
        _repository.Delete(item.Id);
        _all.Remove(item);
        Items.Remove(item);
        RefreshCounts();
        NotifyDataChanged();
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
        NotifyDataChanged();
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
        NotifyDataChanged();
    }

    public int ImportItems(IEnumerable<TodoItem> imported)
    {
        var count = 0;
        foreach (var item in imported)
        {
            item.Title = item.Title.Trim();
            if (item.Title.Length == 0)
            {
                continue;
            }

            item.Id = 0;
            _repository.Add(item);
            _all.Add(item);
            count++;
        }

        if (count > 0)
        {
            ApplyFilter();
            NotifyDataChanged();
        }

        return count;
    }

    public void ApplyEdit(
        TodoItem item,
        string title,
        DateTime? dueAt,
        TodoRecurrence recurrence,
        bool important,
        int tagIndex,
        string note = "")
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
        item.Note = (note ?? string.Empty).Trim();
        item.DueAt = dueAt;
        item.Recurrence = recurrence;
        item.IsImportant = important;
        item.TagIndex = tagIndex;
        _repository.Update(item);

        // 编辑只可能让这一条变显或变隐（标签/重要变化），按需增删一行，不重建列表。
        SyncVisible(item);
        RefreshCounts();
        NotifyDataChanged();
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
        NotifyDataChanged();
    }

    public void AddSubtask(TodoItem item, string title)
    {
        title = title.Trim();
        if (title.Length == 0)
        {
            return;
        }

        DateTime? dueAt = null;
        if (ToDooo.Helpers.NaturalDateParser.TryParse(title, DateTime.Now, out var parsedDue, out var cleaned))
        {
            title = cleaned;
            dueAt = parsedDue;
        }

        var subtask = new SubtaskItem
        {
            Title = title,
            DueAt = dueAt,
            SortOrder = item.Subtasks.Count,
        };
        if (item.Id > 0)
        {
            _repository.AddSubtask(item.Id, subtask);
        }

        item.Subtasks.Add(subtask);
        item.RaiseSubtasksChanged();
        NotifySubtaskExpansionChanged();
        NotifyDataChanged();
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
        NotifyDataChanged();
    }

    public void UpdateSubtask(
        TodoItem item,
        SubtaskItem subtask,
        string title,
        string note,
        DateTime? dueAt,
        bool isDone)
    {
        title = title.Trim();
        if (title.Length > 0)
        {
            subtask.Title = title;
        }

        subtask.Note = (note ?? string.Empty).Trim();
        if (subtask.DueAt != dueAt)
        {
            subtask.DueNotified = false;
        }

        subtask.DueAt = dueAt;
        subtask.IsDone = isDone;
        if (subtask.Id > 0)
        {
            _repository.UpdateSubtask(subtask);
        }

        item.RaiseSubtasksChanged();
        SyncVisible(item);
        NotifyDataChanged();
    }

    /// <summary>给子任务设置或清除截止时间。</summary>
    public void SetSubtaskDue(TodoItem item, SubtaskItem subtask, DateTime? dueAt)
    {
        if (subtask.DueAt != dueAt)
        {
            subtask.DueNotified = false;
        }

        subtask.DueAt = dueAt;
        if (subtask.Id > 0)
        {
            _repository.UpdateSubtask(subtask);
        }

        item.RaiseSubtasksChanged();
        SyncVisible(item);
        NotifyDataChanged();
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
        NotifySubtaskExpansionChanged();
        NotifyDataChanged();
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
        NotifyDataChanged();
    }

    /// <summary>给无截止日期的待办指定具体日期和时间。</summary>
    public void ScheduleAt(TodoItem item, DateTime dueAt)
    {
        item.DueAt = dueAt;
        item.DueNotified = false;
        item.SnoozeUntil = null;
        _repository.Update(item);
        SyncVisible(item);
        NotifyDataChanged();
    }

    /// <summary>把待办移回无截止日期。</summary>
    public void ClearDue(TodoItem item)
    {
        item.DueAt = null;
        item.DueNotified = false;
        item.SnoozeUntil = null;
        _repository.Update(item);
        SyncVisible(item);
        NotifyDataChanged();
    }

    public sealed record OverdueSnapshot(TodoItem Item, SubtaskItem? Subtask, DateTime? OldDueAt);

    public int RescheduleOverdueToToday(DateTime? nowOverride = null) =>
        RescheduleOverdueToToday(out _, nowOverride);

    /// <summary>把所有已逾期的待办与子任务顺延到今天（保留未到时刻，否则顺延到当前时间 1 小时后），并输出快照支持撤销。</summary>
    public int RescheduleOverdueToToday(out List<OverdueSnapshot> snapshot, DateTime? nowOverride = null)
    {
        snapshot = new List<OverdueSnapshot>();
        var now = nowOverride ?? DateTime.Now;
        var today = now.Date;
        var remainder = now.AddHours(1).Minute % 5;
        var rounded = now.AddHours(1);
        if (remainder != 0)
        {
            rounded = rounded.AddMinutes(5 - remainder);
        }

        var fallbackToday = rounded.Date > today
            ? today.AddHours(23).AddMinutes(55)
            : new DateTime(today.Year, today.Month, today.Day, rounded.Hour, rounded.Minute, 0);

        DateTime NextDueFor(DateTime oldDue)
        {
            var sameTimeToday = today + oldDue.TimeOfDay;
            return sameTimeToday > now ? sameTimeToday : fallbackToday;
        }

        var count = 0;
        foreach (var item in _all)
        {
            if (item.IsDone)
            {
                continue;
            }

            if (item.DueAt is { } due && due < now)
            {
                snapshot.Add(new OverdueSnapshot(item, null, due));
                item.DueAt = NextDueFor(due);
                item.DueNotified = false;
                item.SnoozeUntil = null;
                _repository.Update(item);
                count++;
            }

            foreach (var subtask in item.Subtasks)
            {
                if (!subtask.IsDone && subtask.DueAt is { } subDue && subDue < now)
                {
                    snapshot.Add(new OverdueSnapshot(item, subtask, subDue));
                    subtask.DueAt = NextDueFor(subDue);
                    subtask.DueNotified = false;
                    if (subtask.Id > 0)
                    {
                        _repository.UpdateSubtask(subtask);
                    }

                    count++;
                }
            }
        }

        if (count > 0)
        {
            ApplyFilter();
            NotifyDataChanged();
        }

        return count;
    }

    public void RestoreDueDates(IReadOnlyList<OverdueSnapshot> snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        foreach (var entry in snapshot)
        {
            if (entry.Subtask is { } subtask)
            {
                subtask.DueAt = entry.OldDueAt;
                subtask.DueNotified = false;
                if (subtask.Id > 0)
                {
                    _repository.UpdateSubtask(subtask);
                }

                entry.Item.RaiseSubtasksChanged();
            }
            else
            {
                entry.Item.DueAt = entry.OldDueAt;
                entry.Item.DueNotified = false;
                _repository.Update(entry.Item);
            }
        }

        ApplyFilter();
        NotifyDataChanged();
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
                if (subtask.DueAt != draft.DueAt)
                {
                    subtask.DueNotified = false;
                }

                subtask.Title = title;
                subtask.Note = (draft.Note ?? string.Empty).Trim();
                subtask.IsDone = draft.Done;
                subtask.DueAt = draft.DueAt;
                subtask.SortOrder = index++;
                _repository.UpdateSubtask(subtask);
                ordered.Add(subtask);
                continue;
            }

            var created = new SubtaskItem
            {
                Title = title,
                Note = (draft.Note ?? string.Empty).Trim(),
                IsDone = draft.Done,
                DueAt = draft.DueAt,
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
        NotifySubtaskExpansionChanged();
        NotifyDataChanged();
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

        // 离开「已完成」时清掉时间范围，回来是全新的列表；进「已完成」也不沿用智能视图。
        if (Filter != TodoFilter.Done)
        {
            SelectedDoneRange = 0;
        }
        else if (SelectedSmartView != -1)
        {
            SelectedSmartView = -1;
        }

        ApplyFilter();
    }

    public void SetDoneRange(DoneRange range)
    {
        // 再点一次当前范围即取消，回到「全部」。
        SelectedDoneRange = SelectedDoneRange == (int)range ? 0 : (int)range;
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
            foreach (var subtask in item.Subtasks)
            {
                subtask.RaiseDerived();
            }
        }
    }

    private static int CompareItems(TodoItem left, TodoItem right)
    {
        if (left.IsDone != right.IsDone)
        {
            return left.IsDone.CompareTo(right.IsDone);
        }

        return left.SortOrder != right.SortOrder
            ? left.SortOrder.CompareTo(right.SortOrder)
            : left.Id.CompareTo(right.Id);
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
        _all.Sort(CompareItems);

        Items.Clear();
        var now = DateTime.Now;
        var today = now.Date;
        foreach (var item in _all)
        {
            if (Matches(item))
            {
                // 搜索或智能视图命中的是子任务时，直接把这行展开，别让人看不到命中位置。
                if (_searchQuery.Length > 0 && item.HasSubtasks)
                {
                    item.SubtasksExpanded = item.Subtasks.Any(subtask =>
                        subtask.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
                }
                else if (SelectedSmartView >= 0 && item.HasSubtasks &&
                    item.Subtasks.Any(subtask => !subtask.IsDone &&
                        MatchesSmartViewDate(subtask.DueAt, (SmartView)SelectedSmartView, now, today)))
                {
                    item.SubtasksExpanded = true;
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

    // 按 ApplyFilter 用的同一套排序键（未完成在前，其次 SortOrder，再其次 Id）算插入位置。
    private int IndexFor(TodoItem item)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (CompareItems(item, Items[i]) < 0)
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
        OnPropertyChanged(nameof(HasOverdue));
        OnPropertyChanged(nameof(AllCount));
        NotifySubtaskExpansionChanged();
    }

    private bool MatchesSmartView(TodoItem item)
    {
        // 智能视图只看未完成的；已完成的不再占日期。
        if (item.IsDone)
        {
            return false;
        }

        var now = Clock();
        var today = now.Date;
        var view = (SmartView)SelectedSmartView;
        return MatchesSmartViewDate(item.DueAt, view, now, today) ||
            item.Subtasks.Any(subtask => !subtask.IsDone && MatchesSmartViewDate(subtask.DueAt, view, now, today));
    }

    private static bool MatchesSmartViewDate(DateTime? dueAt, SmartView view, DateTime now, DateTime today) => view switch
    {
        SmartView.Today => dueAt is { } due && due.Date == today,
        SmartView.Tomorrow => dueAt is { } due && due.Date == today.AddDays(1),
        SmartView.ThisWeek => dueAt is { } due &&
            due.Date >= today &&
            due.Date < WeekStart(today).AddDays(7),
        SmartView.Overdue => dueAt is { } due && due < now,
        _ => true,
    };

    // 周一为一周起点。
    private static DateTime WeekStart(DateTime day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private bool Matches(TodoItem item)
    {
        if (_searchQuery.Length > 0 &&
            !item.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) &&
            !item.Note.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) &&
            !item.Subtasks.Any(subtask =>
                subtask.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                subtask.Note.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (SelectedTag >= 0 && item.TagIndex != SelectedTag)
        {
            return false;
        }

        // 智能视图只在未完成类视图里生效；「已完成」里不参与筛选。
        if (Filter != TodoFilter.Done && SelectedSmartView >= 0 && !MatchesSmartView(item))
        {
            return false;
        }

        return Filter switch
        {
            TodoFilter.Active => !item.IsDone,
            TodoFilter.Important => item.IsImportant && !item.IsDone,
            TodoFilter.Done => item.IsDone && MatchesDoneRange(item),
            _ => true,
        };
    }

    /// <summary>「已完成」按完成时间筛选；没有完成时间的旧数据在「全部」里照样显示。</summary>
    private bool MatchesDoneRange(TodoItem item)
    {
        var range = (DoneRange)SelectedDoneRange;
        if (range == DoneRange.All)
        {
            return true;
        }

        if (item.CompletedAt is not { } completed)
        {
            return false;
        }

        var now = Clock();
        var today = now.Date;
        var completedDate = completed.Date;
        return range switch
        {
            DoneRange.Today => completedDate == today,
            DoneRange.Yesterday => completedDate == today.AddDays(-1),
            DoneRange.ThisWeek => completedDate >= WeekStart(today)
                && completedDate < WeekStart(today).AddDays(7),
            DoneRange.ThisMonth => completedDate.Year == today.Year
                && completedDate.Month == today.Month,
            _ => true,
        };
    }
}
