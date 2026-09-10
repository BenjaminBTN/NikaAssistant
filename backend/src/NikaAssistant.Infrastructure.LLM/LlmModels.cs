namespace NikaAssistant.Infrastructure.LLM;

public sealed record LlmMessage(
    string Role,
    string? Content,
    string? ToolCallId = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null);

public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

public sealed record LlmResponse(string? Content, IReadOnlyList<LlmToolCall> ToolCalls, bool IsError = false, string? Model = null);

public sealed record LlmTool(string Name, string Description, string ParametersJson);
