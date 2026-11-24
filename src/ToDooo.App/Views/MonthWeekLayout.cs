using ToDooo.Models;

namespace ToDooo.Views;

/// <summary>跨日色条在某一周内的位置：第几列开始、跨几列、放在第几个槽位。</summary>
public sealed record EventBar(CalendarEvent Event, int StartColumn, int ColumnSpan, int Lane,
    bool ContinuesBefore, bool ContinuesAfter)
{
    public bool RoundedStart => !ContinuesBefore;

    public bool RoundedEnd => !ContinuesAfter;
}

/// <summary>某一周日历条目的排布结果：色条 + 每天被挤出去的数量。</summary>
public sealed class WeekLayout
{
    public List<EventBar> Bars { get; } = new();

    /// <summary>每天（列 0-6）因槽位不够没画出来的事件数。</summary>
    public int[] Overflow { get; } = new int[7];

    public int UsedLanes { get; set; }

    public bool IsEmpty => Bars.Count == 0 && Overflow.All(count => count == 0);
}

/// <summary>
/// 月视图的周排布算法。把与本周相交的事件裁到本周，贪心分配槽位（先按起始列、再按长度），
/// 同槽位内互不重叠，从而画出类似系统日历的连续色条。纯逻辑，不依赖任何 UI 类型。
/// </summary>
public static class MonthWeekLayout
{
    /// <summary>
    /// 计算一周的色条布局。
    /// weekStart 是周一 0 点；maxLanes 为 0 表示不限。
    /// </summary>
    public static WeekLayout Build(IEnumerable<CalendarEvent> events, DateTime weekStart, int maxLanes = 0)
    {
        var weekEndDate = weekStart.AddDays(7);
        var layout = new WeekLayout();

        // 先裁到本周：起止都夹进 [weekStart, weekEnd)，记录原本是否越界。
        var candidates = new List<(CalendarEvent Event, int StartCol, int EndCol, bool Before, bool After)>();

        foreach (var evt in events)
        {
            var evtStart = evt.StartAt;
            var evtEnd = evt.EndAt ?? evt.StartAt;
            if (evtEnd <= evtStart)
            {
                evtEnd = evtStart.AddMinutes(30);
            }

            if (evt.IsAllDay && evtEnd <= evtStart)
            {
                evtEnd = evtStart.AddDays(1);
            }

            if (evtEnd <= weekStart || evtStart >= weekEndDate)
            {
                continue;
            }

            var clampedStart = evtStart < weekStart ? weekStart : evtStart;
            var clampedEnd = evtEnd > weekEndDate ? weekEndDate : evtEnd;
            var startCol = Math.Clamp((clampedStart.Date - weekStart.Date).Days, 0, 6);

            // 结束时间落在 0 点整时算前一天结束，避免多占一列。
            var endDate = clampedEnd.Date;
            if (clampedEnd.TimeOfDay == TimeSpan.Zero && endDate > clampedStart.Date)
            {
                endDate = endDate.AddDays(-1);
            }

            var endCol = Math.Clamp((endDate - weekStart.Date).Days, startCol, 6);

            // 元组里直接写 a < b 会被当成泛型参数，必须加括号。
            candidates.Add((evt, startCol, endCol, (evtStart < weekStart), (evtEnd > weekEndDate)));
        }

        // 全天/跨日事件优先占上面的槽位，再按开始时间、长度排。
        var ordered = candidates
            .OrderByDescending(c => c.Event.IsAllDay)
            .ThenBy(c => c.StartCol)
            .ThenByDescending(c => c.EndCol - c.StartCol)
            .ThenBy(c => c.Event.StartAt)
            .ToList();

        // laneOccupied[lane][column] 记录该槽位该列是否已被占用。
        var laneOccupied = new List<bool[]>();

        foreach (var candidate in ordered)
        {
            var span = candidate.EndCol - candidate.StartCol + 1;
            var lane = -1;
            for (var i = 0; i < laneOccupied.Count; i++)
            {
                if (IsFree(laneOccupied[i], candidate.StartCol, candidate.EndCol))
                {
                    lane = i;
                    break;
                }
            }

            if (lane < 0)
            {
                if (maxLanes > 0 && laneOccupied.Count >= maxLanes)
                {
                    for (var col = candidate.StartCol; col <= candidate.EndCol; col++)
                    {
                        layout.Overflow[col]++;
                    }

                    continue;
                }

                laneOccupied.Add(new bool[7]);
                lane = laneOccupied.Count - 1;
            }

            for (var col = candidate.StartCol; col <= candidate.EndCol; col++)
            {
                laneOccupied[lane][col] = true;
            }

            layout.Bars.Add(new EventBar(candidate.Event, candidate.StartCol, span, lane,
                candidate.Before, candidate.After));
        }

        layout.UsedLanes = laneOccupied.Count;
        return layout;
    }

    private static bool IsFree(bool[] lane, int startCol, int endCol)
    {
        for (var col = startCol; col <= endCol; col++)
        {
            if (lane[col])
            {
                return false;
            }
        }

        return true;
    }
}
