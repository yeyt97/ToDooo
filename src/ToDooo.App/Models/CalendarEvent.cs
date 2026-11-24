namespace ToDooo.Models;

/// <summary>一个 iCloud 日历集合（VEVENT 列表），由用户勾选决定是否在日历里显示。</summary>
public sealed class CalendarSource
{
    public long Id { get; set; }

    /// <summary>服务器上的集合路径，例如 /476391584/calendars/xxx/ 。用作稳定标识。</summary>
    public string Href { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>集合色，形如 #1BADF8FF；苹果给的原始值，用于画事件色条。</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>是否在日历里显示该集合的事件。</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>列表顺序，来自 calendar-order。</summary>
    public int SortOrder { get; set; }

    public string? SyncToken { get; set; }
}

/// <summary>
/// 一条已展开的日历事件实例。循环事件由展开器按窗口生成多个实例，
/// RecurrenceId 非空时表示这是被单独改期/编辑过的那一次。
/// </summary>
public sealed class CalendarEvent
{
    public long Id { get; set; }

    /// <summary>所属集合的 href。</summary>
    public string CalendarHref { get; set; } = string.Empty;

    /// <summary>服务器资源路径（.ics 文件的 href）。同一 UID 的多个实例共用它。</summary>
    public string ResourceHref { get; set; } = string.Empty;

    public string Uid { get; set; } = string.Empty;

    /// <summary>展开后的实例开始时间（本地时间）。</summary>
    public DateTime StartAt { get; set; }

    /// <summary>结束时间（本地时间）；全天事件为次日 0 点，不含当天。</summary>
    public DateTime? EndAt { get; set; }

    public bool IsAllDay { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    /// <summary>事件备注（DESCRIPTION）。</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>相关链接（URL）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>分类（CATEGORIES），多个用逗号连接。</summary>
    public string Categories { get; set; } = string.Empty;

    /// <summary>忙闲状态：false = 显示为空闲（TRANSP:TRANSPARENT）。</summary>
    public bool IsBusy { get; set; } = true;

    /// <summary>STATUS 原值：CONFIRMED / TENTATIVE / CANCELLED。</summary>
    public string Status { get; set; } = "CONFIRMED";

    /// <summary>隐私级别：PUBLIC / PRIVATE / CONFIDENTIAL。</summary>
    public string Class { get; set; } = "PUBLIC";

    /// <summary>优先级：0 = 未指定，1–4 高，5 中，6–9 低。</summary>
    public int Priority { get; set; }

    /// <summary>RECURRENCE-ID 原值，用于区分循环实例；为空表示非循环或母事件。</summary>
    public DateTime? RecurrenceId { get; set; }

    /// <summary>原始 RRULE 文本；为空表示单次事件。</summary>
    public string RRule { get; set; } = string.Empty;

    public string ETag { get; set; } = string.Empty;

    /// <summary>集合色缓存，渲染时直接取自 CalendarSource。</summary>
    public string Color { get; set; } = string.Empty;

    public DateTime? LastModified { get; set; }

    public DateTime EndOrDefault => EndAt ?? StartAt.AddMinutes(30);
}
