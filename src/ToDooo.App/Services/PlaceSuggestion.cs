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

/// <summary>一条原始命中：展示用候选 + 经纬度（部分源不返回坐标）。</summary>
public sealed record PlaceHit(PlaceSuggestion Suggestion, double? Lat, double? Lon);
