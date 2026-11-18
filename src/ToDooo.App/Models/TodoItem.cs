using CommunityToolkit.Mvvm.ComponentModel;

namespace ToDooo.Models;

public enum TodoRecurrence
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
}

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

    public TodoItem CreateNextOccurrence(DateTime nextDue) => new()
    {
        Title = Title,
        IsImportant = IsImportant,
        Recurrence = Recurrence,
        TagIndex = TagIndex,
        DueAt = nextDue,
        SortOrder = SortOrder + 0.5,
        CreatedAt = DateTime.Now,
    };
}
