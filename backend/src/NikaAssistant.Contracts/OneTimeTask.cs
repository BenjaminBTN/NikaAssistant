namespace NikaAssistant.Contracts;

public sealed record OneTimeTask(string Status, string Task, string Assignee, string Comment, IReadOnlyList<string> Tags, string DueDate = "");
