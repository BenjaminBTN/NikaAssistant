using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NikaAssistant.Presentation;

/// <summary>
/// Всплывающее окно «Ваш ассистент запущен» после старта сервера:
/// Windows — MessageBox, Linux — zenity/notify-send, macOS — диалог через osascript.
/// Не блокирует старт: вызывается из ApplicationStarted и работает в фоне.
/// </summary>
public static class StartupNotifier
{
    public const string OptOutArg = "--no-popup";
    private const string Title = "NikaAssistant";
    private const string Message = "Ваш ассистент запущен";

    public static void ShowStarted(string[] args, IEnumerable<string> urls, IConfiguration configuration)
    {
        try
        {
            if (args.Any(a => string.Equals(a, OptOutArg, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (configuration.GetValue<bool?>("StartupPopup:Enabled") is false)
            {
                return;
            }

            // В Development (dotnet run) окно не показываем, чтобы не мешать отладке.
            // Publish exe показывает всегда.
            if (Autostart.IsDevelopmentBuild(configuration))
            {
                return;
            }

            var text = BuildText(urls);
            Task.Run(() => ShowPlatformPopup(text));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupPopup] {ex.Message}");
        }
    }

    public static string BuildText(IEnumerable<string> urls)
    {
        var url = urls.FirstOrDefault(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(url) ? Message : $"{Message}: {url}";
    }

    private static void ShowPlatformPopup(string text)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ShowWindows(text);
            }
            else if (OperatingSystem.IsLinux())
            {
                ShowLinux(text);
            }
            else if (OperatingSystem.IsMacOS())
            {
                ShowMac(text);
            }
            else
            {
                Console.WriteLine($"[{Title}] {text}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupPopup] {ex.Message}");
        }
    }

    // --- Windows ---

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_TOPMOST = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Проверка совместимости платформы", Justification = "Вызывается только после OperatingSystem.IsWindows().")]
    private static void ShowWindows(string text) =>
        MessageBoxW(IntPtr.Zero, text, Title, MB_OK | MB_ICONINFORMATION | MB_TOPMOST);

    // --- Linux ---

    private static void ShowLinux(string text)
    {
        if (CommandExists("zenity"))
        {
            Run("zenity", ["--info", $"--title={Title}", $"--text={text}", "--no-wrap"]);
        }
        else if (CommandExists("notify-send"))
        {
            Run("notify-send", [Title, text]);
        }
        else
        {
            Console.WriteLine($"[{Title}] {text}");
        }
    }

    // --- macOS ---

    private static void ShowMac(string text) =>
        Run("osascript", ["-e", $"display dialog \"{EscapeAppleScript(text)}\" with title \"{Title}\" buttons {{\"OK\"}} default button \"OK\""]);

    public static string EscapeAppleScript(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Any(dir =>
        {
            try
            {
                return !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, command));
            }
            catch
            {
                return false;
            }
        });
    }

    private static void Run(string fileName, string[] argv)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in argv)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        process?.WaitForExit();
    }
}
