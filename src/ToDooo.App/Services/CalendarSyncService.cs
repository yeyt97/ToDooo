using ToDooo.Models;

namespace ToDooo.Services;

/// <summary>本地已生效、但云端推送失败；调用方应提示“稍后自动重试”而不是当成失败。</summary>
public sealed class CalDavPendingException : Exception
{
    public CalDavPendingException(string message) : base(message)
    {
    }
}

/// <summary>一次同步的结果摘要，供界面显示。</summary>
public sealed record CalendarSyncResult(bool Succeeded, int CalendarCount, int EventCount, string Message)
{
    public static CalendarSyncResult Fail(string message) => new(false, 0, 0, message);
}

/// <summary>
/// 把 iCloud CalDAV 与本地日历缓存串起来：连接 → 发现集合 → 按窗口拉取 → 展开循环 → 写缓存。
/// 单向为主（云端是事实源），本地改动通过 Write* 直接推送到云端后再刷新缓存。
/// </summary>
public sealed class CalendarSyncService
{
    private readonly CalendarRepository _repository;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>连续拖动时的静默期：这段时间内的多次改动只推一次云端。</summary>
    private const int PushQuietPeriodMs = 700;

    private CancellationTokenSource? _pushCts;

    /// <summary>连续失败次数；用于指数退避，避免断网时反复打请求。</summary>
    private int _consecutiveFailures;

    /// <summary>最近一次成功同步时间。</summary>
    public DateTime? LastSyncedAt { get; private set; }

    /// <summary>本地待补传的条数，界面可用来提示「有 N 项待同步」。</summary>
    public int PendingCount => _repository.PendingCount();

    /// <summary>下一次后台同步建议的等待时间（分钟）。失败按 1/2/4/8 递增，封顶 30。</summary>
    public int SuggestedRefreshMinutes(int configuredMinutes)
    {
        if (configuredMinutes <= 0)
        {
            return 0;
        }

        if (_consecutiveFailures == 0)
        {
            return configuredMinutes;
        }

        var backoff = Math.Min(30, (int)Math.Pow(2, Math.Min(_consecutiveFailures, 5)));
        return Math.Max(configuredMinutes, backoff);
    }

    public CalendarSyncService(CalendarRepository repository, SettingsService settings)
    {
        _repository = repository;
        _settings = settings;
    }

    public bool IsConfigured =>
        _settings.Current.CalendarSyncEnabled &&
        !string.IsNullOrWhiteSpace(_settings.Current.CalendarAppleId) &&
        !string.IsNullOrWhiteSpace(_settings.Current.CalendarPasswordCipher);

    /// <summary>
    /// 本地是否有日历可用：配了账号，或者同步过、缓存里有日历集合。
    /// 离线或还没配账号时，日历照常可以查看和编辑，改动排进待补传队列。
    /// </summary>
    public bool HasLocalCalendar => IsConfigured || _repository.GetSources().Count > 0;

    public CalendarRepository Repository => _repository;

    public event EventHandler? EventsChanged;

    /// <summary>是否正在与云端交互（拉取或补发）。界面据此显示转圈。</summary>
    public bool IsSyncing { get; private set; }

    public event EventHandler<bool>? SyncingChanged;

    private void SetSyncing(bool value)
    {
        if (IsSyncing == value)
        {
            return;
        }

        IsSyncing = value;
        SyncingChanged?.Invoke(this, value);
    }

    public string AppleId => _settings.Current.CalendarAppleId;

    public string Password
    {
        get => SecretProtector.Unprotect(_settings.Current.CalendarPasswordCipher);
    }

    public void SaveCredentials(string appleId, string password)
    {
        _settings.Current.CalendarAppleId = appleId.Trim();
        _settings.Current.CalendarPasswordCipher = SecretProtector.Protect(password);
        _settings.Save();
    }

    public void ClearCredentials()
    {
        _settings.Current.CalendarAppleId = string.Empty;
        _settings.Current.CalendarPasswordCipher = string.Empty;
        _settings.Save();
        _repository.ClearAll();
        EventsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>用当前设置里的凭据测试连接，成功则返回日历数量。</summary>
    public async Task<CalendarSyncResult> TestConnectionAsync(
        string appleId, string password, CancellationToken token = default)
    {
        try
        {
            using var client = new CalDavClient(appleId, password);
            await client.ConnectAsync(token).ConfigureAwait(false);
            var calendars = await client.GetCalendarsAsync(token).ConfigureAwait(false);
            return new CalendarSyncResult(true, calendars.Count, 0,
                $"连接成功，找到 {calendars.Count} 个日历。");
        }
        catch (CalDavException exception)
        {
            return CalendarSyncResult.Fail(exception.Message);
        }
    }

    /// <summary>
    /// 合并向导专用：把一条本地 ICS 作为<b>新增</b>上传到云端集合，绝不覆盖已有资源。
    /// 资源名由调用方给（本地集合路径带 local: 前缀，不能直接当远端文件名）。
    /// </summary>
    public async Task UploadLocalMasterAsync(string targetCalendarHref, string resourceName,
        string ics, CancellationToken token = default)
    {
        if (!IsConfigured)
        {
            throw new CalDavException("尚未配置日历账号，无法上传。");
        }

        using var client = new CalDavClient(AppleId, Password);
        await client.ConnectAsync(token).ConfigureAwait(false);
        await client.CreateEventAsync(targetCalendarHref, resourceName, ics, token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 同步 [from, to) 窗口：刷新集合列表与可见性之外的元数据，逐集合整体替换缓存事件。
    /// 并发调用会被串行化，界面反复触发刷新也不会互相踩。
    /// </summary>
    public async Task<CalendarSyncResult> SyncAsync(DateTime from, DateTime to,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        // 整窗同步要真的连服务器，没账号就没得同步；本地查看编辑不受影响。
        if (!IsConfigured)
        {
            return CalendarSyncResult.Fail("尚未配置日历账号。");
        }

        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
        {
            return new CalendarSyncResult(true, 0, 0, "正在同步，请稍候。");
        }

        SetSyncing(true);
        try
        {
            using var client = new CalDavClient(AppleId, Password);
            await client.ConnectAsync(token).ConfigureAwait(false);

            // 先补发上次离线时没推成功的增删。
            await FlushPendingAsync(client, token).ConfigureAwait(false);

            progress?.Report("正在读取日历列表…");
            var calendars = await client.GetCalendarsAsync(token).ConfigureAwait(false);
            _repository.MergeSources(calendars);

            var visible = _repository.GetSources();
            var colorByHref = visible.ToDictionary(s => s.Href, s => s.Color);

            var total = 0;
            var failed = new List<string>();
            foreach (var calendar in calendars)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"正在读取「{calendar.DisplayName}」…");
                try
                {
                    var raw = await client.QueryEventsAsync(calendar.Href, from, to, token).ConfigureAwait(false);
                    _repository.ReplaceMasters(calendar.Href, raw);
                    var expanded = ExpandItems(raw, from, to);
                    _repository.ReplaceEvents(calendar.Href, expanded);
                    total += expanded.Count;
                }
                catch (CalDavException exception) when (exception.StatusCode is 403 or 404)
                {
                    // 个别集合（共享只读、无名遗留集合）不响应查询，跳过即可，不影响其它日历。
                    failed.Add(calendar.DisplayName);
                    _repository.ReplaceEvents(calendar.Href, new List<CalendarEvent>());
                }
            }

            _consecutiveFailures = 0;
            LastSyncedAt = DateTime.Now;
            EventsChanged?.Invoke(this, EventArgs.Empty);
            var message = failed.Count == 0
                ? $"已同步 {calendars.Count} 个日历、{total} 个事件。"
                : $"已同步 {total} 个事件（{string.Join("、", failed)} 跳过）。";
            return new CalendarSyncResult(true, calendars.Count, total, message);
        }
        catch (CalDavException exception)
        {
            _consecutiveFailures++;
            return CalendarSyncResult.Fail(exception.Message);
        }
        catch (OperationCanceledException)
        {
            return CalendarSyncResult.Fail("同步已取消。");
        }
        finally
        {
            SetSyncing(false);
            // 必须归还信号量，否则第一次同步之后所有后续同步都会被挡在门外、看起来点了没反应。
            _gate.Release();

            // 同步过程中新拖动的改动进不了本轮推送，停稳后再补一次。
            if (_repository.PendingCount() > 0)
            {
                ScheduleDeferredPush();
            }
        }
    }

    /// <summary>把离线期间积压的增删补发到云端；成功一条清一条，失败就留着下次再试。</summary>
    private async Task FlushPendingAsync(CalDavClient client, CancellationToken token)
    {
        foreach (var pending in _repository.GetPending())
        {
            try
            {
                if (string.Equals(pending.Kind, "create", StringComparison.Ordinal))
                {
                    var resourceName = pending.ResourceHref.TrimEnd('/').Split('/').Last();
                    try
                    {
                        await client.CreateEventAsync(pending.CalendarHref, resourceName, pending.Payload, token)
                            .ConfigureAwait(false);
                    }
                    catch (CalDavException exception) when (exception.StatusCode == 412 ||
                        exception.StatusCode == 409)
                    {
                        // 上一次其实已经建成功了（或手机端先建了同名资源）：改走覆盖写入，
                        // 否则 If-None-Match 会一直 412，这条永远卡在队里。
                        await client.UpdateEventAsync(pending.ResourceHref, pending.Payload, null, token)
                            .ConfigureAwait(false);
                    }
                }
                else if (string.Equals(pending.Kind, "update", StringComparison.Ordinal))
                {
                    // 补发时不再校验 etag：本地修改本来就是要覆盖云端，若带旧 etag 会永久 412。
                    await client.UpdateEventAsync(pending.ResourceHref, pending.Payload, null, token)
                        .ConfigureAwait(false);
                }
                else if (string.Equals(pending.Kind, "delete", StringComparison.Ordinal))
                {
                    await client.DeleteEventAsync(pending.ResourceHref,
                            string.IsNullOrEmpty(pending.ETag) ? null : pending.ETag, token)
                        .ConfigureAwait(false);
                }

                _repository.RemovePending(pending.ResourceHref, pending.Payload);
                LastPushError = null;
            }
            catch (Exception exception)
            {
                // 保留这一条，把原因留给界面提示：不然用户只看到橙点一直在，不知道卡在哪。
                LastPushError = exception is CalDavException dav
                    ? $"待同步的改动推送失败：{dav.Message}"
                    : $"待同步的改动推送失败：{exception.Message}";
                break;
            }
        }
    }

    /// <summary>最近一次补传失败的原因；成功推送后会清空。</summary>
    public string? LastPushError { get; private set; }

    /// <summary>
    /// 把一次查询拿到的原始条目展开成窗口内的事件实例。
    /// 同一 UID 的母事件与 RECURRENCE-ID 覆盖要合并：母事件负责生成循环，覆盖替换掉对应那一次。
    /// </summary>
    public static List<CalendarEvent> ExpandItems(IEnumerable<RawCalendarItem> raw, DateTime from, DateTime to)
    {
        var events = new List<CalendarEvent>();
        foreach (var item in raw)
        {
            var parsed = IcsCalendar.ParseEvents(item.Ics);
            if (parsed.Count == 0)
            {
                continue;
            }

            var masters = parsed.Where(e => e.RecurrenceId is null).ToList();
            var overrides = new Dictionary<DateTime, IcsEvent>();
            foreach (var overrideEvent in parsed.Where(e => e.RecurrenceId is not null))
            {
                // 同一 RECURRENCE-ID 重复时以文件里靠后的为准，不抛异常。
                overrides[overrideEvent.RecurrenceId!.Value] = overrideEvent;
            }

            // 只有覆盖实例、没有母事件的资源：直接按独立事件放进窗口。
            if (masters.Count == 0)
            {
                foreach (var overrideEvent in overrides.Values)
                {
                    if (string.Equals(overrideEvent.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (overrideEvent.Start < to && (overrideEvent.End ?? overrideEvent.Start) >= from)
                    {
                        events.Add(ToCalendarEvent(item, overrideEvent, overrideEvent.Start, isRecurring: false));
                    }
                }

                continue;
            }

            foreach (var master in masters)
            {
                if (string.Equals(master.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(master.RRule))
                {
                    if (master.Start < to && (master.End ?? master.Start) >= from)
                    {
                        events.Add(ToCalendarEvent(item, master, master.Start, isRecurring: false));
                    }

                    continue;
                }

                var occurrences = RecurrenceExpander.Expand(master, from, to);
                var exdates = new HashSet<DateTime>(master.ExceptionDates);
                var seenOverride = new HashSet<DateTime>();
                foreach (var occurrence in occurrences)
                {
                    // EXDATE 与覆盖实例同时存在时，以 EXDATE 为准：这一次被删掉了。
                    if (exdates.Contains(occurrence))
                    {
                        continue;
                    }

                    if (overrides.TryGetValue(occurrence, out var overrideEvent))
                    {
                        seenOverride.Add(occurrence);
                        if (string.Equals(overrideEvent.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (overrideEvent.Start < to && (overrideEvent.End ?? overrideEvent.Start) >= from)
                        {
                            events.Add(ToCalendarEvent(item, overrideEvent, overrideEvent.Start, isRecurring: false));
                        }

                        continue;
                    }

                    events.Add(ToCalendarEvent(item, master, occurrence, isRecurring: true));
                }

                // 被移到窗口里的覆盖实例（原时间不在窗口内）也要算进来。
                foreach (var (recurrenceId, overrideEvent) in overrides)
                {
                    if (seenOverride.Contains(recurrenceId) || exdates.Contains(recurrenceId))
                    {
                        continue;
                    }

                    if (string.Equals(overrideEvent.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (overrideEvent.Start < to && (overrideEvent.End ?? overrideEvent.Start) >= from)
                    {
                        events.Add(ToCalendarEvent(item, overrideEvent, overrideEvent.Start, isRecurring: false));
                    }
                }

                // 母事件里带 RECURRENCE-ID 的覆盖已经单独处理，避免被当成普通事件重复。
            }
        }

        return events
            .OrderBy(e => e.StartAt)
            .ThenBy(e => e.Summary, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 从本地母事件按窗口展开，供日历渲染。母事件是事实源：
    /// RRULE、EXDATE、RECURRENCE-ID 覆盖全在原始 ICS 里，展开由这里统一负责。
    /// 旧版本只存过平铺实例时退回读平铺表，避免升级后日历瞬间空白。
    /// </summary>
    public List<CalendarEvent> LoadEvents(DateTime from, DateTime to)
    {
        var masters = _repository.GetVisibleMasters();
        var colors = _repository.GetSources().ToDictionary(s => s.Href, s => s.Color);
        if (masters.Count == 0)
        {
            return _repository.GetEventsInRange(from, to, colors);
        }

        var expanded = ExpandItems(masters, from, to);
        foreach (var item in expanded)
        {
            item.Color = colors.TryGetValue(item.CalendarHref, out var color) ? color : string.Empty;
        }

        // 升级前的平铺缓存：这些资源还没有母事件，先照旧显示，等下次同步补齐。
        var withMaster = new HashSet<string>(
            masters.Select(m => m.ResourceHref), StringComparer.OrdinalIgnoreCase);
        var legacy = _repository.GetEventsInRange(from, to, colors)
            .Where(e => !withMaster.Contains(e.ResourceHref));
        return expanded
            .Concat(legacy)
            .OrderBy(e => e.StartAt)
            .ThenBy(e => e.Summary, StringComparer.CurrentCulture)
            .ToList();
    }

    private static CalendarEvent ToCalendarEvent(RawCalendarItem item, IcsEvent source,
        DateTime start, bool isRecurring)
    {
        return new CalendarEvent
        {
            CalendarHref = item.CalendarHref,
            ResourceHref = item.ResourceHref,
            Uid = source.Uid,
            StartAt = start,
            EndAt = source.End is { } end ? start + (end - source.Start) : null,
            IsAllDay = source.IsAllDay,
            Summary = source.Summary,
            Location = source.Location,
            Notes = source.Description,
            Url = source.Url,
            Categories = string.Join(",", source.Categories),
            IsBusy = !string.Equals(source.Transp, "TRANSPARENT", StringComparison.OrdinalIgnoreCase),
            Status = string.IsNullOrEmpty(source.Status) ? "CONFIRMED" : source.Status,
            Class = string.IsNullOrEmpty(source.Class) ? "PUBLIC" : source.Class,
            Priority = source.Priority,
            RecurrenceId = source.RecurrenceId,
            RRule = isRecurring ? source.RRule : string.Empty,
            ETag = item.ETag,
        };
    }

    /// <summary>新建事件所需的可选字段。除标题与时间外都可留空，留空就不写对应属性。</summary>
    public sealed record CalendarEventDraft
    {
        public string Summary { get; init; } = string.Empty;

        public DateTime Start { get; init; }

        public DateTime End { get; init; }

        public bool AllDay { get; init; }

        public string Location { get; init; } = string.Empty;

        public string Notes { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;

        /// <summary>分类，多个用逗号分隔。</summary>
        public string Categories { get; init; } = string.Empty;

        /// <summary>true 表示忙（OPAQUE），false 表示显示为空闲（TRANSPARENT）。</summary>
        public bool Busy { get; init; } = true;

        /// <summary>PUBLIC / PRIVATE / CONFIDENTIAL。</summary>
        public string Class { get; init; } = "PUBLIC";

        /// <summary>0 = 未指定，1–4 高，5 中，6–9 低。</summary>
        public int Priority { get; init; }

        /// <summary>重复规则（如 FREQ=WEEKLY;BYDAY=MO）；为空表示单次事件。</summary>
        public string RRule { get; init; } = string.Empty;
    }

    /// <summary>把草稿序列化成一份完整的 ICS；创建与编辑共用。</summary>
    private static string BuildIcs(string uid, CalendarEventDraft draft, int sequence)
    {
        var stamp = IcsCalendar.FormatUtcStamp(DateTime.UtcNow);
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//ToDooo//Calendar//EN",
            "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT",
            "UID:" + uid,
            "DTSTAMP:" + stamp,
            "CREATED:" + stamp,
            "LAST-MODIFIED:" + stamp,
            "SEQUENCE:" + sequence,
        };

        if (draft.AllDay)
        {
            lines.Add("DTSTART;VALUE=DATE:" + IcsCalendar.FormatDateStamp(draft.Start));
            lines.Add("DTEND;VALUE=DATE:" + IcsCalendar.FormatDateStamp(
                draft.End.Date >= draft.Start.Date ? draft.End.Date.AddDays(1) : draft.Start.Date.AddDays(1)));
        }
        else
        {
            lines.Add("DTSTART:" + IcsCalendar.FormatUtcStamp(draft.Start));
            lines.Add("DTEND:" + IcsCalendar.FormatUtcStamp(draft.End));
        }

        lines.Add("SUMMARY:" + IcsCalendar.EscapeText(draft.Summary));
        if (!string.IsNullOrWhiteSpace(draft.Location))
        {
            lines.Add("LOCATION:" + IcsCalendar.EscapeText(draft.Location));
        }

        if (!string.IsNullOrWhiteSpace(draft.Notes))
        {
            lines.Add("DESCRIPTION:" + IcsCalendar.EscapeText(draft.Notes));
        }

        if (!string.IsNullOrWhiteSpace(draft.Url))
        {
            lines.Add("URL:" + draft.Url.Trim());
        }

        if (!string.IsNullOrWhiteSpace(draft.Categories))
        {
            var names = draft.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(IcsCalendar.EscapeText);
            lines.Add("CATEGORIES:" + string.Join(",", names));
        }

        lines.Add(draft.Busy ? "TRANSP:OPAQUE" : "TRANSP:TRANSPARENT");
        lines.Add("STATUS:CONFIRMED");
        lines.Add("CLASS:" + (string.IsNullOrWhiteSpace(draft.Class) ? "PUBLIC" : draft.Class.ToUpperInvariant()));
        if (draft.Priority is > 0 and <= 9)
        {
            lines.Add("PRIORITY:" + draft.Priority);
        }

        if (!string.IsNullOrWhiteSpace(draft.RRule))
        {
            lines.Add("RRULE:" + draft.RRule.Trim());
        }

        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.Select(IcsCalendar.Fold)) + "\r\n";
    }

    /// <summary>新建一个事件：先写本地缓存立即显示，再推送到云端；失败则回滚。</summary>
    public async Task<CalendarEvent> CreateEventAsync(string calendarHref, CalendarEventDraft draft,
        CancellationToken token = default)
    {
        var uid = Guid.NewGuid().ToString().ToUpperInvariant() + "@todooo";
        var ics = BuildIcs(uid, draft, sequence: 0);
        var resourceName = uid.Replace("@", "_").Replace(".", "-") + ".ics";
        var predictedPath = calendarHref.TrimEnd('/') + "/" + resourceName;

        var created = new CalendarEvent
        {
            CalendarHref = calendarHref,
            ResourceHref = predictedPath,
            Uid = uid,
            StartAt = draft.Start,
            EndAt = draft.End,
            IsAllDay = draft.AllDay,
            Summary = draft.Summary,
            Location = draft.Location,
            Notes = draft.Notes,
            Url = draft.Url,
            Categories = draft.Categories,
            IsBusy = draft.Busy,
            Status = "CONFIRMED",
            Class = string.IsNullOrWhiteSpace(draft.Class) ? "PUBLIC" : draft.Class.ToUpperInvariant(),
            Priority = draft.Priority,
            RRule = draft.RRule,
        };

        _repository.UpsertMaster(new RawCalendarItem
        {
            CalendarHref = calendarHref,
            ResourceHref = predictedPath,
            ETag = string.Empty,
            Ics = ics,
        });

        // 平铺表是旧读取路径的兜底，也同步写上展开结果，保证两边口径一致。
        var windowStart = draft.Start.Date.AddMonths(-1);
        var windowEnd = draft.End.Date.AddMonths(2);
        _repository.ReplaceEventsForResource(
            calendarHref, predictedPath,
            ExpandItems(new[]
            {
                new RawCalendarItem
                {
                    CalendarHref = calendarHref,
                    ResourceHref = predictedPath,
                    ETag = string.Empty,
                    Ics = ics,
                },
            }, windowStart, windowEnd));

        EventsChanged?.Invoke(this, EventArgs.Empty);

        // 本地日历（local: 前缀）只存本机，不进云端队列——它没有对应的服务器集合。
        if (CalendarRepository.IsLocalHref(calendarHref))
        {
            return created;
        }

        // 没配账号（离线也能新建）：本地已生效，进队列等配上账号再推。
        if (!IsConfigured)
        {
            _repository.AddPending(predictedPath, "create", calendarHref, string.Empty, ics);
            return created;
        }

        try
        {
            using var client = new CalDavClient(AppleId, Password);
            await client.ConnectAsync(token).ConfigureAwait(false);
            await client.CreateEventAsync(calendarHref, resourceName, ics, token)
                .ConfigureAwait(false);
        }
        catch
        {
            // 本地先保留，记一条待补传；网络恢复后由同步统一补发。
            _repository.AddPending(predictedPath, "create", calendarHref, string.Empty, ics);
            throw new CalDavPendingException("日程已保存到本地，云端同步失败，网络恢复后会自动补传。");
        }

        return created;
    }

    /// <summary>编辑已存在的单次日程：覆盖同一资源，成功后同步本地缓存。</summary>
    public async Task UpdateEventAsync(CalendarEvent existing, CalendarEventDraft draft,
        CancellationToken token = default)
    {
        var fallback = BuildIcs(existing.Uid, draft, sequence: 1);

        // 本地母事件存在时做读-改-写，保留 EXDATE、覆盖实例、VALARM 等未管理内容；
        // 单次日程直接用本程序生成的 ICS 也不会丢字段。
        var localPayload = PatchLocalMaster(existing.ResourceHref, draft, fallback);
        SaveMaster(existing, localPayload);
        EventsChanged?.Invoke(this, EventArgs.Empty);

        // 这条资源还等着补传（本地新建未上云）：更新队列内容即可，不必再发 PUT。
        if (_repository.HasPending(existing.ResourceHref, "create"))
        {
            _repository.AddPending(existing.ResourceHref, "create", existing.CalendarHref,
                string.Empty, localPayload);
            return;
        }

        // 本地日历或没配账号：改动只在本地生效。本地集合没有云端对应，不进队列。
        if (!IsConfigured || CalendarRepository.IsLocalHref(existing.CalendarHref))
        {
            if (!CalendarRepository.IsLocalHref(existing.CalendarHref))
            {
                _repository.AddPending(existing.ResourceHref, "update", existing.CalendarHref,
                    existing.ETag ?? string.Empty, localPayload);
            }

            return;
        }

        try
        {
            using var client = new CalDavClient(AppleId, Password);
            await client.ConnectAsync(token).ConfigureAwait(false);

            // 读-改-写：以云端最新版本为底稿，避免把手机端的修改覆盖掉。
            var payload = localPayload;
            var etag = existing.ETag;
            try
            {
                var (original, remoteEtag) = await client.GetEventAsync(existing.ResourceHref, token)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(original))
                {
                    payload = IcsCalendar.PatchMasterEvent(original, draft, sequence: 1);
                    if (!string.IsNullOrEmpty(remoteEtag))
                    {
                        etag = remoteEtag;
                    }
                }
            }
            catch
            {
                // 读不到就用本地母事件版本覆盖
            }

            await client.UpdateEventAsync(existing.ResourceHref, payload, etag, token)
                .ConfigureAwait(false);

            // 云端写回后把最终版本落到本地，避免本地保留一个与云端不同的派生版本。
            _repository.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = existing.CalendarHref,
                ResourceHref = existing.ResourceHref,
                ETag = etag,
                Ics = payload,
            });
        }
        catch
        {
            _repository.AddPending(existing.ResourceHref, "update", existing.CalendarHref,
                existing.ETag ?? string.Empty, localPayload);
            throw new CalDavPendingException("修改已保存到本地，云端同步失败，网络恢复后会自动补传。");
        }
    }

    /// <summary>
    /// 把日程整体挪到另一个日历（CalDAV 不支持跨集合移动，只能在新集合重新创建再删旧资源）。
    /// 先写本地新资源并立即生效，再推云端；旧资源删除失败就排队补删。
    /// </summary>
    public async Task<CalendarEvent> MoveEventToCalendarAsync(CalendarEvent existing,
        string targetCalendarHref, CalendarEventDraft draft, CancellationToken token = default)
    {
        // 先把已有的本地母事件改好（保留 EXDATE / 覆盖实例等），再原样搬到新集合。
        var ics = PatchLocalMaster(existing.ResourceHref, draft,
            BuildIcs(existing.Uid, draft, sequence: 1));

        var target = targetCalendarHref.TrimEnd('/');
        var resourceName = existing.ResourceHref.TrimEnd('/').Split('/').Last();
        var newHref = target + "/" + resourceName;

        var moved = new CalendarEvent
        {
            CalendarHref = targetCalendarHref,
            ResourceHref = newHref,
            Uid = existing.Uid,
            StartAt = draft.Start,
            EndAt = draft.End,
            IsAllDay = draft.AllDay,
            Summary = draft.Summary,
            Location = draft.Location,
            Notes = draft.Notes,
            Url = draft.Url,
            Categories = draft.Categories,
            IsBusy = draft.Busy,
            Status = "CONFIRMED",
            Class = string.IsNullOrWhiteSpace(draft.Class) ? "PUBLIC" : draft.Class.ToUpperInvariant(),
            Priority = draft.Priority,
            RRule = draft.RRule,
        };

        // 本地：旧集合的记录清掉，新集合写入并展开。
        _repository.RemoveMaster(existing.ResourceHref);
        _repository.RemoveEventsByResource(existing.ResourceHref);
        _repository.UpsertMaster(new RawCalendarItem
        {
            CalendarHref = targetCalendarHref,
            ResourceHref = newHref,
            ETag = string.Empty,
            Ics = ics,
        });
        _repository.ReplaceEventsForResource(
            targetCalendarHref, newHref,
            ExpandItems(new[]
            {
                new RawCalendarItem
                {
                    CalendarHref = targetCalendarHref,
                    ResourceHref = newHref,
                    ETag = string.Empty,
                    Ics = ics,
                },
            }, draft.Start.Date.AddMonths(-1), draft.End.Date.AddMonths(2)));
        EventsChanged?.Invoke(this, EventArgs.Empty);

        // 旧资源还等着补传时（本地新建未上云），直接改写队列指向新集合即可。
        if (_repository.HasPending(existing.ResourceHref, "create"))
        {
            _repository.RemovePending(existing.ResourceHref);
            _repository.AddPending(newHref, "create", targetCalendarHref, string.Empty, ics);
            ScheduleDeferredPush();
            return moved;
        }

        // 两边都是本地集合：纯本地搬家，没什么要推的。
        if (CalendarRepository.IsLocalHref(targetCalendarHref) &&
            CalendarRepository.IsLocalHref(existing.CalendarHref))
        {
            return moved;
        }

        // 没配账号（离线也能移动）：本地已换到新集合，云端那一步排进队列。
        if (!IsConfigured)
        {
            _repository.AddPending(newHref, "create", targetCalendarHref, string.Empty, ics);
            _repository.AddPending(existing.ResourceHref, "delete", existing.CalendarHref,
                existing.ETag ?? string.Empty);
            return moved;
        }

        try
        {
            using var client = new CalDavClient(AppleId, Password);
            await client.ConnectAsync(token).ConfigureAwait(false);
            await client.CreateEventAsync(targetCalendarHref, resourceName, ics, token)
                .ConfigureAwait(false);

            try
            {
                await client.DeleteEventAsync(existing.ResourceHref, existing.ETag, token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 新集合已经建好，旧的删不掉就排队补删，不能把这次移动整体判失败。
                _repository.AddPending(existing.ResourceHref, "delete", existing.CalendarHref,
                    existing.ETag ?? string.Empty);
            }
        }
        catch
        {
            // 新集合没建成：排进补传队列，等网络恢复后重试（重试是新建，天然幂等）。
            _repository.AddPending(newHref, "create", targetCalendarHref, string.Empty, ics);
            throw new CalDavPendingException("日程已移动到本地新日历，云端同步失败，网络恢复后会自动补传。");
        }

        return moved;
    }

    /// <summary>
    /// 拖动改期专用：只改本地并排进待推队列，静默期结束后合并成一次推送。
    /// 连续拖动多条日程时不会为每条都新建一次连接，也不会触发整窗同步。
    /// </summary>
    public void UpdateEventDeferred(CalendarEvent existing, CalendarEventDraft draft)
    {
        var localPayload = PatchLocalMaster(existing.ResourceHref, draft,
            BuildIcs(existing.Uid, draft, sequence: 1));
        SaveMaster(existing, localPayload);

        // 本地新建还没上云时继续排队为 create，否则推送会对一个不存在的资源做更新。
        if (_repository.HasPending(existing.ResourceHref, "create"))
        {
            _repository.AddPending(existing.ResourceHref, "create", existing.CalendarHref,
                string.Empty, localPayload);
        }
        else
        {
            _repository.AddPending(existing.ResourceHref, "update", existing.CalendarHref,
                existing.ETag ?? string.Empty, localPayload);
        }

        EventsChanged?.Invoke(this, EventArgs.Empty);
        ScheduleDeferredPush();
    }

    /// <summary>静默期计时：每来一次改动就重置，停下来之后再统一推送。</summary>
    private void ScheduleDeferredPush()
    {
        _pushCts?.Cancel();
        _pushCts?.Dispose();
        var cts = new CancellationTokenSource();
        _pushCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PushQuietPeriodMs, cts.Token).ConfigureAwait(false);
                await PushPendingQuietAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 又拖了一条，等下一轮静默期
            }
        });
    }

    /// <summary>用同一个连接把待推队列一次发完；失败就留在队列里等下次同步。</summary>
    private async Task PushPendingQuietAsync(CancellationToken token)
    {
        if (!IsConfigured)
        {
            return;
        }

        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
        {
            // 正在整窗同步，交给它顺带补发
            return;
        }

        SetSyncing(true);
        try
        {
            using var client = new CalDavClient(AppleId, Password);
            await client.ConnectAsync(token).ConfigureAwait(false);
            await FlushPendingAsync(client, token).ConfigureAwait(false);
        }
        catch
        {
            // 失败保持队列不动；下一次同步或下一次拖动会再试
        }
        finally
        {
            SetSyncing(false);
            _gate.Release();
        }
    }

    /// <summary>以本地母事件为底稿改写；没有母事件（旧缓存）时返回新生成的 ICS。</summary>
    private string PatchLocalMaster(string resourceHref, CalendarEventDraft draft, string fallback)
    {
        var local = _repository.GetMasterIcs(resourceHref);
        return string.IsNullOrWhiteSpace(local)
            ? fallback
            : IcsCalendar.PatchMasterEvent(local, draft, sequence: 1);
    }

    private void SaveMaster(CalendarEvent existing, string ics) =>
        _repository.UpsertMaster(new RawCalendarItem
        {
            CalendarHref = existing.CalendarHref,
            ResourceHref = existing.ResourceHref,
            ETag = existing.ETag,
            Ics = ics,
        });

    /// <summary>
    /// 编辑「整个系列」：只改母事件本身，保留已有的单独修改（RECURRENCE-ID 覆盖）和 EXDATE。
    /// </summary>
    public async Task UpdateSeriesAsync(CalendarEvent masterEvent, CalendarEventDraft draft,
        CancellationToken token = default)
    {
        await UpdateEventAsync(masterEvent, draft, token).ConfigureAwait(false);
    }

    /// <summary>取某个循环实例所属母事件的字段，用于「编辑整个系列」。</summary>
    public CalendarEvent? GetSeriesMaster(CalendarEvent instance)
    {
        var ics = _repository.GetMasterIcs(instance.ResourceHref);
        if (string.IsNullOrWhiteSpace(ics))
        {
            return null;
        }

        var master = IcsCalendar.ParseEvents(ics).FirstOrDefault(e => e.RecurrenceId is null);
        return master is null
            ? null
            : ToCalendarEvent(new RawCalendarItem
            {
                CalendarHref = instance.CalendarHref,
                ResourceHref = instance.ResourceHref,
                ETag = instance.ETag,
                Ics = ics,
            }, master, master.Start, isRecurring: true);
    }


    /// <summary>兼容旧调用：只给标题和时间。</summary>
    public Task<CalendarEvent> CreateEventAsync(string calendarHref, string summary,
        DateTime start, DateTime end, bool allDay, string location = "", string note = "",
        CancellationToken token = default) =>
        CreateEventAsync(calendarHref, new CalendarEventDraft
        {
            Summary = summary,
            Start = start,
            End = end,
            AllDay = allDay,
            Location = location,
            Notes = note,
        }, token);

    /// <summary>
    /// 修改循环日程中的某一次（写入 RECURRENCE-ID 覆盖实例）。
    /// 本地只改这份母事件里的那一个覆盖实例，其它日期保持不变。
    /// </summary>
    public async Task UpdateOccurrenceAsync(CalendarEvent occurrence, CalendarEventDraft draft,
        CancellationToken token = default)
    {
        var originalStart = occurrence.StartAt;
        var localMaster = _repository.GetMasterIcs(occurrence.ResourceHref);
        if (!string.IsNullOrWhiteSpace(localMaster))
        {
            var patched = IcsCalendar.UpsertOccurrence(localMaster, occurrence.Uid, originalStart,
                occurrence.EndAt, occurrence.IsAllDay, draft);
            SaveMaster(occurrence, patched);
            EventsChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            // 旧缓存没有母事件，只能先按当前改动落一条实例，下一次同步会以云端为准。
            _repository.RemoveSingleEvent(occurrence);
            _repository.AddEvent(new CalendarEvent
            {
                CalendarHref = occurrence.CalendarHref,
                ResourceHref = occurrence.ResourceHref,
                Uid = occurrence.Uid,
                StartAt = draft.Start,
                EndAt = draft.End,
                IsAllDay = draft.AllDay,
                Summary = draft.Summary,
                Location = draft.Location,
                Notes = draft.Notes,
                Url = draft.Url,
                Categories = draft.Categories,
                IsBusy = draft.Busy,
                Status = "CONFIRMED",
                Class = string.IsNullOrWhiteSpace(draft.Class) ? "PUBLIC" : draft.Class.ToUpperInvariant(),
                Priority = draft.Priority,
                ETag = occurrence.ETag,
            });
            EventsChanged?.Invoke(this, EventArgs.Empty);
        }

        // 本地日历或没配账号：本地已生效。本地集合没有云端对应，不进队列。
        if (!IsConfigured || CalendarRepository.IsLocalHref(occurrence.CalendarHref))
        {
            if (CalendarRepository.IsLocalHref(occurrence.CalendarHref))
            {
                return;
            }

            var queued = _repository.GetMasterIcs(occurrence.ResourceHref);
            if (!string.IsNullOrWhiteSpace(queued))
            {
                _repository.AddPending(occurrence.ResourceHref, "update", occurrence.CalendarHref,
                    occurrence.ETag ?? string.Empty, queued);
            }

            return;
        }

        try
        {
            using var client = new CalDavClient(AppleId, Password);
            await client.ConnectAsync(token).ConfigureAwait(false);
            var (original, etag) = await client.GetEventAsync(occurrence.ResourceHref, token)
                .ConfigureAwait(false);
            var payload = IcsCalendar.UpsertOccurrence(original, occurrence.Uid, originalStart,
                occurrence.EndAt, occurrence.IsAllDay, draft);
            await client.UpdateEventAsync(occurrence.ResourceHref, payload,
                    string.IsNullOrEmpty(etag) ? occurrence.ETag : etag, token)
                .ConfigureAwait(false);

            _repository.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = occurrence.CalendarHref,
                ResourceHref = occurrence.ResourceHref,
                ETag = string.IsNullOrEmpty(etag) ? occurrence.ETag : etag,
                Ics = payload,
            });
        }
        catch
        {
            throw new CalDavException("本次修改未同步到云端，请稍后重试。");
        }
    }

    /// <summary>删除循环日程中的某一次：给主事件加 EXDATE，只去掉这一天。</summary>
    public async Task DeleteOccurrenceAsync(CalendarEvent occurrence, CancellationToken token = default)
    {
        // 本地把这次写进 EXDATE，母事件仍然保留，后续实例继续展开。
        var localMaster = _repository.GetMasterIcs(occurrence.ResourceHref);
        if (!string.IsNullOrWhiteSpace(localMaster))
        {
            SaveMaster(occurrence, IcsCalendar.AddExclusion(localMaster, occurrence.StartAt));
        }
        else
        {
            _repository.RemoveSingleEvent(occurrence);
        }

        EventsChanged?.Invoke(this, EventArgs.Empty);

        // 本地日历或没配账号：本地已排除这一天。本地集合不进队列。
        if (!IsConfigured || CalendarRepository.IsLocalHref(occurrence.CalendarHref))
        {
            if (CalendarRepository.IsLocalHref(occurrence.CalendarHref))
            {
                return;
            }

            var queued = _repository.GetMasterIcs(occurrence.ResourceHref);
            if (!string.IsNullOrWhiteSpace(queued))
            {
                _repository.AddPending(occurrence.ResourceHref, "update", occurrence.CalendarHref,
                    occurrence.ETag ?? string.Empty, queued);
            }

            return;
        }

        try
        {
            using var client = new CalDavClient(AppleId, Password);
            await client.ConnectAsync(token).ConfigureAwait(false);
            var (original, etag) = await client.GetEventAsync(occurrence.ResourceHref, token)
                .ConfigureAwait(false);
            var payload = IcsCalendar.AddExclusion(original, occurrence.StartAt);
            await client.UpdateEventAsync(occurrence.ResourceHref, payload,
                    string.IsNullOrEmpty(etag) ? occurrence.ETag : etag, token)
                .ConfigureAwait(false);

            _repository.UpsertMaster(new RawCalendarItem
            {
                CalendarHref = occurrence.CalendarHref,
                ResourceHref = occurrence.ResourceHref,
                ETag = string.IsNullOrEmpty(etag) ? occurrence.ETag : etag,
                Ics = payload,
            });
        }
        catch
        {
            throw new CalDavException("本次删除未同步到云端，请稍后重试。");
        }
    }

    public async Task DeleteEventAsync(CalendarEvent calendarEvent, CancellationToken token = default)
    {
        // 先删本地：界面立刻消失；云端失败就排队等下次补删。
        _repository.RemoveMaster(calendarEvent.ResourceHref);
        _repository.RemoveEventsByResource(calendarEvent.ResourceHref);
        EventsChanged?.Invoke(this, EventArgs.Empty);

        // 本地日历：删本地就够了，云端没有对应资源。
        if (CalendarRepository.IsLocalHref(calendarEvent.CalendarHref))
        {
            return;
        }

        // 没配账号（离线也能删）：本地已删，排一条补删等配上账号再执行。
        if (!IsConfigured)
        {
            _repository.AddPending(calendarEvent.ResourceHref, "delete", calendarEvent.CalendarHref,
                calendarEvent.ETag ?? string.Empty);
            return;
        }

        try
        {
            using var client = new CalDavClient(AppleId, Password);
            await client.ConnectAsync(token).ConfigureAwait(false);
            await client.DeleteEventAsync(calendarEvent.ResourceHref, calendarEvent.ETag, token)
                .ConfigureAwait(false);
        }
        catch
        {
            _repository.AddPending(calendarEvent.ResourceHref, "delete", calendarEvent.CalendarHref,
                calendarEvent.ETag ?? string.Empty);
            throw new CalDavPendingException("已从本地删除，云端同步失败，网络恢复后会自动补删。");
        }
    }

}
