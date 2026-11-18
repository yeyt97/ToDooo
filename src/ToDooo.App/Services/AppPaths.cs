using System.IO;

namespace ToDooo.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToDooo");

    public static string DatabasePath => Path.Combine(Root, "todo.db");

    public static string SettingsPath => Path.Combine(Root, "settings.json");

    public static string LogPath => Path.Combine(Root, "error.log");

    public static void Ensure() => Directory.CreateDirectory(Root);
}
