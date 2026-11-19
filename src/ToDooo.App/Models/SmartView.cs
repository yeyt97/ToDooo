namespace ToDooo.Models;

/// <summary>智能视图：按截止时间自动筛出的几个常用视角。</summary>
public enum SmartView
{
    Today = 0,
    Tomorrow = 1,
    ThisWeek = 2,
    Overdue = 3,
}

public static class SmartViewInfo
{
    public static string Title(SmartView view) => view switch
    {
        SmartView.Today => "今天",
        SmartView.Tomorrow => "明天",
        SmartView.ThisWeek => "本周",
        SmartView.Overdue => "已逾期",
        _ => string.Empty,
    };

    public static readonly SmartView[] All =
    {
        SmartView.Today,
        SmartView.Tomorrow,
        SmartView.ThisWeek,
        SmartView.Overdue,
    };
}