using Microsoft.Win32;

namespace ToDooo.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ToDooo";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    /// <summary>已开启自启但路径指向别的 exe（换过安装目录）时，纠正成当前程序。</summary>
    public static void EnsureMatchesCurrentProcess()
    {
        var exe = Environment.ProcessPath;
        if (!IsEnabled || string.IsNullOrEmpty(exe))
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        var expected = $"\"{exe}\"";
        if (key?.GetValue(ValueName) is not string stored ||
            !string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase))
        {
            key?.SetValue(ValueName, expected);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                key.SetValue(ValueName, $"\"{exe}\"");
            }
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
