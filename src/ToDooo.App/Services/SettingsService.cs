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

    /// <summary>窗口宽度（物理像素）。0 表示从未保存过，启动时按默认逻辑尺寸换算。</summary>
    public int WindowWidth { get; set; }

    /// <summary>窗口高度（物理像素）。0 表示从未保存过，启动时按默认逻辑尺寸换算。</summary>
    public int WindowHeight { get; set; }

    /// <summary>待办编辑窗上次被用户拖到的尺寸（逻辑像素）；为 0 则用默认尺寸。</summary>
    public int TodoEditorWidth { get; set; }

    public int TodoEditorHeight { get; set; }

    public int CalendarWidth { get; set; } = 860;

    public int CalendarHeight { get; set; } = 560;

    /// <summary>上次退出时日历窗口是否处于打开状态。</summary>
    public bool CalendarOpen { get; set; }

    /// <summary>日历窗口上次的位置（屏幕像素）；为空则打开时摆到主窗口旁边。</summary>
    public int? CalendarX { get; set; }

    public int? CalendarY { get; set; }

    public int CalendarViewMode { get; set; }

    public bool CalendarHideCompleted { get; set; }

    /// <summary>日历里是否显示本地待办；关闭后只显示 iCloud 日程。</summary>
    public bool CalendarShowTodos { get; set; } = true;

    /// <summary>日历条目基准字号（8–16，默认 10）。</summary>
    public double CalendarFontSize { get; set; } = 10.0;

    public int ThemeMode { get; set; }

    /// <summary>启动时默认选中的筛选标签：0=全部，1=进行中，2=重要，3=已完成。</summary>
    public int DefaultTab { get; set; }

    /// <summary>窗口不透明度（设置页显示为“透明度”）：0.1–1.0，默认 0.8。</summary>
    public double WindowOpacity { get; set; } = 0.8;

    /// <summary>便签独立毛玻璃开关（与待办/日历分开设置）。</summary>
    public bool StickyAcrylicEnabled { get; set; } = true;

    /// <summary>便签独立不透明度（0.1–1.0，默认 0.3 即 70% 透明度，与待办/日历分开设置）。</summary>
    public double StickyOpacity { get; set; } = 0.3;

    /// <summary>新建便签的默认颜色 key（yellow / green / blue / pink / purple / graphite）。</summary>
    public string StickyDefaultColor { get; set; } = "yellow";

    /// <summary>新建便签的默认字号（11–20）。</summary>
    public double StickyDefaultFontSize { get; set; } = 14.0;

    /// <summary>新建便签是否默认置顶。</summary>
    public bool StickyDefaultPinned { get; set; }

    /// <summary>启动 ToDooo 时是否自动恢复上次打开的便签。</summary>
    public bool StickyRestoreOnStartup { get; set; } = true;

    /// <summary>便签拖到屏幕上边缘时是否自动贴边收起。</summary>
    public bool StickyAutoDockTop { get; set; } = true;

    /// <summary>窗口底色：见 Appearance.TintMode（0 跟随主题 / 1 深灰 / 2 藏青 / 3 暖褐）。</summary>
    public int WindowTintMode { get; set; }

    /// <summary>提前多少分钟提醒：0 = 到期时提醒。</summary>
    public int ReminderLeadMinutes { get; set; }

    /// <summary>鼠标离开窗口时自动变淡、移回恢复。</summary>
    public bool AutoHide { get; set; }

    /// <summary>上次检查更新的日期（yyyy-MM-dd）；用于每日只查一次。</summary>
    public string LastUpdateCheckDate { get; set; } = string.Empty;

    /// <summary>发现但用户还没处理的新版本号；不为空时下次启动会提示。</summary>
    public string PendingUpdateVersion { get; set; } = string.Empty;

    /// <summary>鼠标离开多少秒后变淡；0 = 立即变淡。</summary>
    public int AutoHideDelaySeconds { get; set; }

    /// <summary>
    /// 自定义底色（调色盘选的颜色），空串表示用预设方案。
    /// 存 #RRGGBB，深色窗口下会自动压暗、浅色下提亮。
    /// </summary>
    public string CustomTintColor { get; set; } = string.Empty;

    /// <summary>智能视图总开关：关闭后主窗口不显示这一栏，设置里也不展开选项。</summary>
    public bool SmartViewEnabled { get; set; } = true;

    /// <summary>颜色标签总开关：关闭后主窗口和编辑窗都不显示标签圆点/色条。</summary>
    public bool ColorTagsEnabled { get; set; } = true;

    /// <summary>全局呼出快捷键：0=关闭，1=Ctrl+Alt+T，2=Ctrl+Shift+T，3=Alt+Q。</summary>
    public int GlobalHotkeyMode { get; set; } = 1;

    /// <summary>快速记一条快捷键：0=关闭，1=Ctrl+Alt+N，2=Ctrl+Shift+N，3=Alt+N。</summary>
    public int QuickAddHotkeyMode { get; set; }

    /// <summary>已经提示过的新版本号，避免每次启动重复弹提示。</summary>
    public string LastNotifiedVersion { get; set; } = string.Empty;

    /// <summary>8 种颜色标签的自定义名称。</summary>
    public List<string> TagNames { get; set; } = new();

    /// <summary>智能视图的启用状态与显示顺序（列表顺序即显示顺序）。</summary>
    public List<SmartViewSetting> SmartViews { get; set; } = SmartViewSetting.CreateDefault();

    /// <summary>
    /// 是否启用 iCloud 日历同步。关闭即纯离线：只用本地日历数据，不发任何网络请求。
    /// </summary>
    public bool CalendarSyncEnabled { get; set; }

    /// <summary>日历同步用的 Apple ID；为空表示未配置。</summary>
    public string CalendarAppleId { get; set; } = string.Empty;

    /// <summary>应用专用密码的 DPAPI 密文，绝不存明文。</summary>
    public string CalendarPasswordCipher { get; set; } = string.Empty;

    /// <summary>后台自动刷新间隔（分钟）：0 = 只手动刷新。</summary>
    public int CalendarRefreshMinutes { get; set; } = 15;
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
