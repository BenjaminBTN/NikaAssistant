using Microsoft.Extensions.Options;
using NikaAssistant.Application.Abstractions;
using NikaAssistant.Infrastructure.LLM.Gemini;
using NikaAssistant.Infrastructure.LLM.OpenAICompatible;

namespace NikaAssistant.Infrastructure.LLM;

/// <summary>
/// Роутер ILlmClient: выбирает реализацию по Llm:Provider на каждый запрос,
/// так что смена провайдера/модели в конфиге применяется без рестарта.
/// "Gemini" (и алиас "Google") — нативный API Google AI Studio;
/// "OpenAICompatible" (алиасы "OpenAI", "OpenRouter", "Custom") —
/// любой OpenAI-совместимый endpoint через Llm:BaseUrl.
/// </summary>
public sealed class LlmClientRouter : ILlmClient
{
    private readonly GeminiClient _gemini;
    private readonly OpenAiCompatibleClient _openAiCompatible;
    private readonly IOptionsMonitor<LlmOptions> _options;

    public LlmClientRouter(
        GeminiClient gemini,
        OpenAiCompatibleClient openAiCompatible,
        IOptionsMonitor<LlmOptions> options)
    {
        _gemini = gemini;
        _openAiCompatible = openAiCompatible;
        _options = options;
    }

    public Task<LlmResponse> CompleteAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null,
        CancellationToken cancellationToken = default,
        string? forceToolName = null)
    {
        var provider = _options.CurrentValue.Provider?.Trim();
        if (IsGemini(provider))
        {
            return _gemini.CompleteAsync(messages, tools, cancellationToken, forceToolName);
        }

        if (IsOpenAiCompatible(provider))
        {
            return _openAiCompatible.CompleteAsync(messages, tools, cancellationToken, forceToolName);
        }

        return Task.FromResult(new LlmResponse(
            $"Неизвестный LLM-провайдер «{provider}». Допустимые значения Llm:Provider: " +
            "«Gemini» (ключ из Google AI Studio) или «OpenAICompatible» (любой OpenAI-совместимый endpoint через Llm:BaseUrl).",
            Array.Empty<LlmToolCall>(),
            IsError: true,
            ErrorKind: LlmErrorKind.ProviderError));
    }

    internal static bool IsGemini(string? provider) =>
        string.IsNullOrWhiteSpace(provider)
        || string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "Google", StringComparison.OrdinalIgnoreCase);

    internal static bool IsOpenAiCompatible(string? provider) =>
        string.Equals(provider, "OpenAICompatible", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "OpenAI-Compatible", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "OpenRouter", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "Custom", StringComparison.OrdinalIgnoreCase);
}
