using System.IO;
using System.Text.Json;

namespace ToDooo.Services;

public sealed class AppSettings
{
    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public int WindowWidth { get; set; } = 440;

    public int WindowHeight { get; set; } = 716;

    public bool PinnedToDesktop { get; set; } = true;
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
