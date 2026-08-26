using NikaAssistant.Contracts;

namespace NikaAssistant.Application.CreateTask;

public sealed class AddTaskHandler
{
    private const string FilePath = @"C:\Users\galki\Storage\Tasks\OneTime\one-time-tasks.md";
    private static readonly object Sync = new();

    public Task AddTaskAsync(AddTaskRequest request, CancellationToken cancellationToken = default)
    {
        var row = $"| [ ] | {Escape(request.Task)} | {Escape(request.Assignee)} | {Escape(request.Comment)} |";

        lock (Sync)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(FilePath))
            {
                var header =
                    "# Разовые задачи" + Environment.NewLine + Environment.NewLine +
                    "| Статус | Задача | Ответственный | Комментарий |" + Environment.NewLine +
                    "| --- | --- | --- | --- |" + Environment.NewLine;

                File.WriteAllText(FilePath, header);
            }

            File.AppendAllText(FilePath, row + Environment.NewLine);
        }

        return Task.CompletedTask;
    }

    private static string Escape(string? value) =>
        (value ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", " ")
            .Replace("|", "\\|")
            .Trim();
}
