using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ToDooo.Services;

/// <summary>地点检索用哪个数据源（可在设置里切换）。</summary>
public enum PlaceSource
{
    /// <summary>百度地图联想：国内直连、免 Key。</summary>
    Baidu = 0,

    /// <summary>高德开放平台：国内最快，需要自己申请免费 Key。</summary>
    Amap = 1,

    /// <summary>OpenStreetMap 官方 Nominatim：免 Key，海外服务器。</summary>
    Nominatim = 2,

    /// <summary>Photon（同样是 OSM 数据）：免 Key，海外服务器。</summary>
    Photon = 3,
}

/// <summary>「测试」按钮的结果：是否可用 + 一句能看懂的话。</summary>
public sealed record PlaceTestResult(bool Success, string Message);

/// <summary>
/// 地点搜索：按设置里选的数据源查候选地点。
/// 只取地名与地址文本，不上传任何日程内容；任何失败都返回空列表，不挡着保存日程。
/// </summary>
public static class PlaceSearchService
{
    private const string DomesticIpCityEndpoint = "https://whois.pconline.com.cn/ipJson.jsp?json=true";

    private sealed record LocalAnchor(double Lat, double Lon, string Viewbox);

    private static readonly HttpClient Http = CreateClient();
    private static readonly Lazy<Task<LocalAnchor?>> CurrentAnchor = new(ResolveLocalAnchorAsync);

    private static PlaceSource _source = PlaceSource.Baidu;
    private static string _amapKey = string.Empty;

    /// <summary>当前数据源，供设置界面回显。</summary>
    public static PlaceSource Source => _source;

    /// <summary>按设置应用数据源；启动时与设置变更时各调一次。</summary>
    public static void Configure(int source, string? amapKey)
    {
        _source = Enum.IsDefined(typeof(PlaceSource), source) ? (PlaceSource)source : PlaceSource.Baidu;
        _amapKey = (amapKey ?? string.Empty).Trim();
    }

    private static HttpClient CreateClient()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 忽略注册失败
        }

        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // Nominatim 的使用政策要求带可识别的 User-Agent，否则会被限流。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ToDooo/1.10 (calendar app; contact via github.com/yeyt97/ToDooo)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    // ---- 入口 ---------------------------------------------------------------

    /// <summary>
    /// 按关键词搜地点，返回最多 <paramref name="limit"/> 条候选。
    /// 网络失败返回空列表：地点是可选填项，不该因为它挡住保存。
    /// </summary>
    public static async Task<IReadOnlyList<PlaceSuggestion>> SearchAsync(
        string query, int limit = 6, CancellationToken token = default)
    {
        var keyword = (query ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            return Array.Empty<PlaceSuggestion>();
        }

        try
        {
            var clampedLimit = PlaceParsers.ClampLimit(limit);
            return _source switch
            {
                PlaceSource.Baidu => await SearchBaiduAsync(keyword, clampedLimit, token).ConfigureAwait(false),
                PlaceSource.Amap => await SearchAmapAsync(keyword, clampedLimit, token).ConfigureAwait(false),
                PlaceSource.Photon => await SearchPhotonAsync(keyword, clampedLimit, token).ConfigureAwait(false),
                _ => await SearchNominatimAsync(keyword, clampedLimit, token).ConfigureAwait(false),
            };
        }
        catch
        {
            return Array.Empty<PlaceSuggestion>();
        }
    }

    /// <summary>
    /// 「测试」按钮：用给定数据源真查一次，返回是否可用、耗时与失败原因。
    /// 不受当前保存的设置影响，方便先试再选。
    /// </summary>
    public static async Task<PlaceTestResult> TestAsync(
        int source, string? amapKey, string keyword = "温州", CancellationToken token = default)
    {
        var provider = Enum.IsDefined(typeof(PlaceSource), source) ? (PlaceSource)source : PlaceSource.Baidu;
        var key = (amapKey ?? string.Empty).Trim();
        if (provider == PlaceSource.Amap && key.Length == 0)
        {
            return new PlaceTestResult(false, "先填写高德 Key 再测试");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew();
        try
        {
            List<PlaceHit> hits;
            string? error = null;
            switch (provider)
            {
                case PlaceSource.Baidu:
                    hits = await QueryAsync(PlaceParsers.BaiduUrl(keyword), json => PlaceParsers.ParseBaidu(json, 3), cts.Token).ConfigureAwait(false);
                    break;
                case PlaceSource.Amap:
                    (hits, error) = await QueryAmapAsync(PlaceParsers.AmapUrl(keyword, 3, key), cts.Token).ConfigureAwait(false);
                    break;
                case PlaceSource.Photon:
                    hits = await QueryAsync(PlaceParsers.PhotonUrl(keyword, 3), PlaceParsers.ParsePhoton, cts.Token).ConfigureAwait(false);
                    break;
                default:
                    hits = await QueryAsync(PlaceParsers.NominatimUrl(keyword, 3), PlaceParsers.ParseNominatim, cts.Token).ConfigureAwait(false);
                    break;
            }

            watch.Stop();
            var ms = (int)watch.ElapsedMilliseconds;
            if (error is not null)
            {
                return new PlaceTestResult(false, error);
            }

            return hits.Count > 0
                ? new PlaceTestResult(true, $"可用 · 返回 {hits.Count} 条 · {ms} ms")
                : new PlaceTestResult(false, $"能连上但没查到结果（{ms} ms）");
        }
        catch (OperationCanceledException)
        {
            return new PlaceTestResult(false, "超时：10 秒内没有响应");
        }
        catch (HttpRequestException ex)
        {
            return new PlaceTestResult(false, $"连接失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new PlaceTestResult(false, $"出错：{ex.Message}");
        }
    }

    // ---- 各数据源 -----------------------------------------------------------

    private static async Task<IReadOnlyList<PlaceSuggestion>> SearchBaiduAsync(
        string keyword, int limit, CancellationToken token)
    {
        var hits = await QueryAsync(
            PlaceParsers.BaiduUrl(keyword),
            json => PlaceParsers.ParseBaidu(json, limit),
            token).ConfigureAwait(false);
        return hits.Select(h => h.Suggestion).ToList();
    }

    private static async Task<IReadOnlyList<PlaceSuggestion>> SearchNominatimAsync(
        string keyword, int limit, CancellationToken token)
    {
        var anchor = await CurrentAnchor.Value.ConfigureAwait(false);

        List<PlaceHit> merged;
        if (anchor is not null)
        {
            // 并行查「当前城市内」和「全局」：同城结果按距当前位置由近到远排序，再补跨城结果。
            var localTask = QueryAsync(
                PlaceParsers.NominatimUrl(keyword, limit, anchor.Viewbox), PlaceParsers.ParseNominatim, token);
            var globalTask = QueryAsync(
                PlaceParsers.NominatimUrl(keyword, limit), PlaceParsers.ParseNominatim, token);
            await Task.WhenAll(localTask, globalTask).ConfigureAwait(false);

            var orderedLocal = localTask.Result
                .OrderBy(h => DistanceSq(anchor.Lat, anchor.Lon, h.Lat, h.Lon));

            merged = new List<PlaceHit>(limit);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hit in orderedLocal.Concat(globalTask.Result))
            {
                if (seen.Add(hit.Suggestion.DisplayName))
                {
                    merged.Add(hit);
                    if (merged.Count >= limit)
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            merged = await QueryAsync(
                PlaceParsers.NominatimUrl(keyword, limit), PlaceParsers.ParseNominatim, token).ConfigureAwait(false);
        }

        if (merged.Count > 0)
        {
            return merged.Select(x => x.Suggestion).ToList();
        }

        // Nominatim 要求关键词全部命中同一条记录的地址树，像「温州医科大学 同仁楼」这类
        // 「大地点 + 内部楼栋」会返回 0 条。拆成锚点 + 目标，用锚点附近 viewbox 兜底查一次。
        var parts = keyword.Split(
            new[] { ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var anchorQuery = string.Join(' ', parts[..^1]);
            var targetQuery = parts[^1];
            var anchors = await QueryAsync(
                PlaceParsers.NominatimUrl(anchorQuery, 1), PlaceParsers.ParseNominatim, token).ConfigureAwait(false);
            if (anchors.Count > 0 && anchors[0].Lat is double lat && anchors[0].Lon is double lon)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var viewbox = string.Format(inv, "{0},{1},{2},{3}", lon - 0.05, lat + 0.05, lon + 0.05, lat - 0.05);
                var bounded = await QueryAsync(
                    PlaceParsers.NominatimUrl(targetQuery, limit, viewbox), PlaceParsers.ParseNominatim, token).ConfigureAwait(false);
                if (bounded.Count > 0)
                {
                    return bounded.Select(x => x.Suggestion).ToList();
                }
            }
        }

        return Array.Empty<PlaceSuggestion>();
    }

    private static async Task<IReadOnlyList<PlaceSuggestion>> SearchPhotonAsync(
        string keyword, int limit, CancellationToken token)
    {
        var anchor = await CurrentAnchor.Value.ConfigureAwait(false);
        var hits = await QueryAsync(
            PlaceParsers.PhotonUrl(keyword, limit, anchor?.Lat, anchor?.Lon),
            PlaceParsers.ParsePhoton,
            token).ConfigureAwait(false);
        return hits.Select(h => h.Suggestion).ToList();
    }

    private static async Task<IReadOnlyList<PlaceSuggestion>> SearchAmapAsync(
        string keyword, int limit, CancellationToken token)
    {
        if (_amapKey.Length == 0)
        {
            return Array.Empty<PlaceSuggestion>();
        }

        var (hits, _) = await QueryAmapAsync(
            PlaceParsers.AmapUrl(keyword, limit, _amapKey), token).ConfigureAwait(false);
        return hits.Select(h => h.Suggestion).ToList();
    }

    // ---- HTTP ---------------------------------------------------------------

    private static async Task<List<PlaceHit>> QueryAsync(
        string url, Func<string, List<PlaceHit>> parse, CancellationToken token)
    {
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new List<PlaceHit>();
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return parse(json);
    }

    private static async Task<(List<PlaceHit> Hits, string? Error)> QueryAmapAsync(
        string url, CancellationToken token)
    {
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (new List<PlaceHit>(), $"HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return PlaceParsers.ParseAmap(json);
    }

    // ---- 当前位置（用于把同城结果排前面） -----------------------------------

    /// <summary>
    /// 识别当前所在位置（优先 Windows 系统定位精确到区县/街道，不可用时回退国内 IP 城市定位），
    /// 并换算成排序用的锚点缓存起来。失败返回 null，此时按服务端默认顺序展示。
    /// </summary>
    private static async Task<LocalAnchor?> ResolveLocalAnchorAsync()
    {
        var winPos = await TryGetWindowsLocationAsync().ConfigureAwait(false);
        if (winPos is (double wLat, double wLon))
        {
            return BuildAnchor(wLat, wLon);
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var response = await Http.GetAsync(DomesticIpCityEndpoint, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            var json = DecodePconlineJson(bytes);
            using var doc = JsonDocument.Parse(json);

            var city = doc.RootElement.TryGetProperty("city", out var cityEl) ? cityEl.GetString()?.Trim() : null;
            var pro = doc.RootElement.TryGetProperty("pro", out var proEl) ? proEl.GetString()?.Trim() : null;
            var cityQuery = !string.IsNullOrEmpty(city) ? city : pro;
            if (string.IsNullOrEmpty(cityQuery))
            {
                return null;
            }

            var hits = await QueryAsync(
                PlaceParsers.NominatimUrl(cityQuery, 1), PlaceParsers.ParseNominatim, cts.Token).ConfigureAwait(false);
            if (hits.Count == 0 || hits[0].Lat is not double lat || hits[0].Lon is not double lon)
            {
                return null;
            }

            return BuildAnchor(lat, lon);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(double Lat, double Lon)?> TryGetWindowsLocationAsync()
    {
#if WINDOWS
        try
        {
            var locator = new Windows.Devices.Geolocation.Geolocator
            {
                DesiredAccuracyInMeters = 500,
            };
            var op = locator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(15),
                timeout: TimeSpan.FromSeconds(1.5));
            var pos = await op.AsTask().ConfigureAwait(false);
            var coord = pos?.Coordinate?.Point?.Position;
            if (coord is { } p)
            {
                return (p.Latitude, p.Longitude);
            }
        }
        catch
        {
            // 未授予位置权限或超时，静默回退 IP 定位。
        }
#endif
        await Task.CompletedTask;
        return null;
    }

    private static LocalAnchor BuildAnchor(double lat, double lon)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var viewbox = string.Format(inv, "{0},{1},{2},{3}", lon - 0.40, lat + 0.40, lon + 0.40, lat - 0.40);
        return new LocalAnchor(lat, lon, viewbox);
    }

    private static double DistanceSq(double lat1, double lon1, double? lat2, double? lon2)
    {
        if (lat2 is not double la || lon2 is not double lo)
        {
            return double.MaxValue;
        }

        var dLat = la - lat1;
        var dLon = (lo - lon1) * Math.Cos(lat1 * Math.PI / 180.0);
        return dLat * dLat + dLon * dLon;
    }

    private static string DecodePconlineJson(byte[] bytes)
    {
        try
        {
            return Encoding.GetEncoding("GBK").GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    // ---- 最近地点（离线可用） ----------------------------------------------

    private static readonly object RecentLock = new();
    private static List<string>? _recentCache;
    private const int MaxRecentPlaces = 30;

    private static string RecentPlacesPath
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("TODOOO_DATA_DIR");
            var root = !string.IsNullOrWhiteSpace(custom)
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToDooo");
            return Path.Combine(root, "recent-places.json");
        }
    }

    /// <summary>记录一条用过的地点，供离线降级与在线优先展示。</summary>
    public static void RecordRecent(string? displayName)
    {
        var text = (displayName ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        lock (RecentLock)
        {
            var list = LoadRecentUnlocked();
            list.RemoveAll(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, text);
            if (list.Count > MaxRecentPlaces)
            {
                list.RemoveRange(MaxRecentPlaces, list.Count - MaxRecentPlaces);
            }

            try
            {
                var path = RecentPlacesPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(list));
            }
            catch
            {
                // 写盘失败不影响日程保存
            }
        }
    }

    /// <summary>本地瞬时检索最近地点（纯离线可用；关键词为空时返回最近的几条）。</summary>
    public static IReadOnlyList<PlaceSuggestion> GetRecent(string? query = null, int limit = 5)
    {
        var keyword = (query ?? string.Empty).Trim();
        lock (RecentLock)
        {
            var list = LoadRecentUnlocked();
            IEnumerable<string> matched = keyword.Length == 0
                ? list
                : list.Where(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            return matched
                .Take(Math.Clamp(limit, 1, MaxRecentPlaces))
                .Select(d => new PlaceSuggestion(PlaceParsers.FirstPart(d), d, IsRecent: true))
                .ToList();
        }
    }

    private static List<string> LoadRecentUnlocked()
    {
        if (_recentCache is not null)
        {
            return _recentCache;
        }

        try
        {
            var path = RecentPlacesPath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    _recentCache = loaded.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                    return _recentCache;
                }
            }
        }
        catch
        {
            // 文件损坏时退回空列表
        }

        _recentCache = new List<string>();
        return _recentCache;
    }
}
