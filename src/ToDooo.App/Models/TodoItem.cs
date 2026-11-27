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
    private string note = string.Empty;

    [ObservableProperty]
    private bool isDone;

    [ObservableProperty]
    private DateTime? dueAt;

    [ObservableProperty]
    private double sortOrder;

    [ObservableProperty]
    private bool dueNotified;

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public bool IsOverdue => !IsDone && DueAt is { } due && due < DateTime.Now;

    public string DueText => DueAt is { } due ? due.ToString("M/d HH:mm") : string.Empty;

    partial void OnDueAtChanged(DateTime? value) => RaiseDerived();

    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(IsOverdue));

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    public void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(DueText));
    }
}

/// <summary>编辑弹窗里子任务的中间态：Id 为 0 表示这次新加的。</summary>
public sealed record SubtaskDraft(long Id, string Title, bool Done, DateTime? DueAt = null, string Note = "");

public partial class TodoItem : ObservableObject
{
    public long Id { get; set; }

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string note = string.Empty;

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

    [ObservableProperty]
    private bool subtaskAddActive;

    public ObservableCollection<SubtaskItem> Subtasks { get; } = new();

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public bool HasSubtasks => Subtasks.Count > 0;
    public int SubtaskDoneCount => Subtasks.Count(item => item.IsDone);

    public string SubtaskProgressText => $"{SubtaskDoneCount}/{Subtasks.Count}";

    public bool IsOverdue => !IsDone && DueAt is { } due && due < DateTime.Now;

    public string DueText
    {
        get
        {
            var parts = new List<string>(2);
            if (Recurrence != TodoRecurrence.None)
            {
                parts.Add(RecurrenceText);
            }

            // 卡片上只写日期时间：逾期与否由颜色表达，再写「截止」「已逾期」是重复信息。
            if (DueAt is { } due)
            {
                parts.Add(due.ToString("yyyy/M/d HH:mm"));
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

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

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
            Note = Note,
            IsImportant = IsImportant,
            Recurrence = Recurrence,
            TagIndex = TagIndex,
            DueAt = nextDue,
            SortOrder = SortOrder + 0.5,
            CreatedAt = DateTime.Now,
        };

        // 重复任务的下一轮把子任务带上，保留相对父任务的时间差。
        var shift = DueAt is { } oldDue ? nextDue - oldDue : TimeSpan.Zero;
        foreach (var subtask in Subtasks)
        {
            next.Subtasks.Add(new SubtaskItem
            {
                Title = subtask.Title,
                Note = subtask.Note,
                SortOrder = subtask.SortOrder,
                DueAt = subtask.DueAt is { } subtaskDue ? subtaskDue + shift : null,
            });
        }

        return next;
    }
}
