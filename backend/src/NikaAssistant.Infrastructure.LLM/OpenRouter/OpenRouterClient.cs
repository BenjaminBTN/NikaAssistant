using Microsoft.Extensions.Options;
using NikaAssistant.Application.Abstractions;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NikaAssistant.Infrastructure.LLM.OpenRouter;

public sealed class OpenRouterClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<OpenRouterOptions> _options;

    public OpenRouterClient(HttpClient httpClient, IOptionsMonitor<OpenRouterOptions> options)
    {
        _httpClient = httpClient;
        _options = options;
        _httpClient.BaseAddress = new Uri("https://openrouter.ai/api/v1/");
    }

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null,
        CancellationToken cancellationToken = default,
        string? forceToolName = null)
    {
        // Опции читаются на каждый запрос: правка appsettings.json / appsettings.Local.json
        // или env-переменных применяется без пересборки (JSON — без рестарта).
        var snapshot = _options.CurrentValue;
        var apiKey = string.IsNullOrWhiteSpace(snapshot.ApiKey)
            ? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
            : snapshot.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new LlmResponse(
                "OpenRouter:ApiKey не задан. Укажите ключ в appsettings.Local.json, appsettings.json (поле OpenRouter:ApiKey) или env-переменной OpenRouter__ApiKey / OPENROUTER_API_KEY.",
                Array.Empty<LlmToolCall>(),
                IsError: true,
                ErrorKind: LlmErrorKind.ProviderError);
        }

        var model = string.IsNullOrWhiteSpace(snapshot.Model) ? "openai/gpt-4o" : snapshot.Model;
        var fallbackModels = snapshot.FallbackModels?
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToArray() ?? [];
        var referer = string.IsNullOrWhiteSpace(snapshot.Referer) ? "https://nika-assistant.example.com" : snapshot.Referer;
        var title = string.IsNullOrWhiteSpace(snapshot.Title) ? "NikaAssistant" : snapshot.Title;

        var requestMessages = messages.Select(ToRequestMessage).ToArray();
        var toolDefinitions = tools?.Select(ToToolDefinition).ToArray();

        // OpenRouter сам переберёт модели по порядку, если основная отвалилась (DEGRADED, rate limit и т.п.).
        string[]? models = null;
        if (fallbackModels.Length > 0)
        {
            models = new string[fallbackModels.Length + 1];
            models[0] = model;
            Array.Copy(fallbackModels, 0, models, 1, fallbackModels.Length);
        }

        var request = new ChatCompletionRequest(model, requestMessages, toolDefinitions, models,
            forceToolName is null ? null : new ToolChoice("function", new ToolChoiceFunction(forceToolName)));

        var json = JsonSerializer.Serialize(request, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Add("HTTP-Referer", referer);
        httpRequest.Headers.Add("X-Title", title);
        httpRequest.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorKind = response.StatusCode switch
            {
                System.Net.HttpStatusCode.PaymentRequired => LlmErrorKind.InsufficientCredits,
                (System.Net.HttpStatusCode)429 => LlmErrorKind.RateLimited,
                _ => LlmErrorKind.ProviderError
            };
            var hint = errorKind switch
            {
                LlmErrorKind.InsufficientCredits => " Возможно, закончился баланс аккаунта.",
                LlmErrorKind.RateLimited => " Превышен лимит запросов — подождите и попробуйте снова.",
                _ => string.Empty
            };
            return new LlmResponse(
                $"Ошибка OpenRouter: {(int)response.StatusCode} {response.StatusCode}{hint} {errorBody}".Trim(),
                Array.Empty<LlmToolCall>(),
                IsError: true,
                ErrorKind: errorKind);
        }

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
            JsonOptions, cancellationToken);

        var message = result?.Choices.FirstOrDefault()?.Message;
        var toolCalls = (message?.ToolCalls ?? Array.Empty<ResponseToolCall>())
            .Select(t => new LlmToolCall(t.Id, t.Function.Name, t.Function.Arguments))
            .ToArray();

        return new LlmResponse(message?.Content, toolCalls, IsError: false, Model: result?.Model);
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
        [property: JsonPropertyName("models")] string[]? Models = null,
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
