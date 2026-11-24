using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using ToDooo.Models;

namespace ToDooo.Services;

/// <summary>CalDAV 调用失败的统一异常，携带 HTTP 状态码便于上层区分 401（凭据错）与网络错误。</summary>
public sealed class CalDavException : Exception
{
    public CalDavException(string message, int statusCode = 0, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>
/// iCloud CalDAV 客户端。已验证的协议要点都在这里落地：
/// - 发现链：caldav.icloud.com → current-user-principal → calendar-home-set（会落到分片域名）
/// - 301 必须手动跟随：HttpClient 会把 PROPFIND 降级成 GET
/// - supported-calendar-component-set 里 comp 用单引号
/// - calendar-multiget 必须用 path 形式的 href
/// - calendar-data 可能被 CDATA 包裹
/// </summary>
public sealed class CalDavClient : IDisposable
{
    private const string DiscoveryBase = "https://caldav.icloud.com";
    private static readonly Regex ResponseRegex =
        new(@"(?s)<[^>]*:?response[ >].*?</[^>]*:?response>", RegexOptions.Compiled);
    private static readonly Regex HrefRegex =
        new(@"<[^>]*:?href[^>]*>\s*([^<]+?)\s*</[^>]*:?href>", RegexOptions.Compiled);
    private static readonly Regex DisplayNameRegex =
        new(@"<[^>]*:?displayname[^>]*>\s*([^<]*)\s*</[^>]*:?displayname>", RegexOptions.Compiled);
    private static readonly Regex CompRegex =
        new(@"comp\s+name=[""']([^""']+)[""']", RegexOptions.Compiled);
    private static readonly Regex CalendarColorRegex =
        new(@"<[^>]*:?calendar-color[^>]*>\s*([^<]*)\s*</[^>]*:?calendar-color>", RegexOptions.Compiled);
    private static readonly Regex CalendarOrderRegex =
        new(@"<[^>]*:?calendar-order[^>]*>\s*([^<]*)\s*</[^>]*:?calendar-order>", RegexOptions.Compiled);
    private static readonly Regex CalendarDataRegex =
        new(@"(?s)<[^>]*calendar-data[^>]*>(.*?)</[^>]*calendar-data>", RegexOptions.Compiled);
    private static readonly Regex ETagRegex =
        new(@"<[^>]*:?getetag[^>]*>\s*([^<]*)\s*</[^>]*:?getetag>", RegexOptions.Compiled);

    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod ReportMethod = new("REPORT");

    private readonly HttpClient _client;
    private string _homeUrl = string.Empty;
    private string _homeBase = string.Empty;

    public CalDavClient(string appleId, string appPassword)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
        };
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ToDooo/1.6 CalDAV");
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{appleId}:{appPassword}"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public string HomeBase => _homeBase;

    /// <summary>校验凭据并解析出 calendar-home-set。失败抛 CalDavException（401 表示账号或密码不对）。</summary>
    public async Task<string> ConnectAsync(CancellationToken token = default)
    {
        var principalBody = """
            <D:propfind xmlns:D="DAV:"><D:prop><D:current-user-principal/></D:prop></D:propfind>
            """;
        var (status, body) = await SendAsync(PropFindMethod, DiscoveryBase + "/", principalBody, token);
        if (status == HttpStatusCode.Unauthorized)
        {
            throw new CalDavException("Apple ID 或应用专用密码不正确。", 401);
        }

        if ((int)status >= 400)
        {
            throw new CalDavException($"发现服务失败（HTTP {(int)status}）。", (int)status);
        }

        var principal = GetHref(MatchBlock(body, "current-user-principal"));
        if (string.IsNullOrEmpty(principal))
        {
            throw new CalDavException("服务器没有返回 current-user-principal。", (int)status);
        }

        var principalUrl = Absolute(DiscoveryBase + "/", principal);

        var homeBody = """
            <D:propfind xmlns:D="DAV:"><D:prop><C:calendar-home-set xmlns:C="urn:ietf:params:xml:ns:caldav"/></D:prop></D:propfind>
            """;
        var homeResponse = await SendAsync(PropFindMethod, principalUrl, homeBody, token);
        if (homeResponse.Status == HttpStatusCode.Unauthorized)
        {
            throw new CalDavException("Apple ID 或应用专用密码不正确。", 401);
        }

        var homeHref = GetHref(MatchBlock(homeResponse.Body, "calendar-home-set"));
        if (string.IsNullOrEmpty(homeHref))
        {
            throw new CalDavException($"服务器没有返回 calendar-home-set（HTTP {(int)homeResponse.Status}）。",
                (int)homeResponse.Status);
        }

        _homeUrl = Absolute(principalUrl, homeHref);
        _homeBase = new Uri(_homeUrl).GetLeftPart(UriPartial.Authority);
        return _homeUrl;
    }

    /// <summary>列出所有支持 VEVENT 的日历集合。</summary>
    public async Task<List<CalendarSource>> GetCalendarsAsync(CancellationToken token = default)
    {
        EnsureConnected();
        var body = """
            <D:propfind xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:A="http://apple.com/ns/ical/">
              <D:prop>
                <D:displayname/>
                <D:resourcetype/>
                <C:supported-calendar-component-set/>
                <A:calendar-color/>
                <A:calendar-order/>
              </D:prop>
            </D:propfind>
            """;

        var (status, response) = await SendAsync(PropFindMethod, _homeUrl, body, token, depth: "1");
        if ((int)status >= 400)
        {
            throw new CalDavException($"读取日历列表失败（HTTP {(int)status}）。", (int)status);
        }

        var sources = new List<CalendarSource>();
        foreach (Match match in ResponseRegex.Matches(response))
        {
            var chunk = match.Value;
            var href = GetHref(chunk);
            if (string.IsNullOrEmpty(href) || !href.EndsWith('/'))
            {
                continue;
            }

            var components = CompRegex.Matches(chunk).Select(m => m.Groups[1].Value).ToList();
            if (!components.Contains("VEVENT"))
            {
                continue;
            }

            // 收件箱/发件箱也会带 VEVENT 说明，但它们不是可查询的日历集合。
            var normalized = href.TrimEnd('/');
            if (normalized.EndsWith("/inbox", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith("/outbox", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = Absolute(_homeUrl, href);
            if (string.Equals(url, _homeUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nameMatch = DisplayNameRegex.Match(chunk);
            var colorMatch = CalendarColorRegex.Match(chunk);
            var orderMatch = CalendarOrderRegex.Match(chunk);

            sources.Add(new CalendarSource
            {
                Href = href,
                DisplayName = nameMatch.Success
                    ? WebUtility.HtmlDecode(nameMatch.Groups[1].Value)
                    : string.Empty,
                Color = colorMatch.Success ? colorMatch.Groups[1].Value.Trim() : string.Empty,
                SortOrder = orderMatch.Success && int.TryParse(orderMatch.Groups[1].Value.Trim(), out var order)
                    ? order
                    : 0,
                IsVisible = true,
            });
        }

        return sources.OrderBy(s => s.SortOrder).ThenBy(s => s.DisplayName).ToList();
    }

    /// <summary>
    /// 拉取某个集合在 [from, to) 内的事件（含循环事件的母事件）。
    /// 返回原始 ICS 与 etag，由上层负责展开与写缓存。
    /// </summary>
    public async Task<List<RawCalendarItem>> QueryEventsAsync(
        string calendarHref, DateTime from, DateTime to, CancellationToken token = default)
    {
        EnsureConnected();
        var calendarUrl = Absolute(_homeUrl, calendarHref);

        // 时间窗往前多要一天：循环事件母事件可能早于窗口开始。
        var queryStart = IcsCalendar.FormatUtcStamp(from.Date.AddDays(-1));
        var queryEnd = IcsCalendar.FormatUtcStamp(to.Date.AddDays(1));
        var body = $"""
            <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop><D:getetag/><C:calendar-data/></D:prop>
              <C:filter>
                <C:comp-filter name="VCALENDAR">
                  <C:comp-filter name="VEVENT">
                    <C:time-range start="{queryStart}" end="{queryEnd}"/>
                  </C:comp-filter>
                </C:comp-filter>
              </C:filter>
            </C:calendar-query>
            """;

        var (status, response) = await SendAsync(ReportMethod, calendarUrl, body, token, depth: "1");
        if ((int)status >= 400)
        {
            throw new CalDavException($"读取事件失败（HTTP {(int)status}）。", (int)status);
        }

        var items = new List<RawCalendarItem>();
        foreach (Match match in ResponseRegex.Matches(response))
        {
            var chunk = match.Value;
            var href = GetHref(chunk);
            var dataMatch = CalendarDataRegex.Match(chunk);
            if (string.IsNullOrEmpty(href) || !dataMatch.Success)
            {
                continue;
            }

            var ics = UnwrapCalendarData(dataMatch.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(ics))
            {
                continue;
            }

            var etagMatch = ETagRegex.Match(chunk);
            items.Add(new RawCalendarItem
            {
                ResourceHref = href,
                ETag = etagMatch.Success ? etagMatch.Groups[1].Value.Trim().Trim('"') : string.Empty,
                Ics = ics,
                CalendarHref = calendarHref,
            });
        }

        return items;
    }

    /// <summary>新建事件。resourceName 是文件名（不含路径），需保证集合内唯一。</summary>
    public async Task<string> CreateEventAsync(
        string calendarHref, string resourceName, string ics, CancellationToken token = default)
    {
        EnsureConnected();
        var url = Absolute(_homeUrl, calendarHref.TrimEnd('/') + "/" + resourceName);
        var (status, _) = await SendAsync(HttpMethod.Put, url, ics, token,
            contentType: "text/calendar; charset=utf-8",
            extraHeaders: new Dictionary<string, string> { ["If-None-Match"] = "*" });

        if (status == HttpStatusCode.PreconditionFailed)
        {
            throw new CalDavException("已有同名事件，改用更新而不是新建。", 412);
        }

        if ((int)status >= 400)
        {
            throw new CalDavException($"新建事件失败（HTTP {(int)status}）。", (int)status);
        }

        return url;
    }

    /// <summary>读取单个事件的原始 ICS 与 etag，用于「读-改-写」式编辑（保留提醒等未管理字段）。</summary>
    public async Task<(string Ics, string ETag)> GetEventAsync(string resourceHref,
        CancellationToken token = default)
    {
        EnsureConnected();
        var url = Absolute(_homeUrl, resourceHref);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "text/calendar");
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new CalDavException("连接 iCloud 失败，请检查网络。", 0, exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if ((int)response.StatusCode >= 400)
            {
                throw new CalDavException($"读取事件失败（HTTP {(int)response.StatusCode}）。",
                    (int)response.StatusCode);
            }

            var etag = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;
            return (body, etag);
        }
    }

    /// <summary>覆盖式更新事件。etag 非空时用 If-Match 做乐观并发；412 表示远端已被改过。</summary>
    public async Task UpdateEventAsync(string resourceHref, string ics, string? etag,
        CancellationToken token = default)
    {
        EnsureConnected();
        var url = Absolute(_homeUrl, resourceHref);
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(etag))
        {
            headers["If-Match"] = $"\"{etag.Trim('"')}\"";
        }

        var (status, body) = await SendAsync(HttpMethod.Put, url, ics, token,
            contentType: "text/calendar; charset=utf-8", extraHeaders: headers);

        if (status == HttpStatusCode.PreconditionFailed)
        {
            var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : "｜" + body.Trim();
            throw new CalDavException(
                $"这条事件在别处被改过，请先刷新再改。（etag={etag ?? "null"}{detail}）", 412);
        }

        if ((int)status >= 400)
        {
            throw new CalDavException($"更新事件失败（HTTP {(int)status}）。", (int)status);
        }
    }

    public async Task DeleteEventAsync(string resourceHref, string? etag = null, CancellationToken token = default)
    {
        EnsureConnected();
        var url = Absolute(_homeUrl, resourceHref);
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(etag))
        {
            headers["If-Match"] = $"\"{etag.Trim('"')}\"";
        }

        var (status, _) = await SendAsync(HttpMethod.Delete, url, null, token, extraHeaders: headers);
        if (status == HttpStatusCode.NotFound)
        {
            return;
        }

        if (status == HttpStatusCode.PreconditionFailed)
        {
            throw new CalDavException("这条事件在别处被改过，请先刷新再删。", 412);
        }

        if ((int)status >= 400)
        {
            throw new CalDavException($"删除事件失败（HTTP {(int)status}）。", (int)status);
        }
    }

    private void EnsureConnected()
    {
        if (string.IsNullOrEmpty(_homeUrl))
        {
            throw new CalDavException("尚未连接服务器。");
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method, string url, string? body, CancellationToken token,
        string depth = "0", string? contentType = null,
        Dictionary<string, string>? extraHeaders = null)
    {
        var response = await SendOnceAsync(method, url, body, token, depth, contentType, extraHeaders);
        // 301/302 手动跟一次：HttpClient 自动跳转会把 PROPFIND 变成 GET。
        if ((int)response.Status is 301 or 302 or 307 or 308 && response.Location is { } location)
        {
            var target = Absolute(url, location.ToString());
            response = await SendOnceAsync(method, target, body, token, depth, contentType, extraHeaders);
        }

        return (response.Status, response.Body);
    }

    private async Task<(HttpStatusCode Status, string Body, Uri? Location)> SendOnceAsync(
        HttpMethod method, string url, string? body, CancellationToken token,
        string depth, string? contentType, Dictionary<string, string>? extraHeaders)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Depth", depth);
        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                contentType ?? "application/xml; charset=utf-8");
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!token.IsCancellationRequested)
        {
            throw new CalDavException("连接 iCloud 超时，请检查网络。", 0, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new CalDavException("连接 iCloud 失败，请检查网络。", 0, exception);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return (response.StatusCode, text, response.Headers.Location);
        }
    }

    private static string MatchBlock(string body, string propertyName)
    {
        var match = Regex.Match(body,
            $@"(?s)<[^>]*{Regex.Escape(propertyName)}[^>]*>.*?</[^>]*{Regex.Escape(propertyName)}>");
        return match.Success ? match.Value : string.Empty;
    }

    private static string? GetHref(string chunk)
    {
        var match = HrefRegex.Match(chunk);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string UnwrapCalendarData(string raw)
    {
        var trimmed = raw.Trim();
        var cdata = Regex.Match(trimmed, @"(?s)^\s*<!\[CDATA\[(.*?)\]\]>\s*$");
        var ics = cdata.Success ? cdata.Groups[1].Value : WebUtility.HtmlDecode(trimmed);
        return ics.Replace("\r\n", "\n").Trim();
    }

    private static string Absolute(string baseUrl, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        return new Uri(new Uri(baseUrl), href).ToString();
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>一次同步拿到的远端原始条目：完整 ICS + etag + 资源路径。</summary>
public sealed class RawCalendarItem
{
    public string CalendarHref { get; init; } = string.Empty;

    public string ResourceHref { get; init; } = string.Empty;

    public string ETag { get; init; } = string.Empty;

    public string Ics { get; init; } = string.Empty;
}
