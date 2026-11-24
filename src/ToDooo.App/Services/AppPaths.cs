using System.IO;

namespace ToDooo.Services;

public static class AppPaths
{
    /// <summary>
    /// 数据目录。默认 %LocalAppData%\ToDooo；
    /// 可用环境变量 TODOOO_DATA_DIR 指向别处，用于截图或并行调试实例。
    /// </summary>
    public static string Root { get; } = ResolveRoot();

    public static string DatabasePath => Path.Combine(Root, "todo.db");

    public static string BackupDir => Path.Combine(Root, "backups");

    public static string SettingsPath => Path.Combine(Root, "settings.json");

    public static string LogPath => Path.Combine(Root, "error.log");

    /// <summary>单实例互斥名；换数据目录时会跟着换，两份实例可以同时跑。</summary>
    public static string InstanceKey { get; } = $"ToDooo-{Hash(Root)}";

    public static void Ensure() => Directory.CreateDirectory(Root);

    private static string ResolveRoot()
    {
        var custom = Environment.GetEnvironmentVariable("TODOOO_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                return Path.GetFullPath(custom);
            }
            catch
            {
                // 路径非法就退回默认目录，不让启动失败
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToDooo");
    }

    /// <summary>目录路径取短哈希做互斥名，避免路径里的分隔符进 key。</summary>
    private static string Hash(string value)
    {
        var sum = 17u;
        foreach (var c in value)
        {
            sum = sum * 31 + c;
        }

        return sum.ToString("X8");
    }
}
