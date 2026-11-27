using Microsoft.Data.Sqlite;
using ToDooo.Models;

namespace ToDooo.Services;

/// <summary>
/// 日历事件的本地缓存。事件是远端数据的镜像：每次同步把某个集合在窗口内的事件整体替换，
/// 不做增量合并，避免复杂的冲突判断。账号凭据不落在这里，由 SettingsService 单独加密保存。
/// </summary>
public sealed class CalendarRepository
{
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private readonly string _connectionString;

    public CalendarRepository(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS calendar_sources (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                href         TEXT    NOT NULL UNIQUE,
                display_name TEXT    NOT NULL DEFAULT '',
                color        TEXT    NOT NULL DEFAULT '',
                is_visible   INTEGER NOT NULL DEFAULT 1,
                sort_order   INTEGER NOT NULL DEFAULT 0,
                sync_token   TEXT    NULL
            );

            CREATE TABLE IF NOT EXISTS calendar_events (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                calendar_href  TEXT    NOT NULL,
                resource_href  TEXT    NOT NULL,
                uid            TEXT    NOT NULL DEFAULT '',
                start_at       TEXT    NOT NULL,
                end_at         TEXT    NULL,
                is_all_day     INTEGER NOT NULL DEFAULT 0,
                summary        TEXT    NOT NULL DEFAULT '',
                location       TEXT    NOT NULL DEFAULT '',
                recurrence_id  TEXT    NULL,
                rrule          TEXT    NOT NULL DEFAULT '',
                etag           TEXT    NOT NULL DEFAULT '',
                last_modified  TEXT    NULL
            );

            CREATE TABLE IF NOT EXISTS calendar_pending (
                resource_href TEXT PRIMARY KEY,
                kind          TEXT NOT NULL,
                calendar_href TEXT NOT NULL,
                etag          TEXT NOT NULL DEFAULT '',
                payload       TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS calendar_event_masters (
                resource_href TEXT PRIMARY KEY,
                calendar_href TEXT NOT NULL,
                uid           TEXT NOT NULL DEFAULT '',
                etag          TEXT NOT NULL DEFAULT '',
                raw_ics       TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_events_range ON calendar_events(start_at);
            CREATE INDEX IF NOT EXISTS idx_events_calendar ON calendar_events(calendar_href);
            CREATE INDEX IF NOT EXISTS idx_masters_calendar ON calendar_event_masters(calendar_href);
            """;
        command.ExecuteNonQuery();

        // 旧库补列：SQLite 没有 ADD COLUMN IF NOT EXISTS，重复执行报错就忽略。
        foreach (var column in new[]
                 {
                     "notes TEXT NOT NULL DEFAULT ''",
                     "url TEXT NOT NULL DEFAULT ''",
                     "categories TEXT NOT NULL DEFAULT ''",
                     "is_busy INTEGER NOT NULL DEFAULT 1",
                     "status TEXT NOT NULL DEFAULT 'CONFIRMED'",
                     "class TEXT NOT NULL DEFAULT 'PUBLIC'",
                     "priority INTEGER NOT NULL DEFAULT 0",
                 })
        {
            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE calendar_events ADD COLUMN " + column;
                alter.ExecuteNonQuery();
            }
            catch
            {
                // 列已存在
            }
        }
    }

    public List<CalendarSource> GetSources()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, href, display_name, color, is_visible, sort_order, sync_token
            FROM calendar_sources
            ORDER BY sort_order ASC, display_name ASC
            """;

        var sources = new List<CalendarSource>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sources.Add(new CalendarSource
            {
                Id = reader.GetInt64(0),
                Href = reader.GetString(1),
                DisplayName = reader.GetString(2),
                Color = reader.GetString(3),
                IsVisible = reader.GetInt64(4) != 0,
                SortOrder = (int)reader.GetInt64(5),
                SyncToken = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }

        return sources;
    }

    /// <summary>合并远端发现的集合：已有的更新名称/颜色/顺序，用户设过可见性的保留，新的默认可见。</summary>
    public void MergeSources(IEnumerable<CalendarSource> discovered)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO calendar_sources (href, display_name, color, is_visible, sort_order)
            VALUES ($href, $name, $color, 1, $order)
            ON CONFLICT(href) DO UPDATE SET
                display_name = excluded.display_name,
                color = excluded.color,
                sort_order = excluded.sort_order
            """;
        var hrefParameter = command.Parameters.Add("$href", SqliteType.Text);
        var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
        var colorParameter = command.Parameters.Add("$color", SqliteType.Text);
        var orderParameter = command.Parameters.Add("$order", SqliteType.Integer);

        foreach (var source in discovered)
        {
            hrefParameter.Value = source.Href;
            nameParameter.Value = source.DisplayName;
            colorParameter.Value = source.Color;
            orderParameter.Value = source.SortOrder;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SetSourceVisible(string href, bool visible)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE calendar_sources SET is_visible = $visible WHERE href = $href";
        command.Parameters.AddWithValue("$visible", visible ? 1 : 0);
        command.Parameters.AddWithValue("$href", href);
        command.ExecuteNonQuery();

        // 日历筛选菜单和设置页共用这一份开关，任一处改动都要通知另一处，
        // 否则两边会各说各话（菜单里关掉了，设置里还勾着）。
        SourceVisibilityChanged?.Invoke(this, href);
    }

    /// <summary>某个日历的显示开关被改动了；参数是集合 href。</summary>
    public event EventHandler<string>? SourceVisibilityChanged;

    public void SetSourceColor(string href, string color)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE calendar_sources SET color = $color WHERE href = $href";
        command.Parameters.AddWithValue("$color", color);
        command.Parameters.AddWithValue("$href", href);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 本程序自建的本地日历：href 带 local: 前缀，永远不会和服务器集合路径撞。
    /// 本地日历只存本机，同步时云端也不会覆盖它。
    /// </summary>
    public const string LocalHrefPrefix = "local:";

    public static bool IsLocalHref(string? href) =>
        href is not null && href.StartsWith(LocalHrefPrefix, StringComparison.Ordinal);

    /// <summary>新建一个本地日历集合，返回生成的 href。</summary>
    public string AddLocalSource(string displayName, string color)
    {
        var href = LocalHrefPrefix + Guid.NewGuid().ToString("N");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO calendar_sources (href, display_name, color, is_visible, sort_order)
            VALUES ($href, $name, $color, 1,
                    (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM calendar_sources))
            """;
        command.Parameters.AddWithValue("$href", href);
        command.Parameters.AddWithValue("$name", displayName.Trim());
        command.Parameters.AddWithValue("$color", color);
        command.ExecuteNonQuery();
        return href;
    }

    public void RenameSource(string href, string displayName)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE calendar_sources SET display_name = $name WHERE href = $href";
        command.Parameters.AddWithValue("$name", displayName.Trim());
        command.Parameters.AddWithValue("$href", href);
        command.ExecuteNonQuery();
    }

    /// <summary>删除日历集合，连同它的事件、母事件和待补传项一并清掉。</summary>
    public void DeleteSource(string href)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        foreach (var sql in new[]
        {
            "DELETE FROM calendar_pending WHERE calendar_href = $href",
            "DELETE FROM calendar_event_masters WHERE calendar_href = $href",
            "DELETE FROM calendar_events WHERE calendar_href = $href",
            "DELETE FROM calendar_sources WHERE href = $href",
        })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$href", href);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
    /// <summary>按资源路径删除缓存事件（一个 .ics 可能含母事件与多个实例，一起删）。</summary>
    public void RemoveEventsByResource(string resourceHref)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM calendar_events WHERE resource_href = $href";
        command.Parameters.AddWithValue("$href", resourceHref);
        command.ExecuteNonQuery();
    }

    /// <summary>只删除某一条实例（循环日程里取消某一次时用）。</summary>
    public void RemoveSingleEvent(CalendarEvent calendarEvent)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM calendar_events
            WHERE resource_href = $res AND start_at = $start
            """;
        command.Parameters.AddWithValue("$res", calendarEvent.ResourceHref);
        command.Parameters.AddWithValue("$start", calendarEvent.StartAt.ToString(DateFormat));
        command.ExecuteNonQuery();
    }

    /// <summary>把刚创建/更新成功的单条事件写进缓存，省去再拉一次窗口数据。</summary>
    public void AddEvent(CalendarEvent item)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO calendar_events (calendar_href, resource_href, uid, start_at, end_at,
                                         is_all_day, summary, location, recurrence_id, rrule,
                                         etag, last_modified, notes, url, categories,
                                         is_busy, status, class, priority)
            VALUES ($cal, $res, $uid, $start, $end, $allday, $summary, $location,
                    $recur, $rrule, $etag, $modified, $notes, $url, $categories,
                    $busy, $status, $class, $priority)
            """;
        command.Parameters.AddWithValue("$cal", item.CalendarHref);
        command.Parameters.AddWithValue("$res", item.ResourceHref);
        command.Parameters.AddWithValue("$uid", item.Uid);
        command.Parameters.AddWithValue("$start", item.StartAt.ToString(DateFormat));
        command.Parameters.AddWithValue("$end", (object?)item.EndAt?.ToString(DateFormat) ?? DBNull.Value);
        command.Parameters.AddWithValue("$allday", item.IsAllDay ? 1 : 0);
        command.Parameters.AddWithValue("$summary", item.Summary ?? string.Empty);
        command.Parameters.AddWithValue("$location", item.Location ?? string.Empty);
        command.Parameters.AddWithValue("$recur", (object?)item.RecurrenceId?.ToString(DateFormat) ?? DBNull.Value);
        command.Parameters.AddWithValue("$rrule", item.RRule ?? string.Empty);
        command.Parameters.AddWithValue("$etag", item.ETag ?? string.Empty);
        command.Parameters.AddWithValue("$modified", (object?)item.LastModified?.ToString(DateFormat) ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", item.Notes ?? string.Empty);
        command.Parameters.AddWithValue("$url", item.Url ?? string.Empty);
        command.Parameters.AddWithValue("$categories", item.Categories ?? string.Empty);
        command.Parameters.AddWithValue("$busy", item.IsBusy ? 1 : 0);
        command.Parameters.AddWithValue("$status", string.IsNullOrEmpty(item.Status) ? "CONFIRMED" : item.Status);
        command.Parameters.AddWithValue("$class", string.IsNullOrEmpty(item.Class) ? "PUBLIC" : item.Class);
        command.Parameters.AddWithValue("$priority", item.Priority);
        command.ExecuteNonQuery();
    }

    /// <summary>只替换某一份资源展开出的平铺实例，同集合其它日程不受影响。</summary>
    public void ReplaceEventsForResource(string calendarHref, string resourceHref,
        IReadOnlyList<CalendarEvent> events)
    {
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "DELETE FROM calendar_events WHERE calendar_href = $href AND resource_href = $res";
            command.Parameters.AddWithValue("$href", calendarHref);
            command.Parameters.AddWithValue("$res", resourceHref);
            command.ExecuteNonQuery();
        }

        foreach (var item in events)
        {
            AddEvent(item);
        }
    }

    /// <summary>整体替换某个集合的全部缓存事件。远端返回什么就是什么，避免陈旧数据。</summary>
    public void ReplaceEvents(string calendarHref, IReadOnlyList<CalendarEvent> events)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        // 有待补传操作（新建/修改）的资源，其本地版本比云端更新，
        // 同步时不能拿远端旧数据把它覆盖掉。
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM calendar_events
                WHERE calendar_href = $href
                  AND resource_href NOT IN (SELECT resource_href FROM calendar_pending)
                """;
            delete.Parameters.AddWithValue("$href", calendarHref);
            delete.ExecuteNonQuery();
        }

        if (events.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO calendar_events (calendar_href, resource_href, uid, start_at, end_at,
                                             is_all_day, summary, location, recurrence_id, rrule,
                                             etag, last_modified, notes, url, categories,
                                             is_busy, status, class, priority)
                VALUES ($cal, $res, $uid, $start, $end, $allday, $summary, $location,
                        $recur, $rrule, $etag, $modified, $notes, $url, $categories,
                        $busy, $status, $class, $priority)
                """;
            var cal = insert.Parameters.Add("$cal", SqliteType.Text);
            var res = insert.Parameters.Add("$res", SqliteType.Text);
            var uid = insert.Parameters.Add("$uid", SqliteType.Text);
            var start = insert.Parameters.Add("$start", SqliteType.Text);
            var end = insert.Parameters.Add("$end", SqliteType.Text);
            var allDay = insert.Parameters.Add("$allday", SqliteType.Integer);
            var summary = insert.Parameters.Add("$summary", SqliteType.Text);
            var location = insert.Parameters.Add("$location", SqliteType.Text);
            var recur = insert.Parameters.Add("$recur", SqliteType.Text);
            var rrule = insert.Parameters.Add("$rrule", SqliteType.Text);
            var etag = insert.Parameters.Add("$etag", SqliteType.Text);
            var modified = insert.Parameters.Add("$modified", SqliteType.Text);
            var notes = insert.Parameters.Add("$notes", SqliteType.Text);
            var url = insert.Parameters.Add("$url", SqliteType.Text);
            var categories = insert.Parameters.Add("$categories", SqliteType.Text);
            var busy = insert.Parameters.Add("$busy", SqliteType.Integer);
            var status = insert.Parameters.Add("$status", SqliteType.Text);
            var klass = insert.Parameters.Add("$class", SqliteType.Text);
            var priority = insert.Parameters.Add("$priority", SqliteType.Integer);

            // 已存在的（待补传）资源不重复插入。
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT resource_href FROM calendar_events WHERE calendar_href = $href";
                query.Parameters.AddWithValue("$href", calendarHref);
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    existing.Add(reader.GetString(0));
                }
            }

            foreach (var item in events)
            {
                if (!existing.Add(item.ResourceHref))
                {
                    continue;
                }

                cal.Value = item.CalendarHref;
                res.Value = item.ResourceHref;
                uid.Value = item.Uid;
                start.Value = item.StartAt.ToString(DateFormat);
                end.Value = (object?)item.EndAt?.ToString(DateFormat) ?? DBNull.Value;
                allDay.Value = item.IsAllDay ? 1 : 0;
                summary.Value = item.Summary ?? string.Empty;
                location.Value = item.Location ?? string.Empty;
                recur.Value = (object?)item.RecurrenceId?.ToString(DateFormat) ?? DBNull.Value;
                rrule.Value = item.RRule ?? string.Empty;
                etag.Value = item.ETag ?? string.Empty;
                modified.Value = (object?)item.LastModified?.ToString(DateFormat) ?? DBNull.Value;
                notes.Value = item.Notes ?? string.Empty;
                url.Value = item.Url ?? string.Empty;
                categories.Value = item.Categories ?? string.Empty;
                busy.Value = item.IsBusy ? 1 : 0;
                status.Value = string.IsNullOrEmpty(item.Status) ? "CONFIRMED" : item.Status;
                klass.Value = string.IsNullOrEmpty(item.Class) ? "PUBLIC" : item.Class;
                priority.Value = item.Priority;
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>读取窗口内的可见事件，供日历渲染。</summary>
    public List<CalendarEvent> GetEventsInRange(DateTime from, DateTime to, IReadOnlyDictionary<string, string> colorByHref)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id, e.calendar_href, e.resource_href, e.uid, e.start_at, e.end_at,
                   e.is_all_day, e.summary, e.location, e.recurrence_id, e.rrule,
                   e.etag, e.last_modified, e.notes, e.url, e.categories,
                   e.is_busy, e.status, e.class, e.priority
            FROM calendar_events e
            JOIN calendar_sources s ON s.href = e.calendar_href
            WHERE s.is_visible = 1
              AND e.start_at >= $from
              AND e.start_at < $to
            ORDER BY e.start_at ASC, e.summary ASC
            """;
        command.Parameters.AddWithValue("$from", from.ToString(DateFormat));
        command.Parameters.AddWithValue("$to", to.ToString(DateFormat));

        var events = new List<CalendarEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var href = reader.GetString(1);
            events.Add(new CalendarEvent
            {
                Id = reader.GetInt64(0),
                CalendarHref = href,
                ResourceHref = reader.GetString(2),
                Uid = reader.GetString(3),
                StartAt = Parse(reader.GetString(4)) ?? DateTime.MinValue,
                EndAt = reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
                IsAllDay = reader.GetInt64(6) != 0,
                Summary = reader.GetString(7),
                Location = reader.GetString(8),
                RecurrenceId = reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
                RRule = reader.GetString(10),
                ETag = reader.GetString(11),
                LastModified = reader.IsDBNull(12) ? null : Parse(reader.GetString(12)),
                Notes = reader.GetString(13),
                Url = reader.GetString(14),
                Categories = reader.GetString(15),
                IsBusy = reader.GetInt64(16) != 0,
                Status = reader.GetString(17),
                Class = reader.GetString(18),
                Priority = (int)reader.GetInt64(19),
                Color = colorByHref.TryGetValue(href, out var color) ? color : string.Empty,
            });
        }

        return events;
    }

    /// <summary>查某条资源当前的 etag，用于判断能否安全写入。</summary>
    public string? GetETag(string resourceHref)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT etag FROM calendar_events WHERE resource_href = $href LIMIT 1";
        command.Parameters.AddWithValue("$href", resourceHref);
        return command.ExecuteScalar() as string;
    }

    /// <summary>记一条待重试的远端操作（create/delete），网络恢复后由同步统一补发。</summary>
    public void AddPending(string resourceHref, string kind, string calendarHref,
        string etag = "", string payload = "")
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO calendar_pending (resource_href, kind, calendar_href, etag, payload)
            VALUES ($res, $kind, $cal, $etag, $payload)
            ON CONFLICT(resource_href) DO UPDATE SET
                kind = excluded.kind,
                calendar_href = excluded.calendar_href,
                etag = excluded.etag,
                payload = excluded.payload
            """;
        command.Parameters.AddWithValue("$res", resourceHref);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$cal", calendarHref);
        command.Parameters.AddWithValue("$etag", etag);
        command.Parameters.AddWithValue("$payload", payload);
        command.ExecuteNonQuery();
    }

    public List<(string ResourceHref, string Kind, string CalendarHref, string ETag, string Payload)> GetPending()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT resource_href, kind, calendar_href, etag, payload FROM calendar_pending";

        var result = new List<(string, string, string, string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4)));
        }

        return result;
    }

    public void RemovePending(string resourceHref)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM calendar_pending WHERE resource_href = $res";
        command.Parameters.AddWithValue("$res", resourceHref);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 推送成功后清队列；若期间本地又改了同一资源（payload 已变），保留新内容不要误删。
    /// </summary>
    public void RemovePending(string resourceHref, string pushedPayload)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM calendar_pending
            WHERE resource_href = $res AND payload = $payload
            """;
        command.Parameters.AddWithValue("$res", resourceHref);
        command.Parameters.AddWithValue("$payload", pushedPayload);
        command.ExecuteNonQuery();
    }

    /// <summary>某资源是否已有指定类型的待补传操作。</summary>
    public bool HasPending(string resourceHref, string kind)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM calendar_pending WHERE resource_href = $res AND kind = $kind";
        command.Parameters.AddWithValue("$res", resourceHref);
        command.Parameters.AddWithValue("$kind", kind);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) > 0;
    }

    public int PendingCount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM calendar_pending";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// 保存/替换一批资源的原始 ICS（母事件 + 覆盖实例都在这份文本里）。
    /// 同步以它为准，读取时再按窗口展开，避免平铺实例在编辑/删除后与云端不一致。
    /// </summary>
    public void UpsertMasters(string calendarHref, IEnumerable<RawCalendarItem> items)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO calendar_event_masters (resource_href, calendar_href, uid, etag, raw_ics)
            VALUES ($res, $cal, $uid, $etag, $ics)
            ON CONFLICT(resource_href) DO UPDATE SET
                calendar_href = excluded.calendar_href,
                uid = excluded.uid,
                etag = excluded.etag,
                raw_ics = excluded.raw_ics
            """;
        var res = command.Parameters.Add("$res", SqliteType.Text);
        var cal = command.Parameters.Add("$cal", SqliteType.Text);
        var uid = command.Parameters.Add("$uid", SqliteType.Text);
        var etag = command.Parameters.Add("$etag", SqliteType.Text);
        var ics = command.Parameters.Add("$ics", SqliteType.Text);

        foreach (var item in items)
        {
            var first = IcsCalendar.ParseEvents(item.Ics ?? string.Empty).FirstOrDefault();
            res.Value = item.ResourceHref;
            cal.Value = item.CalendarHref;
            uid.Value = first?.Uid ?? string.Empty;
            etag.Value = item.ETag ?? string.Empty;
            ics.Value = item.Ics ?? string.Empty;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>替换某集合的母事件集合：远端没再返回的资源直接丢弃（除了还等着补传的）。</summary>
    public void ReplaceMasters(string calendarHref, IReadOnlyList<RawCalendarItem> items)
    {
        using var connection = Open();

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = """
                DELETE FROM calendar_event_masters
                WHERE calendar_href = $href
                  AND resource_href NOT IN (SELECT resource_href FROM calendar_pending)
                """;
            delete.Parameters.AddWithValue("$href", calendarHref);
            delete.ExecuteNonQuery();
        }

        // 有待补传的资源，本地版本比远端新，不能用远端内容覆盖。
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT resource_href FROM calendar_pending";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                pending.Add(reader.GetString(0));
            }
        }

        var fresh = items.Where(item => !pending.Contains(item.ResourceHref)).ToList();
        if (fresh.Count > 0)
        {
            UpsertMasters(calendarHref, fresh);
        }
    }

    /// <summary>写入单条资源的母事件原始 ICS（本地新建/编辑后立即生效）。</summary>
    public void UpsertMaster(RawCalendarItem item) =>
        UpsertMasters(item.CalendarHref, new[] { item });

    /// <summary>读取所有可见日历的母事件原始 ICS，供按窗口展开。</summary>
    public List<RawCalendarItem> GetVisibleMasters()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.calendar_href, m.resource_href, m.etag, m.raw_ics
            FROM calendar_event_masters m
            JOIN calendar_sources s ON s.href = m.calendar_href
            WHERE s.is_visible = 1
            """;

        var items = new List<RawCalendarItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new RawCalendarItem
            {
                CalendarHref = reader.GetString(0),
                ResourceHref = reader.GetString(1),
                ETag = reader.GetString(2),
                Ics = reader.GetString(3),
            });
        }

        return items;
    }

    /// <summary>取单条资源的母事件原始 ICS；不存在返回 null。</summary>
    public string? GetMasterIcs(string resourceHref)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw_ics FROM calendar_event_masters WHERE resource_href = $res";
        command.Parameters.AddWithValue("$res", resourceHref);
        return command.ExecuteScalar() as string;
    }

    /// <summary>读取某个日历集合下的全部母事件（不区分是否可见）。合并向导用得到。</summary>
    public List<RawCalendarItem> GetMastersForCalendar(string calendarHref)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT calendar_href, resource_href, etag, raw_ics
            FROM calendar_event_masters
            WHERE calendar_href = $href
            ORDER BY resource_href
            """;
        command.Parameters.AddWithValue("$href", calendarHref);

        var items = new List<RawCalendarItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new RawCalendarItem
            {
                CalendarHref = reader.GetString(0),
                ResourceHref = reader.GetString(1),
                ETag = reader.GetString(2),
                Ics = reader.GetString(3),
            });
        }

        return items;
    }

    /// <summary>某个日历集合下的母事件条数（合并向导里显示规模）。</summary>
    public int CountMastersForCalendar(string calendarHref)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM calendar_event_masters WHERE calendar_href = $href";
        command.Parameters.AddWithValue("$href", calendarHref);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    public void RemoveMaster(string resourceHref)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM calendar_event_masters WHERE resource_href = $res";
        command.Parameters.AddWithValue("$res", resourceHref);
        command.ExecuteNonQuery();
    }

    public void ClearAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM calendar_events; DELETE FROM calendar_event_masters; DELETE FROM calendar_sources;";
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static DateTime? Parse(string? value) =>
        value is null ? null : DateTime.TryParse(value, out var parsed) ? parsed : null;
}
