using System.Reflection;

namespace NikaAssistant.Presentation;

/// <summary>
/// Запуск, для которого настраивается автозапуск.
/// Executable — что запускать (exe/apphost либо хост dotnet),
/// Arguments — аргументы (для dotnet первым идёт путь к dll),
/// WorkingDirectory — каталог рядом с бинарником (там appsettings.json и Views).
/// </summary>
public sealed record LaunchTarget(string Executable, string[] Arguments, string WorkingDirectory);

/// <summary>
/// Кроссплатформенный автозапуск: Windows — реестр HKCU\Run,
/// Linux — XDG Autostart (*.desktop), macOS — LaunchAgent (plist).
/// Вызывается при старте, чтобы запуск из папки publish сразу добавлял приложение в автозапуск.
/// </summary>
public static class Autostart
{
    public const string OptOutArg = "--no-autostart";

    /// <summary>
    /// Маркер запуска из автозапуска. Всегда дописывается в хранимую запись,
    /// чтобы следующий старт (например, после перезагрузки) опознался как
    /// автоматический — тогда StartupNotifier не показывает окно.
    /// </summary>
    public const string FromAutostartArg = "--from-autostart";

    public static void EnsureRegistered(IConfiguration configuration, string[] args, string appName = "NikaAssistant")
    {
        try
        {
            appName = ResolveAppName(configuration, appName);

            if (IsOptedOut(args) || IsDisabled(configuration) || IsDevelopmentBuild(configuration))
            {
                return;
            }

            var target = ResolveLaunchTarget(args);
            if (target is null)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                WindowsAutostart.EnsureForTarget(appName, target);
            }
            else if (OperatingSystem.IsLinux())
            {
                LinuxAutostart.EnsureForTarget(appName, target);
            }
            else if (OperatingSystem.IsMacOS())
            {
                MacAutostart.EnsureForTarget(appName, target);
            }
        }
        catch (Exception ex)
        {
            // Автозапуск не должен ронять приложение (нет прав, заблокирован реестр и т.п.).
            Console.WriteLine($"[Autostart] Не удалось добавить в автозапуск: {ex.Message}");
        }
    }

    public static string ResolveAppName(IConfiguration configuration, string fallback)
    {
        var configured = configuration["Autostart:Name"];
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }

    public static bool IsOptedOut(string[] args) =>
        args.Any(a => string.Equals(a, OptOutArg, StringComparison.OrdinalIgnoreCase));

    public static bool IsDisabled(IConfiguration configuration) =>
        configuration.GetValue<bool?>("Autostart:Enabled") is false;

    /// <summary>
    /// В Development (dotnet run / отладка) автозапуск не трогаем,
    /// чтобы dev-сборки не прописывались вместо publish exe.
    /// Publish exe по умолчанию стартует в Production и будет зарегистрирован.
    /// </summary>
    public static bool IsDevelopmentBuild(IConfiguration configuration)
    {
        var environment = configuration["ASPNETCORE_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Лежит в папке publish — считаем боевым запуском даже при Development.
        return !IsInPublishFolder(Environment.ProcessPath);
    }

    public static bool IsInPublishFolder(string? exePath) =>
        exePath?.Replace('\\', '/').Contains("/publish/", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Аргументы, пробрасываемые в автозапуск (кроме флага отказа).
    /// </summary>
    public static string[] GetForwardedArgs(string[] args) =>
        args.Where(a => !string.Equals(a, OptOutArg, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// Аргументы для записи в автозапуск: проброшенные + маркер <see cref="FromAutostartArg"/>.
    /// </summary>
    public static string[] GetStoredArgs(string[] args)
    {
        var forwarded = GetForwardedArgs(args);
        if (forwarded.Any(a => string.Equals(a, FromAutostartArg, StringComparison.OrdinalIgnoreCase)))
        {
            return forwarded;
        }

        return [.. forwarded, FromAutostartArg];
    }

    public static string Quote(string value) =>
        value.Contains(' ') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    public static string ToCommandLine(string executable, string[] arguments)
    {
        var parts = new List<string>(arguments.Length + 1) { Quote(executable) };
        parts.AddRange(arguments.Select(Quote));
        return string.Join(' ', parts);
    }

    internal static LaunchTarget? ResolveLaunchTarget(string[] args)
    {
        // В хранимую запись всегда входит маркер --from-autostart.
        var forwarded = GetStoredArgs(args);
        var baseDir = AppContext.BaseDirectory;
        var processPath = Environment.ProcessPath;
        var entryLocation = GetEntryAssemblyLocation();

        // Обычный случай: запущен exe/apphost (в т.ч. self-contained и framework-dependent).
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath) && !IsDotnetHost(processPath))
        {
            return new LaunchTarget(processPath, forwarded, GetDirectory(processPath) ?? baseDir);
        }

        // Запуск через `dotnet app.dll`: в автозапуск пишем хост + путь к dll.
        if (!string.IsNullOrWhiteSpace(processPath) && IsDotnetHost(processPath) && File.Exists(processPath))
        {
            var dll = FirstExistingFile(entryLocation, Path.Combine(baseDir, "NikaAssistant.Presentation.dll"));
            if (dll is null)
            {
                return null;
            }

            return new LaunchTarget(processPath, new[] { dll }.Concat(forwarded).ToArray(), GetDirectory(dll) ?? baseDir);
        }

        // Запасной вариант: местоположение входной сборки.
        if (!string.IsNullOrWhiteSpace(entryLocation) && File.Exists(entryLocation))
        {
            return new LaunchTarget(entryLocation, forwarded, GetDirectory(entryLocation) ?? baseDir);
        }

        return null;
    }

    private static bool IsDotnetHost(string processPath) =>
        Path.GetFileNameWithoutExtension(processPath).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase);

    private static string? GetEntryAssemblyLocation()
    {
        try
        {
            return Assembly.GetEntryAssembly()?.Location;
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstExistingFile(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? GetDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }
}
