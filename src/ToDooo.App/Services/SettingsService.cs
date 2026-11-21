using System.IO;
using System.Text.Json;
using ToDooo.Models;

namespace ToDooo.Services;

public enum ThemeMode
{
    System = 0,
    Dark = 1,
    Light = 2,
}

public sealed class SmartViewSetting
{
    public int View { get; set; }

    public bool Enabled { get; set; } = true;

    public static List<SmartViewSetting> CreateDefault() => SmartViewInfo.All
        .Select(view => new SmartViewSetting { View = (int)view, Enabled = true })
        .ToList();
}

public sealed class AppSettings
{
    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public int WindowWidth { get; set; } = 440;

    public int WindowHeight { get; set; } = 716;

    public int CalendarWidth { get; set; } = 860;

    public int CalendarHeight { get; set; } = 560;

    public int CalendarViewMode { get; set; }

    public bool CalendarHideCompleted { get; set; }

    public int ThemeMode { get; set; }

    /// <summary>启动时默认选中的筛选标签：0=全部，1=进行中，2=重要，3=已完成。</summary>
    public int DefaultTab { get; set; }

    /// <summary>窗口不透明度（设置页显示为“透明度”）：0.1–1.0，默认 0.8。</summary>
    public double WindowOpacity { get; set; } = 0.8;

    /// <summary>提前多少分钟提醒：0 = 到期时提醒。</summary>
    public int ReminderLeadMinutes { get; set; }

    /// <summary>鼠标离开窗口时自动变淡、移回恢复。</summary>
    public bool AutoHide { get; set; }

    /// <summary>全局呼出快捷键：0=关闭，1=Ctrl+Alt+T，2=Ctrl+Shift+T，3=Alt+Q。</summary>
    public int GlobalHotkeyMode { get; set; } = 1;

    /// <summary>快速记一条快捷键：0=关闭，1=Ctrl+Alt+N，2=Ctrl+Shift+N，3=Alt+N。</summary>
    public int QuickAddHotkeyMode { get; set; }

    /// <summary>8 种颜色标签的自定义名称。</summary>
    public List<string> TagNames { get; set; } = new();

    /// <summary>智能视图的启用状态与显示顺序（列表顺序即显示顺序）。</summary>
    public List<SmartViewSetting> SmartViews { get; set; } = SmartViewSetting.CreateDefault();
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private AppSettings _settings;

    public SettingsService(string? path = null)
    {
        _path = path ?? AppPaths.SettingsPath;
        _settings = Load();
    }

    public AppSettings Current => _settings;

    public string GetCustomTagName(int index)
    {
        if (index >= 0 && _settings.TagNames is { } names && index < names.Count && !string.IsNullOrWhiteSpace(names[index]))
        {
            return names[index].Trim();
        }

        return string.Empty;
    }

    public string GetTagName(int index)
    {
        if (index < 0 || index >= 8)
        {
            return string.Empty;
        }

        var custom = GetCustomTagName(index);
        return custom.Length > 0 ? custom : $"标签 {index + 1}";
    }

    public void Save()
    {
        try
        {
            AppPaths.Ensure();
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // 设置写入失败不影响使用
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }

        return new AppSettings();
    }
}
