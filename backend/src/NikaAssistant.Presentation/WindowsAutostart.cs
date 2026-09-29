using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace NikaAssistant.Presentation;

/// <summary>
/// Автозапуск Windows через реестр HKCU\...\Run.
/// Общая логика (флаги, конфиг, Development) — в <see cref="Autostart"/>.
/// </summary>
public static class WindowsAutostart
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static void EnsureRegistered(IConfiguration configuration, string[] args, string appName = "NikaAssistant") =>
        Autostart.EnsureRegistered(configuration, args, appName);

    internal static void EnsureForTarget(string appName, LaunchTarget target)
    {
        var command = Autostart.ToCommandLine(target.Executable, target.Arguments);
        if (IsAlreadyRegistered(appName, command))
        {
            return;
        }

        Register(appName, command);
    }

    private static bool IsAlreadyRegistered(string appName, string expectedCommand)
    {
        return IsAlreadyRegisteredCore(appName, expectedCommand);
    }

    [SuppressMessage("Interoperability", "CA1416:Проверка совместимости платформы", Justification = "Вызывается только после OperatingSystem.IsWindows().")]
    private static bool IsAlreadyRegisteredCore(string appName, string expectedCommand)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var current = key?.GetValue(appName) as string;
        return string.Equals(current?.Trim(), expectedCommand.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [SuppressMessage("Interoperability", "CA1416:Проверка совместимости платформы", Justification = "Вызывается только после OperatingSystem.IsWindows().")]
    private static void Register(string appName, string command)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("Не удалось открыть ключ реестра автозапуска.");
        }

        key.SetValue(appName, command, RegistryValueKind.String);
        Console.WriteLine($"[Autostart] Добавлено в автозапуск Windows: {appName} -> {command}");
    }
}
