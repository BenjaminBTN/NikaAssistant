namespace NikaAssistant.Contracts;

public sealed record UpdateTaskRequest(string Status, string Task, string Assignee, string Comment, string NewStatus);
