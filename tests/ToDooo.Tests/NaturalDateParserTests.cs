using ToDooo.Helpers;
using Xunit;

namespace ToDooo.Tests;

public sealed class NaturalDateParserTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0); // 周三上午 10 点

    [Fact]
    public void TomorrowWithoutTime_DefaultsToNine()
    {
        Assert.True(NaturalDateParser.TryParse("明天交报告", Now, out var due, out var title));
        Assert.Equal(new DateTime(2026, 11, 19, 9, 0, 0), due);
        Assert.Equal("交报告", title);
    }

    [Fact]
    public void TodayWithClockTime_KeepsBoth()
    {
        Assert.True(NaturalDateParser.TryParse("今天18:30 买菜", Now, out var due, out var title));
        Assert.Equal(new DateTime(2026, 11, 18, 18, 30, 0), due);
        Assert.Equal("买菜", title);
    }

    [Fact]
    public void WeekdayWithAfternoonPeriod()
    {
        Assert.True(NaturalDateParser.TryParse("周五下午3点汇报", Now, out var due, out var title));
        Assert.Equal(new DateTime(2026, 11, 20, 15, 0, 0), due);
        Assert.Equal("汇报", title);
    }

    [Fact]
    public void MonthDayWithHalfHour()
    {
        Assert.True(NaturalDateParser.TryParse("11月20日10点半开会", Now, out var due, out var title));
        Assert.Equal(new DateTime(2026, 11, 20, 10, 30, 0), due);
        Assert.Equal("开会", title);
    }

    [Fact]
    public void TimeOnly_RollsToTomorrowWhenPassed()
    {
        var morning = new DateTime(2026, 11, 18, 9, 0, 0);
        Assert.True(NaturalDateParser.TryParse("10点开会", morning, out var due1, out _));
        Assert.Equal(new DateTime(2026, 11, 18, 10, 0, 0), due1);

        var later = new DateTime(2026, 11, 18, 11, 0, 0);
        Assert.True(NaturalDateParser.TryParse("10点开会", later, out var due2, out _));
        Assert.Equal(new DateTime(2026, 11, 19, 10, 0, 0), due2);
    }

    [Fact]
    public void DaysAfter_Works()
    {
        Assert.True(NaturalDateParser.TryParse("3天后提交", Now, out var due, out var title));
        Assert.Equal(new DateTime(2026, 11, 21, 9, 0, 0), due);
        Assert.Equal("提交", title);
    }

    [Fact]
    public void NextWeekday_Works()
    {
        Assert.True(NaturalDateParser.TryParse("下周一 9点 例会", Now, out var due, out var title));
        Assert.Equal(new DateTime(2026, 11, 23, 9, 0, 0), due);
        Assert.Equal("例会", title);
    }

    [Fact]
    public void PlainTitle_IsNotParsed()
    {
        Assert.False(NaturalDateParser.TryParse("买牛奶", Now, out _, out var title));
        Assert.Equal("买牛奶", title);
    }

    [Fact]
    public void DateOnlyText_IsNotParsedIntoEmptyTitle()
    {
        Assert.False(NaturalDateParser.TryParse("明天", Now, out _, out var title));
        Assert.Equal("明天", title);
    }

    [Fact]
    public void TitleCleanup_TrimsTrailingPunctuation()
    {
        Assert.True(NaturalDateParser.TryParse("交报告，明天，10点", Now, out var due, out var title));
        Assert.Equal(new DateTime(2026, 11, 19, 10, 0, 0), due);
        Assert.Equal("交报告", title);
    }
}
