using System.IO;
using System.Text.Json;

namespace ToDooo.Services;

public enum ThemeMode
{
    System = 0,
    Dark = 1,
    Light = 2,
}

public sealed class AppSettings
{
    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public int WindowWidth { get; set; } = 440;

    public int WindowHeight { get; set; } = 716;

    public bool PinnedToDesktop { get; set; } = true;

    public int ThemeMode { get; set; }

    /// <summary>窗口不透明度（设置页显示为“透明度”）：0.1–1.0，默认 0.8。</summary>
    public double WindowOpacity { get; set; } = 0.8;

    /// <summary>提前多少分钟提醒：0 = 到期时提醒。</summary>
    public int ReminderLeadMinutes { get; set; }
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
