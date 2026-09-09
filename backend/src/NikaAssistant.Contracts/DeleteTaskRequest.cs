namespace NikaAssistant.Contracts;

public sealed record DeleteTaskRequest(string Status, string Task, string Assignee, string Comment, string? DueDate = null);
