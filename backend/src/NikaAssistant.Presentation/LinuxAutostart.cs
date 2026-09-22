namespace NikaAssistant.Presentation;

/// <summary>
/// Автозапуск Linux через XDG Autostart: файл ~/.config/autostart/&lt;App&gt;.desktop
/// (или $XDG_CONFIG_HOME/autostart). Работает в GNOME/KDE/Xfce без systemctl.
/// </summary>
public static class LinuxAutostart
{
    internal static void EnsureForTarget(string appName, LaunchTarget target) =>
        EnsureInDirectory(GetAutostartDirectory(), appName, target);

    public static string GetAutostartDirectory(string? configHome = null, string? home = null)
    {
        var xdg = configHome ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(xdg, "autostart");
        }

        var homeDir = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".config", "autostart");
    }

    public static string DesktopFileName(string appName) => SanitizeFileStem(appName) + ".desktop";

    public static string SanitizeFileStem(string appName)
    {
        var stem = new string(appName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' ? c : '-').ToArray())
            .Trim().Trim('-', '.', ' ');
        return string.IsNullOrWhiteSpace(stem) ? "NikaAssistant" : stem;
    }

    public static string BuildDesktopFileContent(string appName, LaunchTarget target)
    {
        var exec = Autostart.ToCommandLine(target.Executable, target.Arguments);
        return string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Version=1.0",
            $"Name={appName}",
            $"Comment={appName} (autostart)",
            $"Exec={exec}",
            $"Path={target.WorkingDirectory}",
            "Terminal=false",
            "Hidden=false",
            "NoDisplay=false",
            "X-GNOME-Autostart-enabled=true",
            "StartupNotify=false",
            "");
    }

    /// <summary>
    /// Запись .desktop в указанный каталог. Проверки ОС нет — её делает <see cref="Autostart"/>.
    /// Публичный для тестов: можно вызвать с временным каталогом на любой ОС.
    /// </summary>
    public static void EnsureInDirectory(string autostartDir, string appName, LaunchTarget target)
    {
        Directory.CreateDirectory(autostartDir);
        var path = Path.Combine(autostartDir, DesktopFileName(appName));
        var content = BuildDesktopFileContent(appName, target);
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }

        File.WriteAllText(path, content);
        Console.WriteLine($"[Autostart] Добавлено в автозапуск Linux: {path}");
    }
}
