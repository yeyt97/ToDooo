using ToDooo.Models;
using ToDooo.Services;
using ToDooo.Views;
using Xunit;

namespace ToDooo.Tests;

public class IcsCalendarTests
{
    private const string Sample = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:abc-123
        DTSTAMP:20261122T000000Z
        DTSTART;TZID=Asia/Shanghai:20261123T090000
        DTEND;TZID=Asia/Shanghai:20261123T100000
        SUMMARY:团队周会
        LOCATION:会议室 A
        END:VEVENT
        BEGIN:VTIMEZONE
        TZID:Asia/Shanghai
        BEGIN:STANDARD
        DTSTART:19910414T020000
        RRULE:FREQ=YEARLY;UNTIL=19910914T170000Z;BYMONTH=9
        TZNAME:CST
        TZOFFSETFROM:+0900
        TZOFFSETTO:+0800
        END:STANDARD
        END:VTIMEZONE
        END:VCALENDAR
        """;

    [Fact]
    public void Parse_ReadsSummaryUidAndTimes()
    {
        var events = IcsCalendar.ParseEvents(Sample);

        var evt = Assert.Single(events);
        Assert.Equal("abc-123", evt.Uid);
        Assert.Equal("团队周会", evt.Summary);
        Assert.Equal("会议室 A", evt.Location);
        Assert.Equal(new DateTime(2026, 11, 23, 9, 0, 0), evt.Start);
        Assert.Equal(new DateTime(2026, 11, 23, 10, 0, 0), evt.End);
        Assert.Equal(TimeSpan.FromHours(1), evt.Duration);
        Assert.False(evt.IsAllDay);
    }

    [Fact]
    public void Parse_IgnoresRruleInsideVTimezone()
    {
        // VTIMEZONE 里的 RRULE 不能污染事件：只有一条 VEVENT，且它没有 RRULE。
        var events = IcsCalendar.ParseEvents(Sample);
        Assert.Single(events);
        Assert.Equal(string.Empty, events[0].RRule);
    }

    [Fact]
    public void Parse_IgnoresAlarmDescription()
    {
        // 苹果日历的 VALARM 里固定写 DESCRIPTION:提醒事项，
        // 它不能覆盖事件本身的备注——否则每条日程都凭空多出一句「提醒事项」。
        var ics =
            "BEGIN:VCALENDAR\r\n" +
            "BEGIN:VEVENT\r\n" +
            "UID:alarm-1\r\n" +
            "DTSTART:20261123T090000\r\n" +
            "DTEND:20261123T100000\r\n" +
            "SUMMARY:取快递\r\n" +
            "DESCRIPTION:真实备注内容\r\n" +
            "BEGIN:VALARM\r\n" +
            "ACTION:DISPLAY\r\n" +
            "DESCRIPTION:提醒事项\r\n" +
            "TRIGGER:-PT30M\r\n" +
            "END:VALARM\r\n" +
            "END:VEVENT\r\n" +
            "END:VCALENDAR\r\n";

        var evt = Assert.Single(IcsCalendar.ParseEvents(ics));

        Assert.Equal("真实备注内容", evt.Description);
        Assert.Equal("取快递", evt.Summary);
    }

    [Fact]
    public void Parse_AlarmOnlyDescriptionDoesNotBecomeNote()
    {
        var ics =
            "BEGIN:VCALENDAR\r\n" +
            "BEGIN:VEVENT\r\n" +
            "UID:alarm-2\r\n" +
            "DTSTART:20261123T090000\r\n" +
            "SUMMARY:没有备注\r\n" +
            "BEGIN:VALARM\r\n" +
            "ACTION:DISPLAY\r\n" +
            "DESCRIPTION:提醒事项\r\n" +
            "TRIGGER:-PT30M\r\n" +
            "END:VALARM\r\n" +
            "END:VEVENT\r\n" +
            "END:VCALENDAR\r\n";

        var evt = Assert.Single(IcsCalendar.ParseEvents(ics));

        Assert.Equal(string.Empty, evt.Description);
    }

    [Fact]
    public void Parse_HandlesFoldedLines()
    {
        const string folded = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:x
            DTSTART:20261123T090000Z
            SUMMARY:这是一个很长的标题
             需要折行显示
            END:VEVENT
            END:VCALENDAR
            """;

        var evt = Assert.Single(IcsCalendar.ParseEvents(folded));
        Assert.Equal("这是一个很长的标题需要折行显示", evt.Summary);
    }

    [Fact]
    public void Parse_ReadsAllDayWithDateValue()
    {
        const string allDay = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:y
            DTSTART;VALUE=DATE:20261123
            DTEND;VALUE=DATE:20261124
            SUMMARY:全天
            END:VEVENT
            END:VCALENDAR
            """;

        var evt = Assert.Single(IcsCalendar.ParseEvents(allDay));
        Assert.True(evt.IsAllDay);
        Assert.Equal(new DateTime(2026, 11, 23), evt.Start);
    }

    [Fact]
    public void Parse_ConvertsUtcStampToLocal()
    {
        var parsed = IcsCalendar.ParseStamp("20261123T010000Z");
        Assert.NotNull(parsed);
        Assert.Equal(DateTimeKind.Local, parsed!.Value.Kind);
        Assert.Equal(new DateTime(2026, 11, 23, 1, 0, 0, DateTimeKind.Utc).ToLocalTime(), parsed.Value);
    }

    [Fact]
    public void EscapeAndUnescape_RoundTrip()
    {
        const string raw = "a,b;c\\d\n第二行";
        var escaped = IcsCalendar.EscapeText(raw);
        Assert.Equal(raw, IcsCalendar.UnescapeText(escaped));
    }

    [Fact]
    public void Fold_KeepsLinesUnderSeventySixBytes()
    {
        var text = "SUMMARY:" + new string('中', 60);
        var folded = IcsCalendar.Fold(text);
        foreach (var line in folded.Split("\r\n"))
        {
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75);
        }

        Assert.Equal(text, folded.Replace("\r\n ", ""));
    }

    [Fact]
    public void Parse_ReadsExtendedFields()
    {
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:full
            DTSTART:20261123T090000Z
            DTEND:20261123T100000Z
            SUMMARY:完整字段
            DESCRIPTION:第一行\n第二行
            URL:https://example.com/meet
            CATEGORIES:工作,重要
            TRANSP:TRANSPARENT
            STATUS:TENTATIVE
            CLASS:PRIVATE
            PRIORITY:1
            END:VEVENT
            END:VCALENDAR
            """;

        var evt = Assert.Single(IcsCalendar.ParseEvents(ics));

        Assert.Equal("第一行\n第二行", evt.Description);
        Assert.Equal("https://example.com/meet", evt.Url);
        Assert.Equal(new[] { "工作", "重要" }, evt.Categories);
        Assert.Equal("TRANSPARENT", evt.Transp);
        Assert.Equal("TENTATIVE", evt.Status);
        Assert.Equal("PRIVATE", evt.Class);
        Assert.Equal(1, evt.Priority);
    }

    [Fact]
    public void Parse_DefaultsAreSaneWhenFieldsAbsent()
    {
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:bare
            DTSTART:20261123T090000Z
            SUMMARY:只有必要字段
            END:VEVENT
            END:VCALENDAR
            """;

        var evt = Assert.Single(IcsCalendar.ParseEvents(ics));

        Assert.Empty(evt.Categories);
        Assert.Equal(string.Empty, evt.Url);
        Assert.Equal(string.Empty, evt.Transp);
        Assert.Equal(0, evt.Priority);
    }
}

public class RecurrenceExpanderTests
{
    private static IcsEvent Master(string rrule, DateTime start) => new()
    {
        Uid = "u",
        Summary = "x",
        Start = start,
        End = start.AddHours(1),
        RRule = rrule,
        TimeZoneId = "Asia/Shanghai",
    };

    [Fact]
    public void Weekly_ExpandsWithinWindow()
    {
        var master = Master("FREQ=WEEKLY;BYDAY=MO", new DateTime(2026, 11, 2, 9, 0, 0));
        var result = RecurrenceExpander.Expand(master, new DateTime(2026, 11, 1), new DateTime(2026, 12, 1));

        Assert.Equal(new[]
        {
            new DateTime(2026, 11, 2, 9, 0, 0),
            new DateTime(2026, 11, 9, 9, 0, 0),
            new DateTime(2026, 11, 16, 9, 0, 0),
            new DateTime(2026, 11, 23, 9, 0, 0),
            new DateTime(2026, 11, 30, 9, 0, 0),
        }, result);
    }

    [Fact]
    public void Daily_RespectsInterval()
    {
        var master = Master("FREQ=DAILY;INTERVAL=2", new DateTime(2026, 11, 1, 8, 0, 0));
        var result = RecurrenceExpander.Expand(master, new DateTime(2026, 11, 1), new DateTime(2026, 11, 8));

        Assert.Equal(new[]
        {
            new DateTime(2026, 11, 1, 8, 0, 0),
            new DateTime(2026, 11, 3, 8, 0, 0),
            new DateTime(2026, 11, 5, 8, 0, 0),
            new DateTime(2026, 11, 7, 8, 0, 0),
        }, result);
    }

    [Fact]
    public void Count_StopsAfterN()
    {
        var master = Master("FREQ=WEEKLY;COUNT=3", new DateTime(2026, 11, 2, 9, 0, 0));
        var result = RecurrenceExpander.Expand(master, new DateTime(2026, 11, 1), new DateTime(2026, 12, 31));

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Until_StopsOnDate()
    {
        var master = Master("FREQ=WEEKLY;UNTIL=20261116T000000Z", new DateTime(2026, 11, 2, 9, 0, 0));
        var result = RecurrenceExpander.Expand(master, new DateTime(2026, 11, 1), new DateTime(2026, 12, 31));

        Assert.All(result, d => Assert.True(d <= new DateTime(2026, 11, 16, 9, 0, 0)));
        Assert.Contains(new DateTime(2026, 11, 16, 9, 0, 0), result);
        Assert.DoesNotContain(new DateTime(2026, 11, 23, 9, 0, 0), result);
    }

    [Fact]
    public void Monthly_ByMonthDay()
    {
        var master = Master("FREQ=MONTHLY;BYMONTHDAY=15", new DateTime(2026, 1, 15, 10, 0, 0));
        var result = RecurrenceExpander.Expand(master, new DateTime(2026, 11, 1), new DateTime(2026, 12, 31));

        Assert.Equal(new[]
        {
            new DateTime(2026, 11, 15, 10, 0, 0),
            new DateTime(2026, 12, 15, 10, 0, 0),
        }, result);
    }

    [Fact]
    public void LongSeries_FastForwardsToWindow()
    {
        // 2010 年起的每日事件，只查 2026 年 11 月也应当很快且结果正确。
        var master = Master("FREQ=DAILY", new DateTime(2010, 1, 1, 7, 0, 0));
        var result = RecurrenceExpander.Expand(master, new DateTime(2026, 11, 1), new DateTime(2026, 11, 4));

        Assert.Equal(3, result.Count);
        Assert.Equal(new DateTime(2026, 11, 1, 7, 0, 0), result[0]);
    }

    [Fact]
    public void NonRecurring_ReturnsOnlyIfInWindow()
    {
        var master = new IcsEvent { Uid = "u", Start = new DateTime(2026, 11, 10, 9, 0, 0) };

        Assert.Single(RecurrenceExpander.Expand(master, new DateTime(2026, 11, 1), new DateTime(2026, 12, 1)));
        Assert.Empty(RecurrenceExpander.Expand(master, new DateTime(2026, 12, 1), new DateTime(2027, 1, 1)));
    }
}

public class MonthWeekLayoutTests
{
    private static readonly DateTime WeekStartDate = new(2026, 11, 2); // 周一

    private static CalendarEvent Evt(DateTime start, DateTime? end, bool allDay = false) => new()
    {
        Uid = Guid.NewGuid().ToString(),
        Summary = "e",
        StartAt = start,
        EndAt = end,
        IsAllDay = allDay,
    };

    [Fact]
    public void SingleDayEvent_OccupiesOneColumn()
    {
        var layout = MonthWeekLayout.Build(
            new[] { Evt(new DateTime(2026, 11, 3, 9, 0, 0), new DateTime(2026, 11, 3, 10, 0, 0)) },
            WeekStartDate);

        var bar = Assert.Single(layout.Bars);
        Assert.Equal(1, bar.StartColumn);
        Assert.Equal(1, bar.ColumnSpan);
        Assert.True(bar.RoundedStart);
        Assert.True(bar.RoundedEnd);
    }

    [Fact]
    public void MultiDayEvent_SpansColumns()
    {
        var layout = MonthWeekLayout.Build(
            new[] { Evt(new DateTime(2026, 11, 3), new DateTime(2026, 11, 6)) },
            WeekStartDate);

        var bar = Assert.Single(layout.Bars);
        Assert.Equal(1, bar.StartColumn);
        Assert.Equal(3, bar.ColumnSpan); // 3、4、5 三天
    }

    [Fact]
    public void CrossWeekEvent_FlagsContinuation()
    {
        // 从上周日 10-31 到本周三 11-4
        var layout = MonthWeekLayout.Build(
            new[] { Evt(new DateTime(2026, 10, 31), new DateTime(2026, 11, 4)) },
            WeekStartDate);

        var bar = Assert.Single(layout.Bars);
        Assert.Equal(0, bar.StartColumn);
        Assert.True(bar.ContinuesBefore);
        Assert.False(bar.ContinuesAfter);
        Assert.False(bar.RoundedStart);
    }

    [Fact]
    public void OverlappingEvents_UseSeparateLanes()
    {
        var layout = MonthWeekLayout.Build(
            new[]
            {
                Evt(new DateTime(2026, 11, 3), new DateTime(2026, 11, 5)),
                Evt(new DateTime(2026, 11, 4), new DateTime(2026, 11, 6)),
            },
            WeekStartDate);

        Assert.Equal(2, layout.Bars.Count);
        Assert.Equal(2, layout.Bars.Select(b => b.Lane).Distinct().Count());
    }

    [Fact]
    public void EventEndingAtMidnight_DoesNotGrabExtraColumn()
    {
        var layout = MonthWeekLayout.Build(
            new[] { Evt(new DateTime(2026, 11, 3, 22, 0, 0), new DateTime(2026, 11, 4, 0, 0, 0)) },
            WeekStartDate);

        var bar = Assert.Single(layout.Bars);
        Assert.Equal(1, bar.StartColumn);
        Assert.Equal(1, bar.ColumnSpan);
    }

    [Fact]
    public void ExceedingMaxLanes_CountsOverflow()
    {
        var events = Enumerable.Range(0, 5)
            .Select(_ => Evt(new DateTime(2026, 11, 3), new DateTime(2026, 11, 4)))
            .ToList();

        var layout = MonthWeekLayout.Build(events, WeekStartDate, maxLanes: 2);

        Assert.Equal(2, layout.UsedLanes);
        Assert.Equal(3, layout.Overflow[1]);
    }

    [Fact]
    public void EventsOutsideWeek_AreIgnored()
    {
        var layout = MonthWeekLayout.Build(
            new[] { Evt(new DateTime(2026, 11, 20), new DateTime(2026, 11, 21)) },
            WeekStartDate);

        Assert.True(layout.IsEmpty);
    }
}

public class CalendarSyncExpandTests
{
    private static RawCalendarItem Raw(string ics) => new()
    {
        CalendarHref = "/c/",
        ResourceHref = "/c/x.ics",
        ETag = "e1",
        Ics = ics,
    };

    [Fact]
    public void ExpandItems_SplitsRecurringIntoInstances()
    {
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:weekly
            DTSTART;TZID=Asia/Shanghai:20261102T090000
            DTEND;TZID=Asia/Shanghai:20261102T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO
            SUMMARY:周会
            END:VEVENT
            END:VCALENDAR
            """;

        var events = CalendarSyncService.ExpandItems(
            new[] { Raw(ics) },
            new DateTime(2026, 11, 1),
            new DateTime(2026, 12, 1));

        Assert.Equal(5, events.Count);
        Assert.All(events, e => Assert.Equal("周会", e.Summary));
        Assert.All(events, e => Assert.Equal(TimeSpan.FromHours(1), e.EndAt!.Value - e.StartAt));
    }

    [Fact]
    public void ExpandItems_OverrideReplacesOccurrence()
    {
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:weekly
            DTSTART;TZID=Asia/Shanghai:20261102T090000
            DTEND;TZID=Asia/Shanghai:20261102T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO
            SUMMARY:周会
            END:VEVENT
            BEGIN:VEVENT
            UID:weekly
            RECURRENCE-ID;TZID=Asia/Shanghai:20261109T090000
            DTSTART;TZID=Asia/Shanghai:20261109T140000
            DTEND;TZID=Asia/Shanghai:20261109T150000
            SUMMARY:周会（改到下午）
            END:VEVENT
            END:VCALENDAR
            """;

        var events = CalendarSyncService.ExpandItems(
            new[] { Raw(ics) },
            new DateTime(2026, 11, 1),
            new DateTime(2026, 12, 1));

        Assert.Equal(5, events.Count);
        var moved = events.Single(e => e.StartAt == new DateTime(2026, 11, 9, 14, 0, 0));
        Assert.Equal("周会（改到下午）", moved.Summary);
        Assert.DoesNotContain(events, e => e.StartAt == new DateTime(2026, 11, 9, 9, 0, 0));
    }

    [Fact]
    public void ExpandItems_SkipsExcludedOccurrence()
    {
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:weekly
            DTSTART;TZID=Asia/Shanghai:20261102T090000
            DTEND;TZID=Asia/Shanghai:20261102T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO
            EXDATE;TZID=Asia/Shanghai:20261116T090000
            SUMMARY:周会
            END:VEVENT
            END:VCALENDAR
            """;

        var events = CalendarSyncService.ExpandItems(
            new[] { Raw(ics) },
            new DateTime(2026, 11, 1),
            new DateTime(2026, 12, 1));

        Assert.Equal(4, events.Count);
        Assert.DoesNotContain(events, e => e.StartAt == new DateTime(2026, 11, 16, 9, 0, 0));
    }

    [Fact]
    public void ExpandItems_SkipsCancelledOverride()
    {
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:weekly
            DTSTART;TZID=Asia/Shanghai:20261102T090000
            DTEND;TZID=Asia/Shanghai:20261102T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO
            SUMMARY:周会
            END:VEVENT
            BEGIN:VEVENT
            UID:weekly
            RECURRENCE-ID;TZID=Asia/Shanghai:20261109T090000
            DTSTART;TZID=Asia/Shanghai:20261109T090000
            DTEND;TZID=Asia/Shanghai:20261109T100000
            STATUS:CANCELLED
            SUMMARY:周会（取消）
            END:VEVENT
            END:VCALENDAR
            """;

        var events = CalendarSyncService.ExpandItems(
            new[] { Raw(ics) },
            new DateTime(2026, 11, 1),
            new DateTime(2026, 12, 1));

        Assert.Equal(4, events.Count);
        Assert.DoesNotContain(events, e => e.StartAt == new DateTime(2026, 11, 9, 9, 0, 0));
    }
}

public class CalendarRepositoryTests
{
    private static (CalendarRepository Repo, SettingsService Settings, CalendarSyncService Sync) Create(string path)
    {
        var repo = new CalendarRepository(path);
        repo.Initialize();
        repo.MergeSources(new[] { new CalendarSource { Href = "/c/", DisplayName = "c", Color = "#123456FF" } });
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json"));
        var sync = new CalendarSyncService(repo, settings);
        return (repo, settings, sync);
    }

    [Fact]
    public void UpsertMaster_ThenLoadEventsExpandsRecurringSeries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var (repo, _, sync) = Create(path);
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = "/c/",
                ResourceHref = "/c/weekly.ics",
                ETag = "e1",
                Ics = """
                    BEGIN:VCALENDAR
                    BEGIN:VEVENT
                    UID:weekly
                    DTSTART;TZID=Asia/Shanghai:20261102T090000
                    DTEND;TZID=Asia/Shanghai:20261102T100000
                    RRULE:FREQ=WEEKLY;BYDAY=MO
                    SUMMARY:周会
                    END:VEVENT
                    END:VCALENDAR
                    """,
            });

            var events = sync.LoadEvents(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1));

            Assert.Equal(5, events.Count);
            Assert.All(events, e => Assert.Equal("#123456FF", e.Color));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void AddExclusion_ThenLoadEventsSkipsThatDate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var (repo, _, sync) = Create(path);
            const string master = """
                BEGIN:VCALENDAR
                BEGIN:VEVENT
                UID:weekly
                DTSTART;TZID=Asia/Shanghai:20261102T090000
                DTEND;TZID=Asia/Shanghai:20261102T100000
                RRULE:FREQ=WEEKLY;BYDAY=MO
                SUMMARY:周会
                END:VEVENT
                END:VCALENDAR
                """;
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = "/c/",
                ResourceHref = "/c/weekly.ics",
                Ics = master,
            });

            var excluded = IcsCalendar.AddExclusion(master, new DateTime(2026, 11, 16, 9, 0, 0));
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = "/c/",
                ResourceHref = "/c/weekly.ics",
                Ics = excluded,
            });

            var events = sync.LoadEvents(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1));

            Assert.Equal(4, events.Count);
            Assert.DoesNotContain(events, e => e.StartAt == new DateTime(2026, 11, 16, 9, 0, 0));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void UpsertOccurrence_OnlyReplacesOneDate()
    {
        const string master = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:weekly
            DTSTART;TZID=Asia/Shanghai:20261102T090000
            DTEND;TZID=Asia/Shanghai:20261102T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO
            SUMMARY:周会
            END:VEVENT
            END:VCALENDAR
            """;

        var patched = IcsCalendar.UpsertOccurrence(
            master,
            "weekly",
            new DateTime(2026, 11, 9, 9, 0, 0),
            new DateTime(2026, 11, 9, 10, 0, 0),
            allDay: false,
            new CalendarSyncService.CalendarEventDraft
            {
                Summary = "周会（改到下午）",
                Start = new DateTime(2026, 11, 9, 14, 0, 0),
                End = new DateTime(2026, 11, 9, 15, 0, 0),
            });

        var events = CalendarSyncService.ExpandItems(
            new[]
            {
                new RawCalendarItem
                {
                    CalendarHref = "/c/",
                    ResourceHref = "/c/weekly.ics",
                    Ics = patched,
                },
            },
            new DateTime(2026, 11, 1),
            new DateTime(2026, 12, 1));

        Assert.Equal(5, events.Count);
        Assert.Contains(events, e => e.StartAt == new DateTime(2026, 11, 2, 9, 0, 0));
        Assert.Contains(events, e => e.StartAt == new DateTime(2026, 11, 9, 14, 0, 0));
        Assert.Contains(events, e => e.StartAt == new DateTime(2026, 11, 30, 9, 0, 0));
        Assert.DoesNotContain(events, e => e.StartAt == new DateTime(2026, 11, 9, 9, 0, 0));
    }

    [Fact]
    public void RemovePending_KeepsNewerPayloadWrittenMeanwhile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new CalendarRepository(path);
            repo.Initialize();

            repo.AddPending("/c/1.ics", "update", "/c/", "e1", "v1");

            // 推送 v1 期间用户又拖了一次，队列被更新成 v2。
            repo.AddPending("/c/1.ics", "update", "/c/", "e1", "v2");

            // v1 推送成功后的清理不能把 v2 一起删掉。
            repo.RemovePending("/c/1.ics", "v1");
            Assert.Equal(1, repo.PendingCount());

            repo.RemovePending("/c/1.ics", "v2");
            Assert.Equal(0, repo.PendingCount());
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task MoveEventToCalendar_MovesLocalResourceBetweenCalendars()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var (repo, settings, sync) = Create(path);
            settings.Current.CalendarAppleId = "test@example.com";
            settings.Current.CalendarPasswordCipher = SecretProtector.Protect("x");
            repo.MergeSources(new[] { new CalendarSource { Href = "/work/", DisplayName = "工作" } });

            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = "/c/",
                ResourceHref = "/c/e1.ics",
                ETag = "etag1",
                Ics = """
                    BEGIN:VCALENDAR
                    BEGIN:VEVENT
                    UID:u1
                    DTSTART;TZID=Asia/Shanghai:20261128T090000
                    DTEND;TZID=Asia/Shanghai:20261128T100000
                    SUMMARY:黑客松决赛
                    END:VEVENT
                    END:VCALENDAR
                    """,
            });

            var existing = new CalendarEvent
            {
                CalendarHref = "/c/",
                ResourceHref = "/c/e1.ics",
                Uid = "u1",
                Summary = "黑客松决赛",
                StartAt = new DateTime(2026, 11, 28, 9, 0, 0),
                EndAt = new DateTime(2026, 11, 28, 10, 0, 0),
                ETag = "etag1",
            };

            // 测试环境连不上 iCloud：本地移动已生效，云端那步会抛「稍后同步」。
            try
            {
                await sync.MoveEventToCalendarAsync(existing, "/work/",
                    new CalendarSyncService.CalendarEventDraft
                    {
                        Summary = "黑客松决赛",
                        Start = new DateTime(2026, 11, 28, 9, 0, 0),
                        End = new DateTime(2026, 11, 28, 10, 0, 0),
                    });
            }
            catch (CalDavPendingException)
            {
            }

            // 旧集合的母事件与展开实例都应清掉，新集合应能读到这条日程。
            Assert.Null(repo.GetMasterIcs("/c/e1.ics"));
            Assert.NotNull(repo.GetMasterIcs("/work/e1.ics"));
            var visible = sync.LoadEvents(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1));
            Assert.Contains(visible, e => e.Summary == "黑客松决赛" && e.CalendarHref == "/work/");
            Assert.DoesNotContain(visible, e => e.CalendarHref == "/c/");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void AllDayRoundTrip_KeepsSingleDayEndInclusive()
    {
        // 全天事件在 ICS 里是排他端（28 号 -> DTEND 29 号）；
        // 解析后 EndAt 应是 29 号 0 点，编辑窗回填时要退一天显示 28 号。
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:allday1
            DTSTART;VALUE=DATE:20261128
            DTEND;VALUE=DATE:20261129
            SUMMARY:黑客松决赛
            END:VEVENT
            END:VCALENDAR
            """;

        var evt = Assert.Single(IcsCalendar.ParseEvents(ics));
        Assert.True(evt.IsAllDay);
        Assert.Equal(new DateTime(2026, 11, 28), evt.Start);
        Assert.Equal(new DateTime(2026, 11, 29), evt.End);

        // 写回时 draft.End 传含当天的 28 号，序列化后 DTEND 必须还是 29 号，不能越存越多。
        var written = IcsCalendar.PatchMasterEvent(ics, new CalendarSyncService.CalendarEventDraft
        {
            Summary = "黑客松决赛",
            Start = new DateTime(2026, 11, 28),
            End = new DateTime(2026, 11, 28),
            AllDay = true,
        }, sequence: 1);

        var rewritten = Assert.Single(IcsCalendar.ParseEvents(written));
        Assert.Equal(new DateTime(2026, 11, 28), rewritten.Start);
        Assert.Equal(new DateTime(2026, 11, 29), rewritten.End);
    }

    [Fact]
    public void MergeSources_PreservesVisibilityOnResync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new CalendarRepository(path);
            repo.Initialize();
            repo.MergeSources(new[]
            {
                new CalendarSource { Href = "/a/", DisplayName = "工作", Color = "#111111FF", SortOrder = 1 },
                new CalendarSource { Href = "/b/", DisplayName = "个人", Color = "#222222FF", SortOrder = 2 },
            });

            repo.SetSourceVisible("/a/", false);

            // 再次同步（名称变了）不应把用户的可见性设置覆盖回默认。
            repo.MergeSources(new[]
            {
                new CalendarSource { Href = "/a/", DisplayName = "工作(改)", Color = "#333333FF", SortOrder = 1 },
                new CalendarSource { Href = "/b/", DisplayName = "个人", Color = "#222222FF", SortOrder = 2 },
            });

            var sources = repo.GetSources();
            Assert.False(sources.Single(s => s.Href == "/a/").IsVisible);
            Assert.Equal("工作(改)", sources.Single(s => s.Href == "/a/").DisplayName);
            Assert.Equal("#333333FF", sources.Single(s => s.Href == "/a/").Color);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void GetEventsInRange_FiltersByVisibilityAndRange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new CalendarRepository(path);
            repo.Initialize();
            repo.MergeSources(new[]
            {
                new CalendarSource { Href = "/visible/", DisplayName = "显示", Color = "#00FF00FF" },
                new CalendarSource { Href = "/hidden/", DisplayName = "隐藏", Color = "#FF0000FF" },
            });
            repo.SetSourceVisible("/hidden/", false);

            repo.ReplaceEvents("/visible/", new List<CalendarEvent>
            {
                new() { CalendarHref = "/visible/", ResourceHref = "/visible/1.ics", Summary = "在内",
                    StartAt = new DateTime(2026, 11, 10, 9, 0, 0) },
                new() { CalendarHref = "/visible/", ResourceHref = "/visible/2.ics", Summary = "在外",
                    StartAt = new DateTime(2027, 3, 1, 9, 0, 0) },
            });
            repo.ReplaceEvents("/hidden/", new List<CalendarEvent>
            {
                new() { CalendarHref = "/hidden/", ResourceHref = "/hidden/1.ics", Summary = "隐藏的",
                    StartAt = new DateTime(2026, 11, 11, 9, 0, 0) },
            });

            var colors = repo.GetSources().ToDictionary(s => s.Href, s => s.Color);
            var result = repo.GetEventsInRange(
                new DateTime(2026, 11, 1), new DateTime(2026, 12, 1), colors);

            var only = Assert.Single(result);
            Assert.Equal("在内", only.Summary);
            Assert.Equal("#00FF00FF", only.Color);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void AddEvent_AppearsImmediatelyAndRemoveClearsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new CalendarRepository(path);
            repo.Initialize();
            repo.MergeSources(new[] { new CalendarSource { Href = "/c/", DisplayName = "c" } });

            // 模拟「创建成功后立即写缓存」：不经过 ReplaceEvents，也要能马上查出来。
            repo.AddEvent(new CalendarEvent
            {
                CalendarHref = "/c/",
                ResourceHref = "/c/new.ics",
                Summary = "新建日程",
                StartAt = new DateTime(2026, 11, 25, 9, 0, 0),
                EndAt = new DateTime(2026, 11, 25, 10, 0, 0),
            });

            var colors = repo.GetSources().ToDictionary(s => s.Href, s => s.Color);
            var found = repo.GetEventsInRange(
                new DateTime(2026, 11, 24), new DateTime(2026, 11, 26), colors);
            Assert.Single(found);
            Assert.Equal("新建日程", found[0].Summary);

            // 删除时按资源清缓存，界面就不会残留。
            repo.RemoveEventsByResource("/c/new.ics");
            Assert.Empty(repo.GetEventsInRange(
                new DateTime(2026, 11, 24), new DateTime(2026, 11, 26), colors));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void ReplaceEvents_DropsStaleRows()    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new CalendarRepository(path);
            repo.Initialize();
            repo.MergeSources(new[] { new CalendarSource { Href = "/c/", DisplayName = "c" } });

            repo.ReplaceEvents("/c/", new List<CalendarEvent>
            {
                new() { CalendarHref = "/c/", ResourceHref = "/c/1.ics", Summary = "旧", StartAt = DateTime.Today },
            });
            repo.ReplaceEvents("/c/", new List<CalendarEvent>());

            var colors = repo.GetSources().ToDictionary(s => s.Href, s => s.Color);
            Assert.Empty(repo.GetEventsInRange(DateTime.Today.AddYears(-1), DateTime.Today.AddYears(1), colors));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void LocalSource_IsCreatedAndDeletable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new CalendarRepository(path);
            repo.Initialize();

            var href = repo.AddLocalSource("个人", "#4D8FE3");

            Assert.True(CalendarRepository.IsLocalHref(href));
            var sources = repo.GetSources();
            Assert.Single(sources);
            Assert.Equal("个人", sources[0].DisplayName);
            Assert.True(sources[0].IsVisible);

            repo.RenameSource(href, "私人");
            Assert.Equal("私人", repo.GetSources()[0].DisplayName);

            repo.DeleteSource(href);
            Assert.Empty(repo.GetSources());
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void LocalSource_SurvivesCloudMerge()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new CalendarRepository(path);
            repo.Initialize();
            var localHref = repo.AddLocalSource("本地", "#4D8FE3");

            // 模拟一次云端同步：只合并服务器上发现的集合。
            repo.MergeSources(new[] { new CalendarSource { Href = "/c/", DisplayName = "云端", Color = "#FF0000FF" } });

            var sources = repo.GetSources();
            Assert.Equal(2, sources.Count);
            Assert.Contains(sources, s => s.Href == localHref && s.DisplayName == "本地");
            Assert.Contains(sources, s => s.Href == "/c/");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void DeleteSource_RemovesItsEventsOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new CalendarRepository(path);
            repo.Initialize();
            var localHref = repo.AddLocalSource("本地", "#4D8FE3");
            repo.MergeSources(new[] { new CalendarSource { Href = "/c/", DisplayName = "云端", Color = "#FF0000FF" } });

            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = localHref,
                ResourceHref = localHref + "/a.ics",
                ETag = "",
                Ics = IcsFor("a", "本地日程"),
            });
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = "/c/",
                ResourceHref = "/c/b.ics",
                ETag = "",
                Ics = IcsFor("b", "云端日程"),
            });

            repo.DeleteSource(localHref);

            var masters = repo.GetVisibleMasters();
            Assert.Single(masters);
            Assert.Equal("/c/", masters[0].CalendarHref);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Merge_CloudWinsOnSameUid()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var (repo, settings, sync) = Create(path);
            var localHref = repo.AddLocalSource("本地", "#4D8FE3");

            // 云端已有 UID=dup 的同一日程
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = "/c/",
                ResourceHref = "/c/dup.ics",
                ETag = "",
                Ics = IcsFor("dup", "云端版本"),
            });
            // 本地也有一条同 UID
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = localHref,
                ResourceHref = localHref + "/dup.ics",
                ETag = "",
                Ics = IcsFor("dup", "本地版本"),
            });

            var merge = new CalendarMergeService(repo, sync);
            var choices = merge.BuildChoices();
            Assert.Single(choices);
            choices[0].Action = MergeAction.MergeInto;
            choices[0].TargetHref = "/c/";

            var outcome = await merge.ApplyAsync(choices);

            // 同 UID 走云端版本，不计入上传，本地副本随后被清掉。
            Assert.Equal(0, outcome.UploadedCount);
            Assert.Equal(1, outcome.CloudWonCount);
            Assert.DoesNotContain(repo.GetSources(), s => s.Href == localHref);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Merge_UploadFailureKeepsLocalCalendar()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var (repo, _, sync) = Create(path);
            var localHref = repo.AddLocalSource("别丢", "#4D8FE3");
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = localHref,
                ResourceHref = localHref + "/f.ics",
                ETag = "",
                Ics = IcsFor("f", "传不上去的"),
            });

            var merge = new CalendarMergeService(repo, sync);
            var choices = merge.BuildChoices();
            choices[0].Action = MergeAction.MergeInto;
            choices[0].TargetHref = "/c/";

            // 测试环境没有账号凭据，上传必然失败。
            var outcome = await merge.ApplyAsync(choices);

            Assert.Equal(1, outcome.FailedCount);
            Assert.Equal(1, outcome.KeptCalendarCount);

            // 关键：失败时本地日历和里面的日程都必须还在。
            Assert.Contains(repo.GetSources(), s => s.Href == localHref);
            Assert.Equal(1, repo.CountMastersForCalendar(localHref));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void BackupIcs_ContainsAllEventsOfCalendar()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var (repo, _, sync) = Create(path);
            var localHref = repo.AddLocalSource("要备份的", "#4D8FE3");
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = localHref,
                ResourceHref = localHref + "/b1.ics",
                ETag = "",
                Ics = IcsFor("b1", "备份第一条"),
            });
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = localHref,
                ResourceHref = localHref + "/b2.ics",
                ETag = "",
                Ics = IcsFor("b2", "备份第二条"),
            });

            var merge = new CalendarMergeService(repo, sync);
            var ics = merge.BuildBackupIcs(localHref);

            Assert.Contains("BEGIN:VCALENDAR", ics);
            Assert.Contains("UID:b1", ics);
            Assert.Contains("UID:b2", ics);
            Assert.Contains("SUMMARY:备份第一条", ics);
            Assert.Contains("SUMMARY:备份第二条", ics);
            // 每个 VEVENT 只出现一次，不能因为重组而重复。
            Assert.Equal(2, ics.Split("BEGIN:VEVENT").Length - 1);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Merge_DiscardRemovesLocalCalendar()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var (repo, _, sync) = Create(path);
            var localHref = repo.AddLocalSource("临时", "#4D8FE3");
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = localHref,
                ResourceHref = localHref + "/x.ics",
                ETag = "",
                Ics = IcsFor("x", "要丢掉的"),
            });

            var merge = new CalendarMergeService(repo, sync);
            var choices = merge.BuildChoices();
            choices[0].Action = MergeAction.Discard;

            var outcome = await merge.ApplyAsync(choices);

            Assert.Equal(1, outcome.DiscardedCount);
            Assert.DoesNotContain(repo.GetSources(), s => s.Href == localHref);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Merge_KeepLocalLeavesEverythingInPlace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var (repo, _, sync) = Create(path);
            var localHref = repo.AddLocalSource("保留", "#4D8FE3");
            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = localHref,
                ResourceHref = localHref + "/k.ics",
                ETag = "",
                Ics = IcsFor("k", "留在本地"),
            });

            var merge = new CalendarMergeService(repo, sync);
            var choices = merge.BuildChoices();
            choices[0].Action = MergeAction.KeepLocal;

            var outcome = await merge.ApplyAsync(choices);

            Assert.Equal(1, outcome.KeptLocalCount);
            Assert.Contains(repo.GetSources(), s => s.Href == localHref);
            Assert.Equal(1, repo.CountMastersForCalendar(localHref));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Merge_HasLocalDataOnlyWhenLocalHasEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.db");
        try
        {
            var (repo, _, sync) = Create(path);
            var merge = new CalendarMergeService(repo, sync);

            // 新建的空本地日历不需要裁决。
            var localHref = repo.AddLocalSource("空的", "#4D8FE3");
            Assert.False(merge.HasLocalDataToReconcile());

            repo.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = localHref,
                ResourceHref = localHref + "/n.ics",
                ETag = "",
                Ics = IcsFor("n", "有内容了"),
            });

            Assert.True(merge.HasLocalDataToReconcile());
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static string IcsFor(string uid, string summary) => string.Join("\r\n", new[]
    {
        "BEGIN:VCALENDAR",
        "BEGIN:VEVENT",
        $"UID:{uid}",
        "DTSTART:20261124T090000",
        "DTEND:20261124T100000",
        $"SUMMARY:{summary}",
        "END:VEVENT",
        "END:VCALENDAR",
        string.Empty,
    });

    private static void TryDelete(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(path + suffix))
                {
                    File.Delete(path + suffix);
                }
            }
            catch
            {
                // 测试清理失败不影响结果
            }
        }
    }
}
