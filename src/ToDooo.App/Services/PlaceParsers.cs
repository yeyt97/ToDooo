using System.Globalization;
using System.Text.Json;

namespace ToDooo.Services;

/// <summary>
/// 地点接口里跟网络无关的那半截：拼请求 URL、解析各家返回的 JSON。
/// 单独拎出来是为了能在单元测试里跑（不依赖 HttpClient 与 WinRT）。
/// </summary>
public static class PlaceParsers
{
    public const string BaiduEndpoint = "https://map.baidu.com/su";
    public const string NominatimEndpoint = "https://nominatim.openstreetmap.org/search";
    public const string PhotonEndpoint = "https://photon.komoot.io/api/";
    public const string AmapEndpoint = "https://restapi.amap.com/v3/place/text";

    /// <summary>各家都支持按条数限制；统一夹一下，避免被服务端拒绝。</summary>
    public static int ClampLimit(int limit) => Math.Clamp(limit, 1, 10);

    // ---- 百度地图（国内直连，免 Key） ----------------------------------------

    public static string BaiduUrl(string keyword) =>
        $"{BaiduEndpoint}?wd={Uri.EscapeDataString(keyword)}&cid=1&type=0";

    /// <summary>
    /// 百度地图联想返回 {"s":["城市$区县$$地点名$城市码$", ...]}。
    /// </summary>
    public static List<PlaceHit> ParseBaidu(string json, int limit = 6)
    {
        var results = new List<PlaceHit>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("s", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        var max = ClampLimit(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parts = raw.Split('$');
            if (parts.Length < 4)
            {
                continue;
            }

            var name = parts[3].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var area = $"{parts[0].Trim()}{parts[1].Trim()}";
            var display = area.Length > 0 ? $"{name},{area}" : name;
            if (!seen.Add(display))
            {
                continue;
            }

            results.Add(new PlaceHit(new PlaceSuggestion(name, display), null, null));
            if (results.Count >= max)
            {
                break;
            }
        }

        return results;
    }

    // ---- OpenStreetMap / Nominatim ------------------------------------------

    public static string NominatimUrl(string keyword, int limit, string? viewbox = null)
    {
        var url = $"{NominatimEndpoint}?q={Uri.EscapeDataString(keyword)}" +
            $"&format=json&limit={ClampLimit(limit)}&addressdetails=1&accept-language=zh-CN,zh";
        if (!string.IsNullOrEmpty(viewbox))
        {
            url += $"&viewbox={viewbox}&bounded=1";
        }

        return url;
    }

    /// <summary>Nominatim 返回一个对象数组，坐标是字符串。</summary>
    public static List<PlaceHit> ParseNominatim(string json)
    {
        var results = new List<PlaceHit>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var display = item.TryGetProperty("display_name", out var displayElement)
                ? displayElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(display))
            {
                continue;
            }

            var lat = ParseDouble(item, "lat");
            var lon = ParseDouble(item, "lon");
            results.Add(new PlaceHit(new PlaceSuggestion(FirstPart(display), display.Trim()), lat, lon));
        }

        return results;
    }

    // ---- Photon（OSM 数据，GeoJSON 返回） ------------------------------------

    public static string PhotonUrl(string keyword, int limit, double? lat = null, double? lon = null)
    {
        var url = $"{PhotonEndpoint}?q={Uri.EscapeDataString(keyword)}" +
            $"&limit={ClampLimit(limit)}&lang=zh";
        if (lat is double la && lon is double lo)
        {
            url += string.Format(CultureInfo.InvariantCulture, "&lat={0:0.####}&lon={1:0.####}", la, lo);
        }

        return url;
    }

    /// <summary>Photon 是 GeoJSON：features[].properties 放名称，geometry.coordinates 是 [lon, lat]。</summary>
    public static List<PlaceHit> ParsePhoton(string json)
    {
        var results = new List<PlaceHit>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var parts = new List<string>(6);
            foreach (var key in new[] { "name", "street", "district", "city", "county", "state", "country" })
            {
                var value = props.TryGetProperty(key, out var el) ? el.GetString()?.Trim() : null;
                if (!string.IsNullOrWhiteSpace(value) && !parts.Contains(value))
                {
                    parts.Add(value!);
                }
            }

            if (parts.Count == 0)
            {
                continue;
            }

            double? lat = null;
            double? lon = null;
            if (feature.TryGetProperty("geometry", out var geometry) &&
                geometry.TryGetProperty("coordinates", out var coordinates) &&
                coordinates.ValueKind == JsonValueKind.Array &&
                coordinates.GetArrayLength() >= 2)
            {
                lon = coordinates[0].TryGetDouble(out var lo) ? lo : null;
                lat = coordinates[1].TryGetDouble(out var la) ? la : null;
            }

            var display = string.Join(", ", parts);
            results.Add(new PlaceHit(new PlaceSuggestion(parts[0], display), lat, lon));
        }

        return results;
    }

    // ---- 高德（国内网络友好，需要免费 Key） ----------------------------------

    public static string AmapUrl(string keyword, int limit, string key, string? city = null)
    {
        var url = $"{AmapEndpoint}?keywords={Uri.EscapeDataString(keyword)}" +
            $"&key={Uri.EscapeDataString(key)}&offset={ClampLimit(limit)}&page=1&extensions=base";
        if (!string.IsNullOrWhiteSpace(city))
        {
            url += $"&city={Uri.EscapeDataString(city.Trim())}&citylimit=true";
        }

        return url;
    }

    /// <summary>
    /// 高德出错也是 HTTP 200，得看 status/info；返回 (命中, 错误说明)。
    /// </summary>
    public static (List<PlaceHit> Hits, string? Error) ParseAmap(string json)
    {
        var results = new List<PlaceHit>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (results, "返回格式不对");
        }

        var status = document.RootElement.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
        var info = document.RootElement.TryGetProperty("info", out var infoEl) ? infoEl.GetString() : null;
        if (!string.Equals(status, "1", StringComparison.Ordinal))
        {
            return (results, string.IsNullOrWhiteSpace(info) ? "高德拒绝了这次请求" : DescribeAmapError(info!));
        }

        if (!document.RootElement.TryGetProperty("pois", out var pois) || pois.ValueKind != JsonValueKind.Array)
        {
            return (results, null);
        }

        foreach (var poi in pois.EnumerateArray())
        {
            var name = GetString(poi, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var area = $"{GetString(poi, "cityname")}{GetString(poi, "adname")}".Trim();
            var address = GetString(poi, "address");
            var detail = address is { Length: > 0 } && !string.Equals(address, "[]", StringComparison.Ordinal)
                ? area.Length > 0 ? $"{area} {address}" : address
                : area;
            var display = detail.Length > 0 ? $"{name},{detail}" : name!;

            double? lat = null;
            double? lon = null;
            var location = GetString(poi, "location");
            if (!string.IsNullOrWhiteSpace(location))
            {
                var pieces = location.Split(',');
                if (pieces.Length == 2 &&
                    double.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) &&
                    double.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var la))
                {
                    lon = lo;
                    lat = la;
                }
            }

            results.Add(new PlaceHit(new PlaceSuggestion(name!, display), lat, lon));
        }

        return (results, null);
    }

    /// <summary>把高德常见的错误码翻成能照着做的中文。</summary>
    public static string DescribeAmapError(string info) => info switch
    {
        "INVALID_USER_KEY" or "INVALID_USER_SCODE" => "Key 无效，检查是否复制完整",
        "USER_KEY_RECYCLED" => "Key 已被回收，重新申请一个",
        "SERVICE_NOT_AVAILABLE" => "该 Key 没开通「Web 服务」权限",
        "DAILY_QUERY_OVER_LIMIT" => "今日调用量已用完",
        "INVALID_USER_IP" or "INVALID_USER_DOMAIN" => "Key 绑定了 IP/域名，与当前网络不符",
        _ => info,
    };

    // ---- 公共 ---------------------------------------------------------------

    /// <summary>名称取地址的第一段（通常是地点本身），完整地址留给第二行。</summary>
    public static string FirstPart(string display)
    {
        var trimmed = (display ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var cut = trimmed.IndexOfAny(new[] { ',', '，' });
        return cut > 0 ? trimmed[..cut].Trim() : trimmed;
    }

    private static double? ParseDouble(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
        {
            return number;
        }

        return element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    }
}
