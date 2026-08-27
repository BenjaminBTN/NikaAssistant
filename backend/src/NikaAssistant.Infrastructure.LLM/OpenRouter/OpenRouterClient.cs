using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NikaAssistant.Infrastructure.LLM.OpenRouter;

public sealed class OpenRouterClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;

    public OpenRouterClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _apiKey = configuration["OpenRouter:ApiKey"]
            ?? throw new InvalidOperationException("OpenRouter:ApiKey не задан в конфигурации.");
        _model = configuration["OpenRouter:Model"] ?? "openai/gpt-4o";
        _httpClient.BaseAddress = new Uri("https://openrouter.ai/api/v1/");
    }

    public async Task<string> ChatAsync(string message, CancellationToken cancellationToken = default)
    {
        var request = new ChatCompletionRequest(_model, new[] { new Message("user", message) });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Headers.Add("HTTP-Referer", "https://nika.local");
        httpRequest.Headers.Add("X-Title", "NikaAssistant");
        httpRequest.Content = JsonContent.Create(request);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var hint = response.StatusCode switch
            {
                System.Net.HttpStatusCode.PaymentRequired => " Возможно, закончился баланс аккаунта OpenRouter.",
                (System.Net.HttpStatusCode)429 => " Превышен лимит запросов (rate limit) — подождите и попробуйте снова.",
                _ => string.Empty
            };
            return $"Ошибка OpenRouter: {(int)response.StatusCode} {response.StatusCode}{hint} {errorBody}".Trim();
        }

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
            cancellationToken: cancellationToken);

        return result?.Choices.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }
}

internal sealed record ChatCompletionRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] Message[] Messages);

internal sealed record Message(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record ChatCompletionResponse(
    [property: JsonPropertyName("choices")] Choice[] Choices);

internal sealed record Choice(
    [property: JsonPropertyName("message")] Message Message);
