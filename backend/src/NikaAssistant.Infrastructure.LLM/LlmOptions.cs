namespace NikaAssistant.Infrastructure.LLM;

/// <summary>
/// Унифицированные runtime-настройки LLM. Биндятся из секции "Llm"
/// (appsettings.json + appsettings.Local.json + env-переменные Llm__*).
/// Для обратной совместимости поверх биндятся legacy-секции "Gemini" и "OpenRouter":
/// отсутствующие в "Llm" значения подхватываются оттуда.
/// Через IOptionsMonitor изменения файла подхватываются без пересборки,
/// Provider/Model/FallbackModels — без рестарта, ApiKey — со следующего запроса.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Провайдер: "Gemini" (нативный API Google AI Studio) или
    /// "OpenAICompatible" (любой OpenAI-совместимый endpoint: OpenAI, OpenRouter, Ollama и т.п.).
    /// Алиас legacy OpenRouter: "OpenRouter" = OpenAICompatible с BaseUrl OpenRouter.
    /// </summary>
    public string Provider { get; set; } = "Gemini";

    /// <summary>
    /// API-ключ. Для Gemini — ключ из Google AI Studio (aistudio.google.com).
    /// Если пуст — ищется в env: GEMINI_API_KEY / GOOGLE_API_KEY / LLM_API_KEY
    /// (для OpenAICompatible: OPENAI_API_KEY / OPENROUTER_API_KEY / LLM_API_KEY).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Основная модель. Дефолт — бесплатная gemini-2.5-flash.</summary>
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>
    /// Фолбэк-модели: перебираются клиентом по порядку, если основная отвалилась
    /// (rate limit, 5xx, модель недоступна и т.п.).
    /// </summary>
    public string[] FallbackModels { get; set; } = [];

    /// <summary>
    /// Только для OpenAICompatible: базовый URL API.
    /// Примеры: https://api.openai.com/v1/ , https://openrouter.ai/api/v1/ , http://localhost:11434/v1/ .
    /// Для Gemini игнорируется (endpoint фиксирован).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Только для OpenRouter-совместимых endpoint'ов (заголовки HTTP-Referer / X-Title).</summary>
    public string Referer { get; set; } = "https://nika-assistant.example.com";

    /// <summary>Только для OpenRouter-совместимых endpoint'ов (заголовки HTTP-Referer / X-Title).</summary>
    public string Title { get; set; } = "NikaAssistant";
}
