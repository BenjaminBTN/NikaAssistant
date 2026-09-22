using System.Security;

namespace NikaAssistant.Presentation;

/// <summary>
/// Автозапуск macOS через LaunchAgent: ~/Library/LaunchAgents/&lt;label&gt;.plist с RunAtLoad.
/// Файл подхватывается при следующем входе; загружать его в текущей сессии не нужно
/// (приложение уже запущено), поэтому launchctl не вызываем.
/// </summary>
public static class MacAutostart
{
    public const string DefaultLabel = "com.nikaassistant";

    internal static void EnsureForTarget(string appName, LaunchTarget target)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        EnsureInDirectory(GetAgentsDirectory(home), home, appName, target);
    }

    public static string GetAgentsDirectory(string? home = null)
    {
        var homeDir = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, "Library", "LaunchAgents");
    }

    public static string Slugify(string appName)
    {
        var slug = new string(appName.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray());
        return string.Join("-", slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string LabelFor(string appName)
    {
        var slug = Slugify(appName);
        return string.IsNullOrEmpty(slug) || slug == "nikaassistant" ? DefaultLabel : $"{DefaultLabel}.{slug}";
    }

    public static string PlistFileName(string label) => label + ".plist";

    public static string BuildPlistContent(string label, string appName, LaunchTarget target, string homeDir)
    {
        var programArguments = new List<string>(target.Arguments.Length + 1) { target.Executable };
        programArguments.AddRange(target.Arguments);
        var argsXml = string.Join('\n', programArguments.Select(a => $"\t\t<string>{SecurityElement.Escape(a)}</string>"));
        // Пути для plist всегда с '/', т.к. файл предназначен для macOS (на Windows здесь был бы '\').
        var logDir = $"{homeDir.TrimEnd('/')}/NikaAssistant/Logs";
        var stem = LinuxAutostart.SanitizeFileStem(appName);

        return string.Join('\n',
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
            "<plist version=\"1.0\">",
            "<dict>",
            "\t<key>Label</key>",
            $"\t<string>{SecurityElement.Escape(label)}</string>",
            "\t<key>ProgramArguments</key>",
            "\t<array>",
            argsXml,
            "\t</array>",
            "\t<key>WorkingDirectory</key>",
            $"\t<string>{SecurityElement.Escape(target.WorkingDirectory)}</string>",
            "\t<key>RunAtLoad</key>",
            "\t<true/>",
            "\t<key>KeepAlive</key>",
            "\t<false/>",
            "\t<key>StandardOutPath</key>",
            $"\t<string>{SecurityElement.Escape($"{logDir}/{stem}-stdout.log")}</string>",
            "\t<key>StandardErrorPath</key>",
            $"\t<string>{SecurityElement.Escape($"{logDir}/{stem}-stderr.log")}</string>",
            "</dict>",
            "</plist>",
            "");
    }

    /// <summary>
    /// Запись plist в указанный каталог. Проверки ОС нет — её делает <see cref="Autostart"/>.
    /// Публичный для тестов: можно вызвать с временным каталогом на любой ОС.
    /// </summary>
    public static void EnsureInDirectory(string agentsDir, string homeDir, string appName, LaunchTarget target)
    {
        Directory.CreateDirectory(agentsDir);
        var label = LabelFor(appName);
        var path = Path.Combine(agentsDir, PlistFileName(label));
        var content = BuildPlistContent(label, appName, target, homeDir);
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }

        File.WriteAllText(path, content);
        Console.WriteLine($"[Autostart] Добавлено в автозапуск macOS: {path} (вступит в силу при следующем входе)");
    }
}
