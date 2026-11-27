using ToDooo.Views;
using Xunit;

namespace ToDooo.Tests;

public class CalendarMonthWindowTests
{
    private static readonly DateTime November2026 = new(2026, 11, 1);

    [Fact]
    public void GridStart_IsSixWeeksBeforeTheFirstOfMonth_OnAMonday()
    {
        var start = CalendarMonthWindow.GridStartFor(November2026);

        Assert.Equal(DayOfWeek.Monday, start.DayOfWeek);
        Assert.Equal(new DateTime(2026, 9, 14), start); // 11/1 所在周的周一往前 42 天
    }

    [Fact]
    public void Window_CoversTheWholeDefaultBuffer()
    {
        var gridStart = CalendarMonthWindow.GridStartFor(November2026);
        var (from, to) = CalendarMonthWindow.For(gridStart, 18, November2026, null);

        Assert.True(from <= gridStart, "窗口要盖住网格第一周");
        Assert.True(
            to >= gridStart.AddDays(18 * 7),
            "窗口要盖住网格最后一周（18 周 ≈ 4 个多月）");
    }

    [Fact]
    public void Window_GrowsWithTheBuffer_SoScrollingDoesNotRunOutOfEvents()
    {
        // 从 2025 年 5 月一路往下滚到 2026 年 11 月：网格被不断加长。
        var gridStart = CalendarMonthWindow.GridStartFor(new DateTime(2025, 5, 1));
        var weeks = 18 + 6 * 14;

        var (from, to) = CalendarMonthWindow.For(gridStart, weeks, November2026, null);

        Assert.True(from <= gridStart);
        Assert.True(to >= new DateTime(2026, 12, 1), "滚动到 2026 年底时窗口必须包含这些日期");
    }

    [Fact]
    public void Window_AlsoCoversThePendingJumpTarget()
    {
        // 网格还停在今天附近，但马上要跳到 2025 年 5 月。
        var gridStart = CalendarMonthWindow.GridStartFor(November2026);
        var (from, to) = CalendarMonthWindow.For(gridStart, 18, November2026, new DateTime(2025, 5, 4));

        Assert.True(from <= CalendarMonthWindow.GridStartFor(new DateTime(2025, 5, 1)));
        Assert.True(to >= new DateTime(2025, 8, 1));
        Assert.True(to >= November2026, "旧网格覆盖的范围也不能丢");
    }

    [Fact]
    public void Window_FallsBackToDefaultWeeks_WhenNothingIsBuiltYet()
    {
        var (from, to) = CalendarMonthWindow.For(default, 0, November2026, null);

        Assert.Equal(CalendarMonthWindow.GridStartFor(November2026).AddDays(-7), from);
        // 2026-09-14 起 18 周 = 2027-01-18，再留一周余量。
        Assert.True(to >= new DateTime(2027, 1, 18), $"实际到 {to:yyyy-MM-dd}");
    }
}
