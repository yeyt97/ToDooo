namespace ToDooo.Services;

/// <summary>
/// RRULE 展开。只覆盖 RFC 5545 里日历实际会用到的子集：
/// FREQ=DAILY/WEEKLY/MONTHLY/YEARLY，配 INTERVAL / COUNT / UNTIL / BYDAY / BYMONTHDAY。
/// BYSETPOS、WKST 等冷门组合不处理，遇到就退化成按 FREQ 基础规则展开。
/// </summary>
public static class RecurrenceExpander
{
    /// <summary>
    /// 展开母事件到 [from, to) 窗口内的所有开始时间。
    /// </summary>
    public static List<DateTime> Expand(IcsEvent master, DateTime from, DateTime to, int maxInstances = 4000)
    {
        var results = new List<DateTime>();
        if (string.IsNullOrWhiteSpace(master.RRule))
        {
            if (master.Start >= from && master.Start < to)
            {
                results.Add(master.Start);
            }

            return results;
        }

        var rule = ParseRule(master.RRule);
        if (!rule.TryGetValue("FREQ", out var freq))
        {
            return results;
        }

        var interval = rule.TryGetValue("INTERVAL", out var intervalText) && int.TryParse(intervalText, out var parsedInterval)
            ? Math.Max(1, parsedInterval)
            : 1;

        var count = rule.TryGetValue("COUNT", out var countText) && int.TryParse(countText, out var parsedCount)
            ? Math.Max(0, parsedCount)
            : 0;

        var until = rule.TryGetValue("UNTIL", out var untilText)
            ? IcsCalendar.ParseStamp(untilText, master.TimeZoneId)
            : null;

        var byDay = ParseByDay(rule.GetValueOrDefault("BYDAY"));
        var byMonthDay = ParseIntList(rule.GetValueOrDefault("BYMONTHDAY"));

        // 循环事件的时/分/秒固定沿用母事件，只改日期。
        var timeOfDay = master.Start.TimeOfDay;
        var startDate = master.Start.Date;

        // 先跳过窗口之前的迭代，避免长系列逐日走到窗口。
        var cursor = startDate;
        var emitted = 0;
        if (cursor < from.Date.AddDays(-1))
        {
            var (skipEmits, skipDate) = FastForward(freq, cursor, from.Date.AddDays(-1), interval, master.Start.Day,
                byDay, byMonthDay, startDate, count);
            emitted = skipEmits;
            cursor = skipDate;
        }

        var guard = 0;
        while (cursor < to.Date.AddDays(1) && guard++ < maxInstances * 8)
        {
            // 苹果写 UNTIL 常用日期粒度（如 20241004），表示「这一天仍然有效」，
            // 所以按日期比较而不是按瞬间比较，避免把最后一天的当天实例漏掉。
            if (until is { } limit && cursor.Date > limit.Date)
            {
                break;
            }

            if (Matches(freq, cursor, startDate, interval, byDay, byMonthDay))
            {
                emitted++;
                var occurrence = cursor.Add(timeOfDay);
                if (occurrence >= from && occurrence < to)
                {
                    results.Add(occurrence);
                }

                if (count > 0 && emitted >= count)
                {
                    break;
                }
            }

            cursor = cursor.AddDays(1);

            if (results.Count >= maxInstances)
            {
                break;
            }
        }

        return results;
    }

    private static (int Emitted, DateTime Cursor) FastForward(
        string freq, DateTime cursor, DateTime target, int interval, int dayOfMonth,
        List<ByDaySpec> byDay, List<int> byMonthDay, DateTime seriesStart, int count)
    {
        var emitted = 0;
        switch (freq)
        {
            case "DAILY":
            {
                var days = (int)(target - cursor).TotalDays;
                var steps = days <= 0 ? 0 : days / interval;
                if (steps > 0)
                {
                    cursor = seriesStart.AddDays(steps * interval);
                    emitted = steps;
                }

                break;
            }

            case "WEEKLY":
            {
                var days = (target - cursor).TotalDays;
                var weeks = days <= 0 ? 0 : (int)(days / (7 * interval));
                if (weeks > 0)
                {
                    cursor = seriesStart.AddDays(weeks * 7 * interval);
                    if (byDay.Count == 0)
                    {
                        emitted = weeks;
                    }
                    else
                    {
                        // 多 BYDAY 时每周可产出多次，保守起见不估算，交给主循环计数。
                        emitted = 0;
                        cursor = seriesStart.AddDays(Math.Max(0, weeks - 1) * 7 * interval);
                    }
                }

                break;
            }

            case "MONTHLY":
            {
                var months = (target.Year - cursor.Year) * 12 + (target.Month - cursor.Month);
                months -= months % interval;
                if (months > 0)
                {
                    cursor = seriesStart.AddMonths(months);
                    if (byMonthDay.Count <= 1 && byDay.Count <= 1)
                    {
                        emitted = months / interval;
                    }
                    else
                    {
                        emitted = 0;
                        cursor = seriesStart.AddMonths(Math.Max(0, months - interval));
                    }
                }

                break;
            }

            case "YEARLY":
            {
                var years = target.Year - cursor.Year;
                years -= years % interval;
                if (years > 0)
                {
                    cursor = seriesStart.AddYears(years);
                    emitted = years / interval;
                }

                break;
            }
        }

        if (count > 0 && emitted >= count)
        {
            emitted = count;
        }

        return (emitted, cursor);
    }

    private static bool Matches(
        string freq, DateTime date, DateTime seriesStart, int interval,
        List<ByDaySpec> byDay, List<int> byMonthDay)
    {
        switch (freq)
        {
            case "DAILY":
                return (date - seriesStart).Days % interval == 0;

            case "WEEKLY":
            {
                var weeks = (int)((date - seriesStart).TotalDays) / 7;
                if (weeks % interval != 0)
                {
                    return false;
                }

                return byDay.Count == 0
                    ? date.DayOfWeek == seriesStart.DayOfWeek
                    : byDay.Any(spec => spec.Day == date.DayOfWeek && OrdinalMatches(spec, date, weekly: true));
            }

            case "MONTHLY":
            {
                var months = (date.Year - seriesStart.Year) * 12 + (date.Month - seriesStart.Month);
                if (months % interval != 0)
                {
                    return false;
                }

                if (byMonthDay.Count > 0)
                {
                    return byMonthDay.Contains(date.Day);
                }

                if (byDay.Count > 0)
                {
                    return byDay.Any(spec => spec.Day == date.DayOfWeek && OrdinalMatches(spec, date, weekly: false));
                }

                return date.Day == seriesStart.Day;
            }

            case "YEARLY":
                return (date.Year - seriesStart.Year) % interval == 0
                       && date.Month == seriesStart.Month
                       && date.Day == seriesStart.Day;

            default:
                return false;
        }
    }

    /// <summary>BYDAY 里带序号时（如 2TU / -1FR）判断是不是当月/当周第几个。</summary>
    private static bool OrdinalMatches(ByDaySpec spec, DateTime date, bool weekly)
    {
        if (spec.Ordinal == 0)
        {
            return true;
        }

        if (weekly)
        {
            // 周维度不区分序号，按同一天处理。
            return true;
        }

        if (spec.Ordinal > 0)
        {
            var occurrence = (date.Day - 1) / 7 + 1;
            return occurrence == spec.Ordinal;
        }

        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        var fromEnd = (daysInMonth - date.Day) / 7 + 1;
        return fromEnd == -spec.Ordinal;
    }

    private sealed record ByDaySpec(DayOfWeek Day, int Ordinal);

    private static List<ByDaySpec> ParseByDay(string? token)
    {
        var specs = new List<ByDaySpec>();
        if (string.IsNullOrWhiteSpace(token))
        {
            return specs;
        }

        foreach (var raw in token.Split(','))
        {
            var item = raw.Trim().ToUpperInvariant();
            if (item.Length < 2)
            {
                continue;
            }

            var code = item[^2..];
            var ordinalText = item[..^2];
            var day = code switch
            {
                "SU" => DayOfWeek.Sunday,
                "MO" => DayOfWeek.Monday,
                "TU" => DayOfWeek.Tuesday,
                "WE" => DayOfWeek.Wednesday,
                "TH" => DayOfWeek.Thursday,
                "FR" => DayOfWeek.Friday,
                "SA" => DayOfWeek.Saturday,
                _ => (DayOfWeek?)null,
            };
            if (day is not { } resolvedDay)
            {
                continue;
            }

            var ordinal = int.TryParse(ordinalText, out var parsedOrdinal) ? parsedOrdinal : 0;
            specs.Add(new ByDaySpec(resolvedDay, ordinal));
        }

        return specs;
    }

    private static List<int> ParseIntList(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new List<int>();
        }

        var values = new List<int>();
        foreach (var raw in token.Split(','))
        {
            if (int.TryParse(raw.Trim(), out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static Dictionary<string, string> ParseRule(string rrule)
    {
        var rule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in rrule.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                rule[part[..eq].Trim().ToUpperInvariant()] = part[(eq + 1)..].Trim();
            }
        }

        return rule;
    }

    private static string? GetValueOrDefault(this Dictionary<string, string> source, string key) =>
        source.TryGetValue(key, out var value) ? value : null;
}
