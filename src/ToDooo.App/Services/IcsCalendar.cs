using System.Globalization;
using System.Text;

namespace ToDooo.Services;

/// <summary>一个 VEVENT 组件的原始字段（已展开折行）。</summary>
public sealed class IcsEvent
{
    public Dictionary<string, List<string>> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Uid { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string RRule { get; set; } = string.Empty;

    /// <summary>URL 属性，通常是会议链接或相关页面。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>CATEGORIES，iCalendar 用逗号分隔多个分类名。</summary>
    public List<string> Categories { get; } = new();

    /// <summary>TRANSP：OPAQUE=忙，TRANSPARENT=空闲。</summary>
    public string Transp { get; set; } = string.Empty;

    /// <summary>STATUS：CONFIRMED / TENTATIVE / CANCELLED。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>CLASS：PUBLIC / PRIVATE / CONFIDENTIAL。</summary>
    public string Class { get; set; } = string.Empty;

    /// <summary>PRIORITY：0（未指定）/ 1–4 高 / 5 中 / 6–9 低。</summary>
    public int Priority { get; set; }

    public DateTime Start { get; set; }

    public DateTime? End { get; set; }

    public bool IsAllDay { get; set; }

    public DateTime? RecurrenceId { get; set; }

    /// <summary>EXDATE：从循环里排除掉的实例开始时间（已转成本地时间）。</summary>
    public List<DateTime> ExceptionDates { get; } = new();

    public string TimeZoneId { get; set; } = string.Empty;

    public TimeSpan Duration => End is { } end && end > Start ? end - Start : TimeSpan.FromMinutes(30);

    /// <summary>取某属性的所有值（EXDATE 可能多行）。</summary>
    public IReadOnlyList<string> GetValues(string name) =>
        Properties.TryGetValue(name, out var values) ? values : Array.Empty<string>();

    public string? GetValue(string name) =>
        Properties.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}

/// <summary>极简 iCalendar 读写：只关心 VEVENT，忽略 VALARM / VTIMEZONE 细节但保留原文以便回写。</summary>
public static class IcsCalendar
{
    /// <summary>把 ICS 文本解析成若干 VEVENT。</summary>
    public static List<IcsEvent> ParseEvents(string ics)
    {
        var blocks = SplitEventBlocks(Unfold(ics));
        var events = new List<IcsEvent>(blocks.Count);
        foreach (var block in blocks)
        {
            var parsed = ParseEventBlock(block);
            if (parsed is not null)
            {
                events.Add(parsed);
            }
        }

        return events;
    }

    /// <summary>RFC 5545 折行还原：以空格或制表符开头的行接续上一行。</summary>
    public static List<string> Unfold(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new List<string>();
        foreach (var raw in normalized.Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && result.Count > 0)
            {
                result[^1] += raw[1..];
            }
            else
            {
                result.Add(raw);
            }
        }

        return result;
    }

    private static List<List<string>> SplitEventBlocks(List<string> lines)
    {
        var blocks = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new List<string>();
                continue;
            }

            if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null)
                {
                    blocks.Add(current);
                }

                current = null;
                continue;
            }

            current?.Add(line);
        }

        return blocks;
    }

    private static IcsEvent? ParseEventBlock(List<string> block)
    {
        var evt = new IcsEvent();
        // VALARM 是嵌在 VEVENT 里的子块，里面的 DESCRIPTION 是提醒通知正文
        // （苹果默认写「提醒事项」），不能被当成事件备注。整块跳过。
        var insideAlarm = false;

        foreach (var line in block)
        {
            if (line.StartsWith("BEGIN:VALARM", StringComparison.OrdinalIgnoreCase))
            {
                insideAlarm = true;
                continue;
            }

            if (line.StartsWith("END:VALARM", StringComparison.OrdinalIgnoreCase))
            {
                insideAlarm = false;
                continue;
            }

            if (insideAlarm)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var head = line[..colon];
            var value = line[(colon + 1)..];
            var semi = head.IndexOf(';');
            var name = (semi >= 0 ? head[..semi] : head).ToUpperInvariant();
            var parameters = semi >= 0 ? head[(semi + 1)..] : string.Empty;

            if (!evt.Properties.TryGetValue(name, out var list))
            {
                list = new List<string>();
                evt.Properties[name] = list;
            }

            list.Add(value);

            switch (name)
            {
                case "UID":
                    evt.Uid = value.Trim();
                    break;
                case "SUMMARY":
                    evt.Summary = UnescapeText(value);
                    break;
                case "LOCATION":
                    evt.Location = UnescapeText(value);
                    break;
                case "DESCRIPTION":
                    evt.Description = UnescapeText(value);
                    break;
                case "RRULE":
                    evt.RRule = value.Trim();
                    break;
                case "URL":
                    evt.Url = value.Trim();
                    break;
                case "CATEGORIES":
                    foreach (var category in value.Split(','))
                    {
                        var categoryName = UnescapeText(category).Trim();
                        if (categoryName.Length > 0)
                        {
                            evt.Categories.Add(categoryName);
                        }
                    }

                    break;
                case "TRANSP":
                    evt.Transp = value.Trim().ToUpperInvariant();
                    break;
                case "STATUS":
                    evt.Status = value.Trim().ToUpperInvariant();
                    break;
                case "CLASS":
                    evt.Class = value.Trim().ToUpperInvariant();
                    break;
                case "PRIORITY":
                    evt.Priority = int.TryParse(value.Trim(), out var priority)
                        ? Math.Clamp(priority, 0, 9)
                        : 0;
                    break;
                case "DTSTART":
                    evt.IsAllDay = parameters.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase);
                    evt.TimeZoneId = ReadParameter(parameters, "TZID") ?? string.Empty;
                    if (ParseStamp(value, evt.TimeZoneId) is { } start)
                    {
                        evt.Start = start;
                    }
                    break;
                case "DTEND":
                    if (ParseStamp(value, ReadParameter(parameters, "TZID") ?? string.Empty) is { } end)
                    {
                        evt.End = end;
                    }
                    break;
                case "RECURRENCE-ID":
                    evt.RecurrenceId = ParseStamp(value, ReadParameter(parameters, "TZID") ?? string.Empty);
                    break;
                case "EXDATE":
                    // 一个 EXDATE 行可以带多个逗号分隔的时刻（苹果常见）。
                    var exDateZone = ReadParameter(parameters, "TZID") ?? evt.TimeZoneId;
                    foreach (var raw in value.Split(','))
                    {
                        if (ParseStamp(raw, exDateZone) is { } excluded)
                        {
                            evt.ExceptionDates.Add(excluded);
                        }
                    }

                    break;
            }
        }

        if (evt.Start == default)
        {
            return null;
        }

        return evt;
    }

    private static string? ReadParameter(string parameters, string key)
    {
        foreach (var part in parameters.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            if (part[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return part[(eq + 1)..].Trim('"');
            }
        }

        return null;
    }

    /// <summary>
    /// 解析日期/时间值。带 Z 的按 UTC 转本地；带 TZID 的按该时区转本地；
    /// 不带时区的时间按本地时间处理（苹果写本地时间时会带上 TZID，这里的兜底很少走到）。
    /// </summary>
    public static DateTime? ParseStamp(string value, string timeZoneId = "")
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length == 8)
        {
            return DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) ? date : null;
        }

        string[] formats = { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" };
        if (text.EndsWith('Z'))
        {
            var body = text[..^1];
            if (DateTime.TryParseExact(body, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var utc))
            {
                return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            }

            return null;
        }

        if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(timeZoneId))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone).ToLocalTime();
            }
            catch (TimeZoneNotFoundException)
            {
                // 时区名不认识时按本地时间处理，至少不会丢事件
            }
            catch (InvalidTimeZoneException)
            {
                // 同上
            }
        }

        return local;
    }

    /// <summary>
    /// 在原始 ICS 里就地改写主 VEVENT 的字段：只替换/插入我们管理的属性，
    /// VALARM、X-APPLE-* 等未管理的行原样保留。这是「读-改-写」编辑的基础。
    /// </summary>
    public static string PatchMasterEvent(string ics, CalendarSyncService.CalendarEventDraft draft, int sequence)
    {
        var normalized = ics.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();

        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DTSTART", "DTEND", "SUMMARY", "LOCATION", "DESCRIPTION", "URL",
            "CATEGORIES", "TRANSP", "STATUS", "CLASS", "PRIORITY", "RRULE",
            "SEQUENCE", "LAST-MODIFIED", "DTSTAMP",
        };

        // 找到第一个 VEVENT（即主事件）的区间。
        var begin = lines.FindIndex(l => l.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase));
        if (begin < 0)
        {
            return ics;
        }

        var end = lines.FindIndex(begin, l => l.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase));
        if (end < 0)
        {
            return ics;
        }

        var head = lines.Take(begin + 1).ToList();
        var body = lines.Skip(begin + 1).Take(end - begin - 1).ToList();
        var tail = lines.Skip(end).ToList();

        // 丢掉我们负责的属性（含折行续行），其余原样保留。
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in body)
        {
            var isContinuation = line.StartsWith(" ") || line.StartsWith("\t");
            if (isContinuation)
            {
                if (!skipping)
                {
                    kept.Add(line);
                }

                continue;
            }

            var colon = line.IndexOf(':');
            var head0 = colon > 0 ? line[..colon] : line;
            var semi = head0.IndexOf(';');
            var name = (semi >= 0 ? head0[..semi] : head0).Trim().ToUpperInvariant();
            skipping = managed.Contains(name);
            if (!skipping)
            {
                kept.Add(line);
            }
        }

        var stamp = FormatUtcStamp(DateTime.UtcNow);
        var updated = new List<string>
        {
            "DTSTAMP:" + stamp,
            "LAST-MODIFIED:" + stamp,
            "SEQUENCE:" + sequence,
        };

        if (draft.AllDay)
        {
            updated.Add("DTSTART;VALUE=DATE:" + FormatDateStamp(draft.Start));
            updated.Add("DTEND;VALUE=DATE:" + FormatDateStamp(
                draft.End.Date >= draft.Start.Date ? draft.End.Date.AddDays(1) : draft.Start.Date.AddDays(1)));
        }
        else
        {
            updated.Add("DTSTART:" + FormatUtcStamp(draft.Start));
            updated.Add("DTEND:" + FormatUtcStamp(draft.End));
        }

        updated.Add("SUMMARY:" + EscapeText(draft.Summary));
        if (!string.IsNullOrWhiteSpace(draft.Location))
        {
            updated.Add("LOCATION:" + EscapeText(draft.Location));
        }

        if (!string.IsNullOrWhiteSpace(draft.Notes))
        {
            updated.Add("DESCRIPTION:" + EscapeText(draft.Notes));
        }

        if (!string.IsNullOrWhiteSpace(draft.Url))
        {
            updated.Add("URL:" + draft.Url.Trim());
        }

        if (!string.IsNullOrWhiteSpace(draft.Categories))
        {
            var names = draft.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(EscapeText);
            updated.Add("CATEGORIES:" + string.Join(",", names));
        }

        updated.Add(draft.Busy ? "TRANSP:OPAQUE" : "TRANSP:TRANSPARENT");
        updated.Add("STATUS:CONFIRMED");
        updated.Add("CLASS:" + (string.IsNullOrWhiteSpace(draft.Class) ? "PUBLIC" : draft.Class.ToUpperInvariant()));
        if (draft.Priority is > 0 and <= 9)
        {
            updated.Add("PRIORITY:" + draft.Priority);
        }

        if (!string.IsNullOrWhiteSpace(draft.RRule))
        {
            updated.Add("RRULE:" + draft.RRule.Trim());
        }

        var result = head.Concat(updated).Concat(kept).Concat(tail);
        return string.Join("\r\n", result.Select(Fold));
    }

    /// <summary>把文本按 VEVENT 块切开，返回每块的起止行号（含 BEGIN/END），其余内容原样保留。</summary>
    private static List<(int Begin, int End, bool IsOverride)> FindEventSpans(List<string> lines)
    {
        var spans = new List<(int, int, bool)>();
        var begin = -1;
        var isOverride = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                begin = i;
                isOverride = false;
                continue;
            }

            if (line.StartsWith("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase) && begin >= 0)
            {
                isOverride = true;
                continue;
            }

            if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase) && begin >= 0)
            {
                spans.Add((begin, i, isOverride));
                begin = -1;
            }
        }

        return spans;
    }

    /// <summary>
    /// 修改循环日程里的某一次：写入一个带 RECURRENCE-ID 的覆盖实例。
    /// 已存在同一时间的覆盖则先剔除，VALARM 等外层内容原样保留。
    /// </summary>
    public static string UpsertOccurrence(string ics, string uid, DateTime occurrenceStart,
        DateTime? occurrenceEnd, bool allDay, CalendarSyncService.CalendarEventDraft draft)
    {
        var normalized = ics.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var spans = FindEventSpans(lines);
        if (spans.Count == 0)
        {
            return ics;
        }

        var stamp = FormatUtcStamp(DateTime.UtcNow);

        // 剔除同一时刻的旧覆盖。
        for (var i = spans.Count - 1; i >= 0; i--)
        {
            var (begin, end, isOverride) = spans[i];
            if (!isOverride)
            {
                continue;
            }

            var recurLine = lines.Skip(begin).Take(end - begin)
                .FirstOrDefault(l => l.StartsWith("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase));
            if (recurLine is null)
            {
                continue;
            }

            var value = recurLine[(recurLine.IndexOf(':') + 1)..];
            var parsed = ParseStamp(value);
            if (parsed is { } parsedValue && Math.Abs((parsedValue - occurrenceStart).TotalMinutes) < 1)
            {
                lines.RemoveRange(begin, end - begin + 1);
                spans = FindEventSpans(lines);
                break;
            }
        }

        var block = new List<string> { "BEGIN:VEVENT", "UID:" + uid, "RECURRENCE-ID:" + FormatUtcStamp(occurrenceStart) };
        block.Add("DTSTAMP:" + stamp);
        if (allDay)
        {
            block.Add("DTSTART;VALUE=DATE:" + FormatDateStamp(draft.Start));
            block.Add("DTEND;VALUE=DATE:" + FormatDateStamp(
                draft.End.Date >= draft.Start.Date ? draft.End.Date.AddDays(1) : draft.Start.Date.AddDays(1)));
        }
        else
        {
            block.Add("DTSTART:" + FormatUtcStamp(draft.Start));
            block.Add("DTEND:" + FormatUtcStamp(draft.End));
        }

        block.Add("SUMMARY:" + EscapeText(draft.Summary));
        if (!string.IsNullOrWhiteSpace(draft.Notes))
        {
            block.Add("DESCRIPTION:" + EscapeText(draft.Notes));
        }

        if (!string.IsNullOrWhiteSpace(draft.Location))
        {
            block.Add("LOCATION:" + EscapeText(draft.Location));
        }

        block.Add("SEQUENCE:1");
        block.Add("STATUS:CONFIRMED");
        block.Add("END:VEVENT");

        // 插到主事件（第一个非覆盖块）之后，保持文件结构简单。
        var master = spans.FirstOrDefault(sp => !sp.IsOverride);
        var insertAt = master.End + 1;
        lines.InsertRange(insertAt, block.Select(Fold));

        return string.Join("\r\n", lines);
    }

    /// <summary>删除循环日程里的某一次：给主事件加一条 EXDATE。</summary>
    public static string AddExclusion(string ics, DateTime occurrenceStart)
    {
        var normalized = ics.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var spans = FindEventSpans(lines);
        var master = spans.FirstOrDefault(sp => !sp.IsOverride);
        if (spans.All(sp => sp.IsOverride))
        {
            // 没有主事件可挂 EXDATE（异常文件），原样返回。
            return ics;
        }

        var exdate = "EXDATE:" + FormatUtcStamp(occurrenceStart);

        // 已有 EXDATE 行就追加逗号，否则新起一行。
        for (var i = master.Begin; i <= master.End; i++)
        {
            if (lines[i].StartsWith("EXDATE", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = lines[i] + "," + FormatUtcStamp(occurrenceStart);
                return string.Join("\r\n", lines);
            }
        }

        lines.Insert(master.End, Fold(exdate));
        return string.Join("\r\n", lines);
    }

    public static string EscapeText(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    public static string UnescapeText(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                sb.Append(value[i] switch
                {
                    'n' or 'N' => '\n',
                    _ => value[i],
                });
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>按 75 字节折行，中文按 UTF-8 字节计算。</summary>
    public static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 73)
        {
            return line;
        }

        var sb = new StringBuilder();
        var bytes = 0;
        var first = true;
        foreach (var ch in line)
        {
            var len = Encoding.UTF8.GetByteCount(ch.ToString());
            if (bytes + len > 73)
            {
                sb.Append("\r\n ");
                bytes = 1;
            }

            sb.Append(ch);
            bytes += len;
            first = false;
        }

        return first ? line : sb.ToString();
    }

    public static string FormatUtcStamp(DateTime value) =>
        value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string FormatLocalStamp(DateTime value) =>
        value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    public static string FormatDateStamp(DateTime value) =>
        value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
