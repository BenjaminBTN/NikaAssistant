namespace NikaAssistant.Infrastructure.LLM;

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null,
        CancellationToken cancellationToken = default);
}
