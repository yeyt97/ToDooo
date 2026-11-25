using ToDooo.Helpers;
using ToDooo.Models;

namespace ToDooo.Services;

/// <summary>本地日历在合并时怎么处理。</summary>
public enum MergeAction
{
    /// <summary>保留为纯本地日历，不上传（默认）。</summary>
    KeepLocal = 0,
    /// <summary>把本地独有的日程搬进目标云端日历，之后删掉本地集合。</summary>
    MergeInto = 1,
    /// <summary>丢弃本地日历及其中的日程。</summary>
    Discard = 2,
}

/// <summary>用户对某个本地日历的合并选择。</summary>
public sealed class LocalCalendarChoice
{
    public required string Href { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>该本地日历里的母事件条数。</summary>
    public int EventCount { get; init; }

    public MergeAction Action { get; set; } = MergeAction.KeepLocal;

    /// <summary>Action = MergeInto 时的目标云端日历 href。</summary>
    public string? TargetHref { get; set; }

    /// <summary>选了「丢弃」时，备份文件写到哪里；为空表示还没备份，不允许丢弃。</summary>
    public string? BackupPath { get; set; }

    /// <summary>这条日历是否已经满足执行条件（丢弃必须先备份）。</summary>
    public bool BackupDone { get; set; } = true;

}

/// <summary>合并过程中某一天发生了什么，供结果页按日期展开查看。</summary>
public sealed class MergeDayDetail
{
    public DateTime Date { get; init; }

    public List<string> Merged { get; } = new();

    public List<string> Uploaded { get; } = new();

    public List<string> Skipped { get; } = new();
}

/// <summary>一次合并的结果，界面据此展示「合并几条 / 上传几条」并支持点进某天。</summary>
public sealed class MergeOutcome
{
    /// <summary>本地独有的日程被搬到目标日历并上云的条数。</summary>
    public int UploadedCount { get; set; }

    /// <summary>因为云端已有同 UID 而采用云端版本、丢弃本地版本的条数。</summary>
    public int CloudWonCount { get; set; }

    /// <summary>保留为本地、未上传的条数。</summary>
    public int KeptLocalCount { get; set; }

    /// <summary>被丢弃的条数。</summary>
    public int DiscardedCount { get; set; }

    public int FailedCount { get; set; }

    /// <summary>因为上传失败而整体保留下来、没有被删掉的本地日历个数。</summary>
    public int KeptCalendarCount { get; set; }

    /// <summary>按日期分组的明细，只含发生过事的日期，按日期排序。</summary>
    public List<MergeDayDetail> Days { get; } = new();

    public MergeDayDetail DayFor(DateTime date)
    {
        var existing = Days.FirstOrDefault(day => day.Date == date.Date);
        if (existing is not null)
        {
            return existing;
        }

        var created = new MergeDayDetail { Date = date.Date };
        Days.Add(created);
        return created;
    }
}

/// <summary>
/// 合并本地日历与云端日历的唯一口径：<b>云端优先</b>。
/// 同一 UID 在云端已存在时直接采用云端版本，本地那份丢弃、不推送；
/// 只有云端没有的日程才作为新增上传。
/// </summary>
public sealed class CalendarMergeService
{
    private readonly CalendarRepository _repository;
    private readonly CalendarSyncService _sync;

    public CalendarMergeService(CalendarRepository repository, CalendarSyncService sync)
    {
        _repository = repository;
        _sync = sync;
    }

    /// <summary>本地是否还有需要用户裁决的数据（本地日历里存在日程）。</summary>
    public bool HasLocalDataToReconcile() =>
        _repository.GetSources()
            .Where(source => CalendarRepository.IsLocalHref(source.Href))
            .Any(source => _repository.CountMastersForCalendar(source.Href) > 0);

    /// <summary>列出本地日历，附带条数与默认动作，供向导渲染。</summary>
    public List<LocalCalendarChoice> BuildChoices()
    {
        return _repository.GetSources()
            .Where(source => CalendarRepository.IsLocalHref(source.Href))
            .Select(source => new LocalCalendarChoice
            {
                Href = source.Href,
                DisplayName = source.DisplayName,
                EventCount = _repository.CountMastersForCalendar(source.Href),
                Action = MergeAction.KeepLocal,
            })
            .ToList();
    }

    /// <summary>
    /// 把某个本地日历的全部日程导出成一份 ICS 文本，供「丢弃前备份」使用。
    /// 只取 VEVENT 块重组，保留原始字段（含循环、时区、提醒）。
    /// </summary>
    public string BuildBackupIcs(string calendarHref)
    {
        var masters = _repository.GetMastersForCalendar(calendarHref);
        var builder = new System.Text.StringBuilder();
        builder.Append("BEGIN:VCALENDAR\r\n");
        builder.Append("VERSION:2.0\r\n");
        builder.Append("PRODID:-//ToDooo//Calendar Backup//CN\r\n");

        foreach (var master in masters)
        {
            var inside = false;
            foreach (var line in IcsCalendar.Unfold(master.Ics))
            {
                var trimmed = line.TrimEnd();
                if (trimmed.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    inside = true;
                }

                if (inside)
                {
                    builder.Append(trimmed).Append("\r\n");
                }

                if (trimmed.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    inside = false;
                }
            }
        }

        builder.Append("END:VCALENDAR\r\n");
        return builder.ToString();
    }

    /// <summary>
    /// 按用户选择执行合并。调用前应已完成一次云端同步，
    /// 这样本地才有云端 UID 可用来判断「谁覆盖谁」。
    /// </summary>
    public async Task<MergeOutcome> ApplyAsync(
        IReadOnlyList<LocalCalendarChoice> choices,
        CancellationToken token = default)
    {
        var outcome = new MergeOutcome();

        // 云端已有 UID 的全集：本地同 UID 的一律以云端为准。
        var cloudUids = BuildCloudUidSet();

        foreach (var choice in choices)
        {
            var masters = _repository.GetMastersForCalendar(choice.Href);
            switch (choice.Action)
            {
                case MergeAction.KeepLocal:
                    outcome.KeptLocalCount += masters.Count;
                    foreach (var master in masters)
                    {
                        foreach (var date in EventDates(master.Ics))
                        {
                            outcome.DayFor(date).Skipped.Add($"{SummaryOf(master.Ics)}（保留本地）");
                        }
                    }

                    break;

                case MergeAction.Discard:
                    outcome.DiscardedCount += masters.Count;
                    foreach (var master in masters)
                    {
                        foreach (var date in EventDates(master.Ics))
                        {
                            outcome.DayFor(date).Skipped.Add($"{SummaryOf(master.Ics)}（已丢弃）");
                        }
                    }

                    _repository.DeleteSource(choice.Href);
                    break;

                case MergeAction.MergeInto when choice.TargetHref is { } target:
                    await MergeIntoAsync(choice, target, masters, cloudUids, outcome, token)
                        .ConfigureAwait(false);
                    break;
            }
        }

        outcome.Days.Sort((a, b) => a.Date.CompareTo(b.Date));
        return outcome;
    }

    private async Task MergeIntoAsync(
        LocalCalendarChoice choice,
        string targetHref,
        List<RawCalendarItem> masters,
        HashSet<string> cloudUids,
        MergeOutcome outcome,
        CancellationToken token)
    {
        var failed = 0;

        foreach (var master in masters)
        {
            var uid = UidOf(master.Ics);
            var dates = EventDates(master.Ics);
            var summary = SummaryOf(master.Ics);

            // 云端已有同 UID：云端为准，本地这条不推送。
            if (uid.Length > 0 && cloudUids.Contains(uid))
            {
                outcome.CloudWonCount++;
                foreach (var date in dates)
                {
                    outcome.DayFor(date).Skipped.Add($"{summary}（云端已有，采用云端版本）");
                }

                DropLocally(master.ResourceHref);
                continue;
            }

            // 云端没有这条：作为新增上传；传成功后本地副本才移除。
            var renamed = RenameResource(master.ResourceHref);
            try
            {
                await _sync.UploadLocalMasterAsync(targetHref, renamed, master.Ics, token)
                    .ConfigureAwait(false);

                outcome.UploadedCount++;
                if (uid.Length > 0)
                {
                    cloudUids.Add(uid);
                }
                foreach (var date in dates)
                {
                    outcome.DayFor(date).Uploaded.Add(summary);
                }

                DropLocally(master.ResourceHref);
            }
            catch (Exception)
            {
                // 失败的这条原样留在本地，之后还能重试，绝不因合并失败丢数据。
                failed++;
                outcome.FailedCount++;
                foreach (var date in dates)
                {
                    outcome.DayFor(date).Skipped.Add($"{summary}（上传失败，已保留在本地）");
                }
            }
        }

        // 只有全部搬迁成功（或采用云端版本）才删本地集合；
        // 有任何失败就保留这个日历，里面只剩没传上去的那几条。
        if (failed == 0)
        {
            _repository.DeleteSource(choice.Href);
        }
        else
        {
            outcome.KeptCalendarCount++;
        }
    }

    /// <summary>把一条已经从云端确认过的资源从本地集合里摘掉（母事件 + 展开缓存）。</summary>
    private void DropLocally(string resourceHref)
    {
        _repository.RemoveMaster(resourceHref);
        _repository.RemoveEventsByResource(resourceHref);
    }

    private HashSet<string> BuildCloudUidSet()
    {
        var uids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _repository.GetSources())
        {
            if (CalendarRepository.IsLocalHref(source.Href))
            {
                continue;
            }

            foreach (var master in _repository.GetMastersForCalendar(source.Href))
            {
                var uid = UidOf(master.Ics);
                if (uid.Length > 0)
                {
                    uids.Add(uid);
                }
            }
        }

        return uids;
    }

    private static string UidOf(string ics) =>
        IcsCalendar.ParseEvents(ics).Select(evt => evt.Uid).FirstOrDefault(uid => !string.IsNullOrWhiteSpace(uid))
        ?? string.Empty;

    private static string SummaryOf(string ics) =>
        IcsCalendar.ParseEvents(ics).Select(evt => evt.Summary).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
        ?? "（无标题）";

    /// <summary>该资源覆盖到的日期（循环日程取开始日，够结果页分组用）。</summary>
    private static IEnumerable<DateTime> EventDates(string ics) =>
        IcsCalendar.ParseEvents(ics)
            .Select(evt => evt.Start.Date)
            .Distinct();

    /// <summary>本地资源路径带 local: 前缀，上传前换成普通文件名。</summary>
    private static string RenameResource(string resourceHref)
    {
        var name = resourceHref[(resourceHref.LastIndexOf('/') + 1)..];
        if (name.Length == 0)
        {
            name = Guid.NewGuid().ToString("N") + ".ics";
        }

        return name.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ? name : name + ".ics";
    }
}
