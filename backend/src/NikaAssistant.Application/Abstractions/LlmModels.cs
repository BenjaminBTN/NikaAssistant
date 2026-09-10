namespace NikaAssistant.Application.Abstractions;

public sealed record LlmMessage(
    string Role,
    string? Content,
    string? ToolCallId = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null);

public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

public enum LlmErrorKind
{
    None,
    RateLimited,
    InsufficientCredits,
    ProviderError
}

public sealed record LlmResponse(string? Content, IReadOnlyList<LlmToolCall> ToolCalls, bool IsError = false, string? Model = null, LlmErrorKind ErrorKind = LlmErrorKind.None);

public sealed record LlmTool(string Name, string Description, string ParametersJson);
