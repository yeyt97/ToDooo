using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ToDooo.Models;

public enum TodoRecurrence
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
}

public partial class SubtaskItem : ObservableObject
{
    public long Id { get; set; }

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private bool isDone;

    [ObservableProperty]
    private double sortOrder;
}

/// <summary>编辑弹窗里子任务的中间态：Id 为 0 表示这次新加的。</summary>
public sealed record SubtaskDraft(long Id, string Title, bool Done);

public partial class TodoItem : ObservableObject
{
    public long Id { get; set; }

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private bool isDone;

    [ObservableProperty]
    private bool isImportant;

    [ObservableProperty]
    private DateTime? dueAt;

    [ObservableProperty]
    private TodoRecurrence recurrence;

    [ObservableProperty]
    private int tagIndex = -1;

    [ObservableProperty]
    private double sortOrder;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [ObservableProperty]
    private DateTime? completedAt;

    [ObservableProperty]
    private bool dueNotified;

    /// <summary>提醒被推迟到什么时候；为空表示没推迟。</summary>
    [ObservableProperty]
    private DateTime? snoozeUntil;

    [ObservableProperty]
    private bool subtasksExpanded;

    public ObservableCollection<SubtaskItem> Subtasks { get; } = new();

    public bool HasSubtasks => Subtasks.Count > 0;

    public int SubtaskDoneCount => Subtasks.Count(item => item.IsDone);

    public string SubtaskProgressText => $"{SubtaskDoneCount}/{Subtasks.Count}";

    public bool IsOverdue => !IsDone && DueAt is { } due && due < DateTime.Now;

    public string DueText
    {
        get
        {
            var parts = new List<string>(3);
            if (Recurrence != TodoRecurrence.None)
            {
                parts.Add(RecurrenceText);
            }

            if (DueAt is { } due)
            {
                parts.Add($"截止 {due:yyyy/M/d HH:mm}");
                if (!IsDone && due < DateTime.Now)
                {
                    parts.Add("已逾期");
                }
            }

            return string.Join(" · ", parts);
        }
    }

    public string RecurrenceText => Recurrence switch
    {
        TodoRecurrence.Daily => "每天",
        TodoRecurrence.Weekly => "每周",
        TodoRecurrence.Monthly => "每月",
        _ => string.Empty,
    };

    public string ImportantMenuText => IsImportant ? "取消重要标记" : "标记为重要";

    partial void OnIsDoneChanged(bool value) => RaiseDerived();

    partial void OnDueAtChanged(DateTime? value) => RaiseDerived();

    partial void OnRecurrenceChanged(TodoRecurrence value) => RaiseDerived();

    partial void OnIsImportantChanged(bool value) => OnPropertyChanged(nameof(ImportantMenuText));

    public void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(DueText));
        OnPropertyChanged(nameof(RecurrenceText));
    }

    public void RaiseSubtasksChanged()
    {
        OnPropertyChanged(nameof(HasSubtasks));
        OnPropertyChanged(nameof(SubtaskDoneCount));
        OnPropertyChanged(nameof(SubtaskProgressText));
    }

    public TodoItem CreateNextOccurrence(DateTime nextDue)
    {
        var next = new TodoItem
        {
            Title = Title,
            IsImportant = IsImportant,
            Recurrence = Recurrence,
            TagIndex = TagIndex,
            DueAt = nextDue,
            SortOrder = SortOrder + 0.5,
            CreatedAt = DateTime.Now,
        };

        // 重复任务的下一轮把子任务带上，但全部重置为未完成。
        foreach (var subtask in Subtasks)
        {
            next.Subtasks.Add(new SubtaskItem
            {
                Title = subtask.Title,
                SortOrder = subtask.SortOrder,
            });
        }

        return next;
    }
}
