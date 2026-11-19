using System.Text.RegularExpressions;

namespace ToDooo.Helpers;

/// <summary>
/// 从标题里识别中文日期/时间（今天、明天、周五、3月5日、3天后、10点半、18:30…），
/// 识别到就把这段时间从标题里摘掉，返回干净的标题。
/// </summary>
public static class NaturalDateParser
{
    private static readonly Regex DatePattern = new(
        @"(?<today>大后天|今天|明天|后天)|(?<weekday>(?<next>下)?(?:周|星期|礼拜)(?<wd>[一二三四五六日天1-7]))|(?<md>(?<month>\d{1,2}|[〇零一二两三四五六七八九十]{1,3})\s*月\s*(?<day>\d{1,2}|[〇零一二两三四五六七八九十]{1,3})\s*[日号])|(?<days>(?<n>\d{1,2}|[〇零一二两三四五六七八九十]{1,3})\s*天后)",
        RegexOptions.Compiled);

    private static readonly Regex CnTimePattern = new(
        @"(?<period>凌晨|早上|上午|中午|下午|傍晚|晚上)?\s*(?<h>\d{1,2}|[〇零一二两三四五六七八九十]{1,3})\s*(?:点|时)(?:钟)?\s*(?:(?<half>半)|(?<quarter>一刻|三刻)|(?<mm>\d{1,2}|[〇零一二两三四五六七八九十]{1,3})\s*分?)?",
        RegexOptions.Compiled);

    private static readonly Regex ClockTimePattern = new(
        @"(?<h>\d{1,2})\s*[:：]\s*(?<m>\d{2})",
        RegexOptions.Compiled);

    public static bool TryParse(string? text, DateTime now, out DateTime due, out string title)
    {
        due = default;
        title = text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var work = text;
        var hasDate = false;
        var weekdayBased = false;
        var date = now.Date;

        var dateMatch = DatePattern.Match(work);
        if (dateMatch.Success)
        {
            if (dateMatch.Groups["today"].Success)
            {
                date = now.Date.AddDays(dateMatch.Groups["today"].Value switch
                {
                    "今天" => 0,
                    "明天" => 1,
                    "后天" => 2,
                    "大后天" => 3,
                    _ => 0,
                });
                hasDate = true;
            }
            else if (dateMatch.Groups["weekday"].Success)
            {
                var target = WeekdayNumber(dateMatch.Groups["wd"].Value[0]);
                var delta = ((target - (int)now.DayOfWeek) + 7) % 7;
                if (dateMatch.Groups["next"].Success)
                {
                    // "下周X" 指的是下一个自然周（周一起算）：落在本周内就再加 7 天。
                    var isoDayOfWeek = ((int)now.DayOfWeek + 6) % 7 + 1;
                    if (delta <= 7 - isoDayOfWeek)
                    {
                        delta += 7;
                    }
                }

                date = now.Date.AddDays(delta);
                hasDate = true;
                weekdayBased = true;
            }
            else if (dateMatch.Groups["md"].Success)
            {
                if (!TryParseNumber(dateMatch.Groups["month"].Value, out var month) ||
                    !TryParseNumber(dateMatch.Groups["day"].Value, out var day))
                {
                    month = 0;
                    day = 0;
                }

                if (month is >= 1 and <= 12 && day is >= 1 and <= 31)
                {
                    var safeDay = Math.Min(day, DateTime.DaysInMonth(now.Year, month));
                    var candidate = new DateTime(now.Year, month, safeDay);
                    if (candidate.Date < now.Date)
                    {
                        candidate = candidate.AddYears(1);
                    }

                    date = candidate;
                    hasDate = true;
                }
            }
            else if (dateMatch.Groups["days"].Success)
            {
                if (!TryParseNumber(dateMatch.Groups["n"].Value, out var days))
                {
                    days = 0;
                }

                if (days is > 0 and <= 365)
                {
                    date = now.Date.AddDays(days);
                    hasDate = true;
                }
            }

            if (hasDate)
            {
                work = Remove(work, dateMatch);
            }
        }

        TimeSpan? time = null;
        var timeMatch = CnTimePattern.Match(work);
        if (timeMatch.Success)
        {
            if (!TryParseNumber(timeMatch.Groups["h"].Value, out var hour))
            {
                hour = -1;
            }

            var minute = 0;
            if (!timeMatch.Groups["mm"].Success || !TryParseNumber(timeMatch.Groups["mm"].Value, out minute))
            {
                minute = timeMatch.Groups["quarter"].Success
                    ? (timeMatch.Groups["quarter"].Value == "一刻" ? 15 : 45)
                    : timeMatch.Groups["half"].Success ? 30 : 0;
            }

            var period = timeMatch.Groups["period"].Value;
            if (period is "下午" or "傍晚" or "晚上")
            {
                if (hour < 12)
                {
                    hour += 12;
                }
            }
            else if (period == "中午")
            {
                if (hour < 6)
                {
                    hour += 12;
                }
            }
            else if (period is "凌晨" or "早上" or "上午")
            {
                if (hour == 12)
                {
                    hour = 0;
                }
            }

            if (hour is >= 0 and <= 23 && minute is >= 0 and <= 59)
            {
                time = new TimeSpan(hour, minute, 0);
                work = Remove(work, timeMatch);
            }
        }

        if (time is null)
        {
            var clockMatch = ClockTimePattern.Match(work);
            if (clockMatch.Success)
            {
                var hour = int.Parse(clockMatch.Groups["h"].Value);
                var minute = int.Parse(clockMatch.Groups["m"].Value);
                if (hour is >= 0 and <= 23 && minute is >= 0 and <= 59)
                {
                    time = new TimeSpan(hour, minute, 0);
                    work = Remove(work, clockMatch);
                }
            }
        }

        if (!hasDate && time is null)
        {
            return false;
        }

        if (hasDate && time is { } explicitTime)
        {
            due = date + explicitTime;
            if (weekdayBased && due < now)
            {
                due = due.AddDays(7);
            }
        }
        else if (hasDate)
        {
            // 只说了日期：今天没给时间就顺延一小时，其它日子按上午 9 点。
            due = date == now.Date ? RoundUpToFiveMinutes(now.AddHours(1)) : date.AddHours(9);
        }
        else if (time is { } onlyTime)
        {
            due = now.Date + onlyTime;
            if (due <= now)
            {
                due = due.AddDays(1);
            }
        }

        var cleaned = CleanTitle(work);
        if (cleaned.Length == 0)
        {
            // 标题里除了时间什么都不剩（比如只输入"明天"），当成没识别，保持原样。
            due = default;
            return false;
        }

        title = cleaned;
        return true;
    }

    private static string Remove(string source, Match match)
        => source.Remove(match.Index, match.Length);

    private static string CleanTitle(string value)
    {
        var collapsed = Regex.Replace(value, @"\s{2,}", " ");
        return collapsed.Trim(' ', '\t', '，', ',', '。', '.', '、', '；', ';', '-', '·', '：', ':');
    }

    private static DateTime RoundUpToFiveMinutes(DateTime value)
    {
        var remainder = value.Minute % 5;
        var rounded = remainder == 0 ? value : value.AddMinutes(5 - remainder);
        return new DateTime(rounded.Year, rounded.Month, rounded.Day, rounded.Hour, rounded.Minute, 0);
    }

    private static bool TryParseNumber(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.All(char.IsAsciiDigit))
        {
            return int.TryParse(text, out value);
        }

        var tenIndex = text.IndexOf('十');
        if (tenIndex < 0)
        {
            return TryParseSingleDigit(text, out value);
        }

        var left = tenIndex == 0 ? "一" : text[..tenIndex];
        if (!TryParseSingleDigit(left, out var tens) || tens is < 1 or > 9)
        {
            return false;
        }

        var ones = 0;
        if (tenIndex + 1 < text.Length && !TryParseSingleDigit(text[(tenIndex + 1)..], out ones))
        {
            return false;
        }

        value = tens * 10 + ones;
        return true;
    }

    private static bool TryParseSingleDigit(string text, out int value)
    {
        value = text switch
        {
            "〇" or "零" => 0,
            "一" => 1,
            "二" or "两" => 2,
            "三" => 3,
            "四" => 4,
            "五" => 5,
            "六" => 6,
            "七" => 7,
            "八" => 8,
            "九" => 9,
            _ => -1,
        };
        return value >= 0;
    }

    private static int WeekdayNumber(char value) => value switch
    {
        '一' or '1' => 1,
        '二' or '2' => 2,
        '三' or '3' => 3,
        '四' or '4' => 4,
        '五' or '5' => 5,
        '六' or '6' => 6,
        '日' or '天' or '7' => 0,
        _ => 0,
    };
}
