using NikaAssistant.Infrastructure.LLM.OpenRouter;

namespace NikaAssistant.Application.Chat;

public sealed class ChatService
{
    private readonly OpenRouterClient _client;

    public ChatService(OpenRouterClient client)
    {
        _client = client;
    }

    public Task<string> AskAsync(string message, CancellationToken cancellationToken = default) =>
        _client.ChatAsync(message, cancellationToken);
}
