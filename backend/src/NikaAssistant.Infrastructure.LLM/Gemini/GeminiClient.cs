using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NikaAssistant.Application.Abstractions;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NikaAssistant.Infrastructure.LLM.Gemini;

/// <summary>
/// Нативный клиент Gemini API (Google AI Studio):
/// POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent
/// с function calling (functionDeclarations). Модели из Model + FallbackModels
/// перебираются клиентом по порядку: при rate limit / 5xx / недоступности модели
/// запрос повторяется на следующей. Ключ — из AI Studio (aistudio.google.com).
/// </summary>
public sealed class GeminiClient : ILlmClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";

    /// <summary>Префикс id, сгенерированных локально (модель id не возвращала — назад не отправляем).</summary>
    private const string LocalCallIdPrefix = "local-";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<LlmOptions> _options;
    private readonly ILogger<GeminiClient> _logger;

    public GeminiClient(HttpClient httpClient, IOptionsMonitor<LlmOptions> options, ILogger<GeminiClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(BaseUrl);
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
                "Gemini:ApiKey не задан. Вставьте ключ из Google AI Studio (aistudio.google.com) " +
                "в appsettings.Local.json рядом с exe (секция Llm → ApiKey) " +
                "или задайте env-переменную GEMINI_API_KEY / GOOGLE_API_KEY / Llm__ApiKey.",
                Array.Empty<LlmToolCall>(),
                IsError: true,
                ErrorKind: LlmErrorKind.MissingApiKey);
        }

        var modelsToTry = new[] { snapshot.Model }
            .Concat(snapshot.FallbackModels ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (modelsToTry.Length == 0)
        {
            modelsToTry = ["gemini-2.5-flash"];
        }

        var geminiTools = BuildTools(tools);
        var toolConfig = forceToolName is null
            ? null
            : new GeminiToolConfig(new GeminiFunctionCallingConfig("ANY", [forceToolName]));

        LlmResponse? lastError = null;
        foreach (var model in modelsToTry)
        {
            var sw = Stopwatch.StartNew();
            var result = await TryModelAsync(model, messages, geminiTools, toolConfig, apiKey, cancellationToken);
            sw.Stop();
            if (!result.IsError)
            {
                _logger.LogInformation("Gemini {Model} ok in {ElapsedMs}ms.", model, sw.ElapsedMilliseconds);
                return result;
            }

            _logger.LogWarning("Gemini {Model} failed in {ElapsedMs}ms: {Detail}", model, sw.ElapsedMilliseconds, result.Content);

            // Ошибка ключа/биллинга одинакова для всех моделей — дальше пробовать бессмысленно.
            if (result.ErrorKind is LlmErrorKind.MissingApiKey or LlmErrorKind.InsufficientCredits || result.FailFast)
            {
                return result;
            }

            lastError = result;
        }

        var tried = string.Join(", ", modelsToTry);
        var detail = lastError?.Content ?? "неизвестная ошибка";
        return new LlmResponse(
            $"Все модели Gemini недоступны ({tried}). Последняя ошибка: {detail}",
            Array.Empty<LlmToolCall>(),
            IsError: true,
            ErrorKind: lastError?.ErrorKind ?? LlmErrorKind.ProviderError);
    }

    private async Task<LlmResponse> TryModelAsync(
        string model,
        IReadOnlyList<LlmMessage> messages,
        GeminiTool[]? tools,
        GeminiToolConfig? toolConfig,
        string apiKey,
        CancellationToken cancellationToken)
    {
        GeminiRequest request;
        try
        {
            request = BuildRequest(messages, tools, toolConfig);
        }
        catch (Exception ex)
        {
            return new LlmResponse(
                $"Ошибка подготовки запроса Gemini ({model}): {ex.Message}",
                Array.Empty<LlmToolCall>(),
                IsError: true,
                ErrorKind: LlmErrorKind.ProviderError);
        }

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"models/{Uri.EscapeDataString(model)}:generateContent");
        httpRequest.Headers.Add("x-goog-api-key", apiKey);
        httpRequest.Content = JsonContent.Create(request, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Таймаут соединения, а не отмена пользователем, — пробуем следующую модель.
            return new LlmResponse(
                $"Ошибка сети Gemini ({model}): превышено время ожидания ответа.",
                Array.Empty<LlmToolCall>(),
                IsError: true,
                ErrorKind: LlmErrorKind.ProviderError);
        }
        catch (HttpRequestException ex)
        {
            // Сеть/TLS/DNS: не роняем запрос 500-й, а идём к следующей модели.
            return new LlmResponse(
                $"Ошибка сети Gemini ({model}): {ex.Message}",
                Array.Empty<LlmToolCall>(),
                IsError: true,
                ErrorKind: LlmErrorKind.ProviderError);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var errorText = ExtractGoogleError(errorBody);
                var status = (int)response.StatusCode;

                // Неверный/заблокированный ключ и пустой биллинг — fail fast, остальные модели не спасут.
                var failFast = response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.PaymentRequired;

                var errorKind = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.PaymentRequired => LlmErrorKind.InsufficientCredits,
                    (System.Net.HttpStatusCode)429 => LlmErrorKind.RateLimited,
                    System.Net.HttpStatusCode.Unauthorized => LlmErrorKind.ProviderError,
                    System.Net.HttpStatusCode.Forbidden => LlmErrorKind.ProviderError,
                    _ => LlmErrorKind.ProviderError
                };
                var hint = errorKind switch
                {
                    LlmErrorKind.InsufficientCredits => " Проверьте биллинг в Google AI Studio.",
                    LlmErrorKind.RateLimited => " Превышен лимит запросов — пробую следующую модель.",
                    _ => string.Empty
                };
                var keyHint = failFast && LooksLikeKeyError(errorText)
                    ? " Похоже, API-ключ неверный или заблокирован — проверьте ключ из AI Studio."
                    : string.Empty;

                return new LlmResponse(
                    $"Ошибка Gemini ({model}): {status} {response.StatusCode}.{hint}{keyHint} {errorText}".Trim(),
                    Array.Empty<LlmToolCall>(),
                    IsError: true,
                    ErrorKind: errorKind,
                    FailFast: failFast);
            }

            var payload = await response.Content.ReadFromJsonAsync<GeminiResponse>(JsonOptions, cancellationToken);
            return MapResponse(payload, model);
        }
    }

    private static LlmResponse MapResponse(GeminiResponse? payload, string model)
    {
        var candidate = payload?.Candidates.FirstOrDefault();
        if (candidate?.Content is null)
        {
            var blockReason = payload?.PromptFeedback?.BlockReason;
            var detail = blockReason is null
                ? "пустой ответ без candidates"
                : $"запрос заблокирован (blockReason: {blockReason})";
            return new LlmResponse(
                $"Ошибка Gemini ({model}): {detail}.",
                Array.Empty<LlmToolCall>(),
                IsError: true,
                ErrorKind: LlmErrorKind.ProviderError);
        }

        var texts = new List<string>();
        var calls = new List<LlmToolCall>();
        foreach (var part in candidate.Content.Parts ?? [])
        {
            // Мыслительный процесс модели (thinking): внутренний черновик, пользователю не показываем.
            if (part.Thought)
            {
                continue;
            }

            if (part.Text is not null)
            {
                texts.Add(part.Text);
            }

            if (part.FunctionCall is not null)
            {
                var fc = part.FunctionCall;
                var id = string.IsNullOrWhiteSpace(fc.Id)
                    ? LocalCallIdPrefix + Guid.NewGuid().ToString("N")
                    : fc.Id;
                var argsJson = fc.Args is null ? "{}" : JsonSerializer.Serialize(fc.Args, JsonOptions);
                calls.Add(new LlmToolCall(id, fc.Name ?? string.Empty, argsJson));
            }
        }

        var content = texts.Count == 0 ? null : string.Concat(texts);
        return new LlmResponse(content, calls, IsError: false, Model: model);
    }

    private static GeminiRequest BuildRequest(
        IReadOnlyList<LlmMessage> messages,
        GeminiTool[]? tools,
        GeminiToolConfig? toolConfig)
    {
        var systemTexts = new List<string>();
        var contents = new List<GeminiContent>();
        // toolCallId -> имя функции (нужно для functionResponse: Gemini требует name).
        var callIdToName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case "system":
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        systemTexts.Add(message.Content);
                    }

                    break;

                case "assistant":
                    var assistantParts = new List<GeminiPart>();
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        assistantParts.Add(new GeminiPart(Text: message.Content));
                    }

                    foreach (var call in message.ToolCalls ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(call.Name))
                        {
                            callIdToName[call.Id] = call.Name;
                        }

                        assistantParts.Add(new GeminiPart(FunctionCall: new GeminiFunctionCall(
                            call.Name,
                            ParseArgs(call.ArgumentsJson),
                            ToModelId(call.Id))));
                    }

                    contents.Add(new GeminiContent("model", assistantParts.ToArray()));
                    break;

                case "tool":
                    var toolName = callIdToName.TryGetValue(message.ToolCallId ?? string.Empty, out var known)
                        ? known
                        : tools?.FirstOrDefault()?.FunctionDeclarations.FirstOrDefault()?.Name;
                    contents.Add(new GeminiContent("user",
                    [
                        new GeminiPart(FunctionResponse: new GeminiFunctionResponse(
                            toolName ?? "unknown",
                            new Dictionary<string, object?> { ["result"] = message.Content ?? string.Empty },
                            ToModelId(message.ToolCallId)))
                    ]));
                    break;

                default: // "user" и всё остальное
                    contents.Add(new GeminiContent("user",
                    [
                        new GeminiPart(Text: message.Content ?? string.Empty)
                    ]));
                    break;
            }
        }

        GeminiSystemInstruction? systemInstruction = systemTexts.Count == 0
            ? null
            : new GeminiSystemInstruction([new GeminiPart(Text: string.Join("\n\n", systemTexts))]);

        return new GeminiRequest(contents.ToArray(), systemInstruction, tools, toolConfig);
    }

    /// <summary>Локально сгенерированные id назад модели не отправляем (она их не знает).</summary>
    private static string? ToModelId(string? id) =>
        string.IsNullOrWhiteSpace(id) || id.StartsWith(LocalCallIdPrefix, StringComparison.Ordinal)
            ? null
            : id;

    private static GeminiTool[]? BuildTools(IReadOnlyList<LlmTool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        return
        [
            new GeminiTool(tools.Select(t => new GeminiFunctionDeclaration(
                t.Name,
                t.Description,
                NormalizeSchemaTypes(t.ParametersJson))).ToArray())
        ];
    }

    /// <summary>
    /// Gemini ждёт типы схемы в верхнем регистре (OBJECT, STRING, ...),
    /// а наши тулзы описаны в классическом JSON Schema (object, string).
    /// Нормализуем значения "type" рекурсивно, остальное не трогаем.
    /// </summary>
    private static JsonElement NormalizeSchemaTypes(string parametersJson)
    {
        using var doc = JsonDocument.Parse(parametersJson);
        var node = JsonNode.Parse(doc.RootElement.GetRawText());
        UppercaseTypes(node);
        return JsonSerializer.SerializeToElement(node, JsonOptions);
    }

    private static void UppercaseTypes(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var prop in obj.ToArray())
            {
                if (prop.Key == "type" && prop.Value is JsonValue value
                    && value.TryGetValue<string>(out var typeName))
                {
                    obj[prop.Key] = typeName.ToUpperInvariant();
                }
                else
                {
                    UppercaseTypes(prop.Value);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                UppercaseTypes(item);
            }
        }
    }

    private static object? ParseArgs(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(argumentsJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractGoogleError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // Не JSON — вернём как есть (обрежем).
        }

        return body.Length <= 500 ? body : body.Substring(0, 500) + "…";
    }

    private static bool LooksLikeKeyError(string? text) =>
        !string.IsNullOrEmpty(text) && (
            text.Contains("API key", StringComparison.OrdinalIgnoreCase)
            || text.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)
            || text.Contains("APIKey", StringComparison.OrdinalIgnoreCase));

    internal static string? ResolveApiKey(LlmOptions snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ApiKey))
        {
            return snapshot.ApiKey!.Trim();
        }

        var env = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
            ?? Environment.GetEnvironmentVariable("LLM_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    // --- Gemini wire models (имена полей — строго как в REST v1beta) ---

    private sealed record GeminiRequest(
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("systemInstruction")] GeminiSystemInstruction? SystemInstruction = null,
        [property: JsonPropertyName("tools")] GeminiTool[]? Tools = null,
        [property: JsonPropertyName("toolConfig")] GeminiToolConfig? ToolConfig = null);

    private sealed record GeminiSystemInstruction(
        [property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiContent(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiPart(
        [property: JsonPropertyName("text")] string? Text = null,
        [property: JsonPropertyName("thought")] bool Thought = false,
        [property: JsonPropertyName("functionCall")] GeminiFunctionCall? FunctionCall = null,
        [property: JsonPropertyName("functionResponse")] GeminiFunctionResponse? FunctionResponse = null);

    private sealed record GeminiTool(
        [property: JsonPropertyName("functionDeclarations")] GeminiFunctionDeclaration[] FunctionDeclarations);

    private sealed record GeminiFunctionDeclaration(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters);

    private sealed record GeminiToolConfig(
        [property: JsonPropertyName("functionCallingConfig")] GeminiFunctionCallingConfig FunctionCallingConfig);

    private sealed record GeminiFunctionCallingConfig(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("allowedFunctionNames")] string[]? AllowedFunctionNames = null);

    private sealed record GeminiFunctionCall(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("args")] object? Args,
        [property: JsonPropertyName("id")] string? Id = null);

    private sealed record GeminiFunctionResponse(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("response")] object? Response,
        [property: JsonPropertyName("id")] string? Id = null);

    private sealed record GeminiResponse(
        [property: JsonPropertyName("candidates")] GeminiCandidate[] Candidates,
        [property: JsonPropertyName("promptFeedback")] GeminiPromptFeedback? PromptFeedback = null);

    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content,
        [property: JsonPropertyName("finishReason")] string? FinishReason = null);

    private sealed record GeminiPromptFeedback(
        [property: JsonPropertyName("blockReason")] string? BlockReason = null);
}
