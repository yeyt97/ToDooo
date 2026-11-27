namespace ToDooo.Views;

/// <summary>
/// 月视图是一块可无限滚动的长网格：默认 18 周，滚到边缘再上下各加 6 周。
/// 从缓存读取事件的日期范围必须覆盖**整块网格**，而不是只覆盖当前这个月；
/// 否则滚出那一个月就滚出了已加载区间，格子会整片变空白
/// （表现为「滚一下日程全没了，点今天又回来」）。
/// </summary>
public static class CalendarMonthWindow
{
    /// <summary>重建网格时默认铺多少周。</summary>
    public const int DefaultWeeks = 18;

    /// <summary>目标月 1 号所在周的周一。</summary>
    public static DateTime WeekStart(DateTime date) =>
        date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>网格起点：目标月 1 号所在周再往前 6 周（留出上滚缓冲）。</summary>
    public static DateTime GridStartFor(DateTime month) =>
        WeekStart(new DateTime(month.Year, month.Month, 1)).AddDays(-42);

    /// <summary>
    /// 需要从缓存读取的事件日期范围，两头各留一周余量。
    /// <paramref name="loadedWeeks"/> 为 0 表示网格还没建过，按默认周数估算。
    /// 跳月时网格会围绕 <paramref name="pendingJump"/> 重建，此时旧网格的起点也要一并覆盖。
    /// </summary>
    public static (DateTime From, DateTime To) For(
        DateTime gridStart, int loadedWeeks, DateTime visibleMonth, DateTime? pendingJump)
    {
        var anchor = pendingJump ?? visibleMonth;
        var anchorStart = GridStartFor(anchor);
        var anchorEnd = anchorStart.AddDays(DefaultWeeks * 7);

        if (loadedWeeks <= 0)
        {
            return (anchorStart.AddDays(-7), anchorEnd.AddDays(7));
        }

        // 旧网格可能早于也可能晚于目标月，两头取并集才安全。
        var start = gridStart < anchorStart ? gridStart : anchorStart;
        var end = gridStart.AddDays(loadedWeeks * 7);
        if (anchorEnd > end)
        {
            end = anchorEnd;
        }

        return (start.AddDays(-7), end.AddDays(7));
    }
}
