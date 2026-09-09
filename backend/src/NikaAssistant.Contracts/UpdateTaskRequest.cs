namespace NikaAssistant.Contracts;

public sealed record UpdateTaskRequest(string Status, string Task, string Assignee, string Comment, string NewStatus, List<string>? NewTags = null, string? NewComment = null, string? NewTask = null, string? NewAssignee = null, string? DueDate = null, string? NewDueDate = null);
