using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ToDooo.Services;

/// <summary>一条地点候选：展示用名称 + 完整地址。</summary>
public sealed record PlaceSuggestion(string Name, string DisplayName, bool IsRecent = false)
{
    /// <summary>最近地点用时钟图标，在线检索结果用定位大头针。</summary>
    public string Glyph => IsRecent ? "\uE823" : "\uE707";

    /// <summary>去掉首段名称后的详细地址，供候选列表第二行展示，避免与 Name 重复。</summary>
    public string Subtitle
    {
        get
        {
            var comma = DisplayName.IndexOfAny(new[] { ',', '，' });
            return comma > 0 && comma + 1 < DisplayName.Length
                ? DisplayName[(comma + 1)..].Trim()
                : DisplayName;
        }
    }
}

/// <summary>
/// 地点搜索：走 OpenStreetMap 的 Nominatim。免费、无需密钥，国内可直连。
/// 只取地名与地址文本，不上传任何日程内容。
/// </summary>
public static class PlaceSearchService
{
    private const string Endpoint = "https://nominatim.openstreetmap.org/search";
    private const string DomesticIpCityEndpoint = "https://whois.pconline.com.cn/ipJson.jsp?json=true";

    private sealed record LocalAnchor(double Lat, double Lon, string Viewbox);

    private static readonly HttpClient Http = CreateClient();
    private static readonly Lazy<Task<LocalAnchor?>> CurrentAnchor = new(ResolveLocalAnchorAsync);

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ToDooo/1.8 (calendar app; contact via github.com/yeyt97/ToDooo)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>
    /// 识别当前所在位置（优先 Windows 系统定位精确到区县/街道，不可用时回退国内 IP 城市定位），
    /// 并换算为 Nominatim 的 viewbox 缓存起来，用于「从当前位置向外扩散」排序。
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

            var hits = await QueryAsync(cityQuery, 1, viewbox: null, cts.Token).ConfigureAwait(false);
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

    /// <summary>
    /// 按关键词搜地点，返回最多 <paramref name="limit"/> 条候选（从当前位置由近到远优先排序）。
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
            var clampedLimit = Math.Clamp(limit, 1, 10);
            var anchor = await CurrentAnchor.Value.ConfigureAwait(false);

            List<RawHit> merged;
            if (anchor is not null)
            {
                // 并行查「当前城市内」和「全局」：同城结果按距当前位置由近到远排序，再补跨城结果。
                var localTask = QueryAsync(keyword, clampedLimit, anchor.Viewbox, token);
                var globalTask = QueryAsync(keyword, clampedLimit, viewbox: null, token);
                await Task.WhenAll(localTask, globalTask).ConfigureAwait(false);

                var orderedLocal = localTask.Result
                    .OrderBy(h => DistanceSq(anchor.Lat, anchor.Lon, h.Lat, h.Lon));

                merged = new List<RawHit>(clampedLimit);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var hit in orderedLocal.Concat(globalTask.Result))
                {
                    if (seen.Add(hit.Suggestion.DisplayName))
                    {
                        merged.Add(hit);
                        if (merged.Count >= clampedLimit)
                        {
                            break;
                        }
                    }
                }
            }
            else
            {
                merged = await QueryAsync(keyword, clampedLimit, viewbox: null, token).ConfigureAwait(false);
            }

            if (merged.Count > 0)
            {
                return merged.Select(x => x.Suggestion).ToList();
            }

            // Nominatim 要求关键词全部命中同一条记录的地址树，像「温州医科大学 同仁楼」这类
            // 「大地点 + 内部楼栋」会返回 0 条。拆成锚点 + 目标，用锚点附近 viewbox 兜底查一次。
            var parts = keyword.Split(new[] { ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                var anchorQuery = string.Join(' ', parts[..^1]);
                var targetQuery = parts[^1];
                var anchors = await QueryAsync(anchorQuery, 1, viewbox: null, token).ConfigureAwait(false);
                if (anchors.Count > 0 && anchors[0].Lat is double lat && anchors[0].Lon is double lon)
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var viewbox = string.Format(inv, "{0},{1},{2},{3}", lon - 0.05, lat + 0.05, lon + 0.05, lat - 0.05);
                    var bounded = await QueryAsync(targetQuery, clampedLimit, viewbox, token).ConfigureAwait(false);
                    if (bounded.Count > 0)
                    {
                        return bounded.Select(x => x.Suggestion).ToList();
                    }
                }
            }

            return Array.Empty<PlaceSuggestion>();
        }
        catch
        {
            return Array.Empty<PlaceSuggestion>();
        }
    }

    private sealed record RawHit(PlaceSuggestion Suggestion, double? Lat, double? Lon);

    private static async Task<List<RawHit>> QueryAsync(
        string keyword, int limit, string? viewbox, CancellationToken token)
    {
        var url = $"{Endpoint}?q={Uri.EscapeDataString(keyword)}" +
            $"&format=json&limit={limit}&addressdetails=1&accept-language=zh-CN,zh";
        if (!string.IsNullOrEmpty(viewbox))
        {
            url += $"&viewbox={viewbox}&bounded=1";
        }

        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new List<RawHit>();
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<RawHit>();
        }

        var results = new List<RawHit>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var display = item.TryGetProperty("display_name", out var displayElement)
                ? displayElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(display))
            {
                continue;
            }

            double? lat = item.TryGetProperty("lat", out var latEl)
                && double.TryParse(latEl.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsedLat)
                ? parsedLat
                : null;
            double? lon = item.TryGetProperty("lon", out var lonEl)
                && double.TryParse(lonEl.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsedLon)
                ? parsedLon
                : null;

            // 名称取地址的第一段（通常是地点本身），完整地址留给第二行。
            var name = FirstPart(display);
            results.Add(new RawHit(new PlaceSuggestion(name, display.Trim()), lat, lon));
        }

        return results;
    }

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
                .Select(d => new PlaceSuggestion(FirstPart(d), d, IsRecent: true))
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

    private static string FirstPart(string display)
    {
        var comma = display.IndexOfAny(new[] { ',', '，' });
        return comma > 0 ? display[..comma].Trim() : display.Trim();
    }
}
