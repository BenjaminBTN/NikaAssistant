using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NikaAssistant.Application.Abstractions;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NikaAssistant.Infrastructure.LLM.OpenAICompatible;

/// <summary>
/// Обобщённый клиент для любого OpenAI-совместимого endpoint'а
/// (OpenAI, OpenRouter, Ollama, Gemini через OpenAI-совместимый слой и т.п.).
/// Endpoint задаётся через Llm:BaseUrl, ключ — Llm:ApiKey.
/// Модели из Model + FallbackModels перебираются клиентом по порядку.
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<LlmOptions> _options;
    private readonly ILogger<OpenAiCompatibleClient> _logger;

    public OpenAiCompatibleClient(HttpClient httpClient, IOptionsMonitor<LlmOptions> options, ILogger<OpenAiCompatibleClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null,
        CancellationToken cancellationToken = default,
        string? forceToolName = null)
    {
        var snapshot = _options.CurrentValue;
        var apiKey = ResolveApiKey(snapshot);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new LlmResponse(
                "LLM:ApiKey не задан. Укажите ключ в appsettings.Local.json (секция Llm → ApiKey) " +
                "или env-переменной Llm__ApiKey / LLM_API_KEY / OPENAI_API_KEY.",
                Array.Empty<LlmToolCall>(),
                IsError: true,
                ErrorKind: LlmErrorKind.MissingApiKey);
        }

        var baseUrl = ResolveBaseUrl(snapshot);
        var modelsToTry = new[] { snapshot.Model }
            .Concat(snapshot.FallbackModels ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (modelsToTry.Length == 0)
        {
            modelsToTry = ["gpt-4o"];
        }

        var requestMessages = messages.Select(ToRequestMessage).ToArray();
        var toolDefinitions = tools?.Select(ToToolDefinition).ToArray();

        LlmResponse? lastError = null;
        foreach (var model in modelsToTry)
        {
            var sw = Stopwatch.StartNew();
            var result = await TryModelAsync(
                baseUrl, apiKey, snapshot, model,
                requestMessages, toolDefinitions, forceToolName, cancellationToken);
            sw.Stop();
            if (!result.IsError)
            {
                _logger.LogInformation("LLM {Model} ok in {ElapsedMs}ms.", model, sw.ElapsedMilliseconds);
                return result;
            }

            _logger.LogWarning("LLM {Model} failed in {ElapsedMs}ms: {Detail}", model, sw.ElapsedMilliseconds, result.Content);

            if (result.ErrorKind is LlmErrorKind.MissingApiKey or LlmErrorKind.InsufficientCredits || result.FailFast)
            {
                return result;
            }

            lastError = result;
        }

        var tried = string.Join(", ", modelsToTry);
        return new LlmResponse(
            $"Все модели недоступны ({tried}). Последняя ошибка: {lastError?.Content ?? "неизвестная ошибка"}",
            Array.Empty<LlmToolCall>(),
            IsError: true,
            ErrorKind: lastError?.ErrorKind ?? LlmErrorKind.ProviderError);
    }

    private async Task<LlmResponse> TryModelAsync(
        string baseUrl,
        string apiKey,
        LlmOptions snapshot,
        string model,
        RequestMessage[] requestMessages,
        ToolDefinition[]? toolDefinitions,
        string? forceToolName,
        CancellationToken cancellationToken)
    {
        var referer = string.IsNullOrWhiteSpace(snapshot.Referer) ? "https://nika-assistant.example.com" : snapshot.Referer;
        var title = string.IsNullOrWhiteSpace(snapshot.Title) ? "NikaAssistant" : snapshot.Title;

        var request = new ChatCompletionRequest(model, requestMessages, toolDefinitions,
            forceToolName is null ? null : new ToolChoice("function", new ToolChoiceFunction(forceToolName)));

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // Заголовки OpenRouter — остальные провайдеры их игнорируют.
        httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", referer);
        httpRequest.Headers.TryAddWithoutValidation("X-Title", title);
        httpRequest.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LlmResponse(
                $"Ошибка сети LLM ({model}): превышено время ожидания ответа.",
                Array.Empty<LlmToolCall>(),
                IsError: true,
                ErrorKind: LlmErrorKind.ProviderError);
        }
        catch (HttpRequestException ex)
        {
            return new LlmResponse(
                $"Ошибка сети LLM ({model}): {ex.Message}",
                Array.Empty<LlmToolCall>(),
                IsError: true,
                ErrorKind: LlmErrorKind.ProviderError);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var errorKind = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.PaymentRequired => LlmErrorKind.InsufficientCredits,
                    (System.Net.HttpStatusCode)429 => LlmErrorKind.RateLimited,
                    _ => LlmErrorKind.ProviderError
                };
                var failFast = response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.PaymentRequired;
                var hint = errorKind switch
                {
                    LlmErrorKind.InsufficientCredits => " Возможно, закончился баланс аккаунта.",
                    LlmErrorKind.RateLimited => " Превышен лимит запросов — пробую следующую модель.",
                    _ => string.Empty
                };
                return new LlmResponse(
                    $"Ошибка LLM ({model}): {(int)response.StatusCode} {response.StatusCode}{hint} {errorBody}".Trim(),
                    Array.Empty<LlmToolCall>(),
                    IsError: true,
                    ErrorKind: errorKind,
                    FailFast: failFast);
            }

            var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
                JsonOptions, cancellationToken);

            var message = result?.Choices.FirstOrDefault()?.Message;
            var toolCalls = (message?.ToolCalls ?? Array.Empty<ResponseToolCall>())
                .Select(t => new LlmToolCall(t.Id, t.Function.Name, t.Function.Arguments))
                .ToArray();

            return new LlmResponse(message?.Content, toolCalls, IsError: false, Model: result?.Model ?? model);
        }
    }

    internal static string ResolveBaseUrl(LlmOptions snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.BaseUrl))
        {
            return snapshot.BaseUrl!.Trim();
        }

        // Legacy-алиас: Provider=OpenRouter без явного BaseUrl — endpoint OpenRouter.
        if (string.Equals(snapshot.Provider, "OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            return "https://openrouter.ai/api/v1/";
        }

        return "https://api.openai.com/v1/";
    }

    internal static string? ResolveApiKey(LlmOptions snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ApiKey))
        {
            return snapshot.ApiKey!.Trim();
        }

        var env = Environment.GetEnvironmentVariable("LLM_API_KEY")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    private static RequestMessage ToRequestMessage(LlmMessage message)
    {
        RequestToolCall[]? toolCalls = message.ToolCalls?
            .Select(t => new RequestToolCall(t.Id, "function", new RequestFunction(t.Name, t.ArgumentsJson)))
            .ToArray();

        return new RequestMessage(message.Role, message.Content, message.ToolCallId, toolCalls);
    }

    private static ToolDefinition ToToolDefinition(LlmTool tool)
    {
        using var doc = JsonDocument.Parse(tool.ParametersJson);
        return new ToolDefinition("function", new ToolFunction(tool.Name, tool.Description, doc.RootElement.Clone()));
    }

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] RequestMessage[] Messages,
        [property: JsonPropertyName("tools")] ToolDefinition[]? Tools = null,
        [property: JsonPropertyName("tool_choice")] ToolChoice? ToolChoice = null);

    private sealed record ToolChoice(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] ToolChoiceFunction Function);

    private sealed record ToolChoiceFunction(
        [property: JsonPropertyName("name")] string Name);

    private sealed record RequestMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null,
        [property: JsonPropertyName("tool_calls")] RequestToolCall[]? ToolCalls = null);

    private sealed record RequestToolCall(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] RequestFunction Function);

    private sealed record RequestFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] string Arguments);

    private sealed record ToolDefinition(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] ToolFunction Function);

    private sealed record ToolFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] Choice[] Choices,
        [property: JsonPropertyName("model")] string? Model = null);

    private sealed record Choice(
        [property: JsonPropertyName("message")] ResponseMessage Message);

    private sealed record ResponseMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("tool_calls")] ResponseToolCall[]? ToolCalls);

    private sealed record ResponseToolCall(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] ResponseFunction Function);

    private sealed record ResponseFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] string Arguments);
}
