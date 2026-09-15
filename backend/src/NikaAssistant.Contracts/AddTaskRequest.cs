namespace NikaAssistant.Contracts;

public sealed record AddTaskRequest(string Task, string? Assignee, string? Comment, List<string>? Tags, string? DueDate = null);
