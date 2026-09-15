namespace NikaAssistant.Infrastructure.LLM.OpenRouter;

/// <summary>
/// Runtime-настройки OpenRouter. Биндятся из секции "OpenRouter"
/// (appsettings.json + appsettings.Local.json + env-переменные OpenRouter__*).
/// Через IOptionsMonitor изменения файла подхватываются без пересборки,
/// Model/FallbackModels — без рестарта, ApiKey — со следующего запроса.
/// </summary>
public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string? ApiKey { get; set; }
    public string Model { get; set; } = "openai/gpt-4o";
    public string[] FallbackModels { get; set; } = [];
    public string Referer { get; set; } = "https://nika-assistant.example.com";
    public string Title { get; set; } = "NikaAssistant";
}
