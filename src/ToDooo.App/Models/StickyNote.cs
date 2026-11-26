using Windows.UI;

namespace ToDooo.Models;

/// <summary>单张便签的元数据与正文（正文存独立 .md 文件，元数据存 meta.json）。</summary>
public sealed class StickyNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string FileName { get; set; } = string.Empty;

    public string Color { get; set; } = "yellow";

    public bool Pinned { get; set; }

    public bool Visible { get; set; } = true;

    public int? X { get; set; }

    public int? Y { get; set; }

    public int Width { get; set; } = 310;

    public int Height { get; set; } = 330;

    public double FontSize { get; set; } = 14.0;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>正文内容（运行时加载自 .md 文件，不写入 meta.json）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Content { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Title
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Content))
            {
                return "未命名便签";
            }

            foreach (var raw in Content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = raw.Trim().TrimStart('#', '-', '*', ' ', '\t');
                if (line.Length > 0)
                {
                    return line.Length > 36 ? line[..36] + "…" : line;
                }
            }

            return "未命名便签";
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Preview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Content))
            {
                return "空白便签";
            }

            var lines = Content
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            if (lines.Count <= 1)
            {
                return string.Empty;
            }

            var joined = string.Join(" · ", lines.Skip(1).Take(2));
            return joined.Length > 72 ? joined[..72] + "…" : joined;
        }
    }
}

/// <summary>符合 WinUI 3 Fluent 审美的 6 色便签色板（明/暗双套配色）。</summary>
public sealed record StickyColorTheme(
    string Key,
    string Name,
    Color Swatch,
    Color LightSurface,
    Color LightHeader,
    Color LightBorder,
    Color DarkSurface,
    Color DarkHeader,
    Color DarkBorder)
{
    public static readonly StickyColorTheme[] All =
    {
        new("yellow", "琥珀黄",
            Color.FromArgb(255, 0xF5, 0xC5, 0x42),
            Color.FromArgb(245, 0xFF, 0xFA, 0xE6), Color.FromArgb(255, 0xF7, 0xDC, 0x7A), Color.FromArgb(140, 0xD9, 0xB8, 0x5B),
            Color.FromArgb(242, 0x2B, 0x26, 0x1A), Color.FromArgb(255, 0xD9, 0xA8, 0x36), Color.FromArgb(120, 0x8F, 0x73, 0x2A)),

        new("green", "薄荷绿",
            Color.FromArgb(255, 0x4E, 0xB8, 0x7A),
            Color.FromArgb(245, 0xEC, 0xFA, 0xF1), Color.FromArgb(255, 0x98, 0xDC, 0xB4), Color.FromArgb(140, 0x6B, 0xBA, 0x8C),
            Color.FromArgb(242, 0x1D, 0x29, 0x22), Color.FromArgb(255, 0x4E, 0xB8, 0x7A), Color.FromArgb(120, 0x39, 0x7D, 0x56)),

        new("blue", "晴空蓝",
            Color.FromArgb(255, 0x4E, 0x94, 0xE6),
            Color.FromArgb(245, 0xEC, 0xF5, 0xFF), Color.FromArgb(255, 0x9B, 0xC6, 0xF7), Color.FromArgb(140, 0x6E, 0xA4, 0xE0),
            Color.FromArgb(242, 0x1C, 0x25, 0x31), Color.FromArgb(255, 0x4E, 0x94, 0xE6), Color.FromArgb(120, 0x3B, 0x6A, 0x9E)),

        new("pink", "樱花粉",
            Color.FromArgb(255, 0xE8, 0x7A, 0x9B),
            Color.FromArgb(245, 0xFF, 0xF0, 0xF5), Color.FromArgb(255, 0xF6, 0xB3, 0xC7), Color.FromArgb(140, 0xDC, 0x88, 0xA3),
            Color.FromArgb(242, 0x2E, 0x1F, 0x26), Color.FromArgb(255, 0xE2, 0x6E, 0x90), Color.FromArgb(120, 0x94, 0x48, 0x60)),

        new("purple", "薰衣草",
            Color.FromArgb(255, 0x9B, 0x7E, 0xE8),
            Color.FromArgb(245, 0xF5, 0xF1, 0xFF), Color.FromArgb(255, 0xC7, 0xB6, 0xF7), Color.FromArgb(140, 0x9F, 0x88, 0xDC),
            Color.FromArgb(242, 0x25, 0x20, 0x31), Color.FromArgb(255, 0x9B, 0x7E, 0xE8), Color.FromArgb(120, 0x68, 0x54, 0x9E)),

        new("graphite", "极简灰",
            Color.FromArgb(255, 0x8E, 0x96, 0xA2),
            Color.FromArgb(245, 0xF6, 0xF7, 0xF9), Color.FromArgb(255, 0xD4, 0xD8, 0xDF), Color.FromArgb(140, 0xB0, 0xB6, 0xC0),
            Color.FromArgb(242, 0x24, 0x26, 0x2A), Color.FromArgb(255, 0x7C, 0x84, 0x90), Color.FromArgb(120, 0x58, 0x5E, 0x68)),
    };

    public static StickyColorTheme Resolve(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
