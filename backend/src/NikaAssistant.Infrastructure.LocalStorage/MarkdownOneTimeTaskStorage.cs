using Microsoft.Extensions.Configuration;
using NikaAssistant.Contracts;

namespace NikaAssistant.Infrastructure.LocalStorage;

public sealed class MarkdownOneTimeTaskStorage : IOneTimeTaskStorage
{
    private const string DefaultFilePath = @"C:\Users\galki\Storage\Tasks\OneTime\one-time-tasks.md";
    private static readonly object Sync = new();

    private readonly string _filePath;

    public MarkdownOneTimeTaskStorage(IConfiguration configuration)
    {
        _filePath = configuration["Storage:OneTimeTasksPath"] ?? DefaultFilePath;
    }

    public Task AddAsync(AddTaskRequest request, CancellationToken cancellationToken = default)
    {
        var row = $"| [ ] | {Escape(request.Task)} | {Escape(request.Assignee)} | {Escape(request.Comment)} |";

        lock (Sync)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(_filePath))
            {
                var header =
                    "# Разовые задачи" + Environment.NewLine + Environment.NewLine +
                    "| Статус | Задача | Ответственный | Комментарий |" + Environment.NewLine +
                    "| --- | --- | --- | --- |" + Environment.NewLine;

                File.WriteAllText(_filePath, header);
            }

            File.AppendAllText(_filePath, row + Environment.NewLine);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default)
    {
        lock (Sync)
        {
            if (!File.Exists(_filePath))
            {
                return Task.CompletedTask;
            }

            var row = $"| {request.Status} | {Escape(request.Task)} | {Escape(request.Assignee)} | {Escape(request.Comment)} |";

            var lines = File.ReadAllLines(_filePath).ToList();
            var index = lines.FindIndex(l => l.Equals(row, StringComparison.Ordinal));

            if (index >= 0)
            {
                lines.RemoveAt(index);
                File.WriteAllLines(_filePath, lines);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OneTimeTask>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var tasks = new List<OneTimeTask>();

        if (!File.Exists(_filePath))
        {
            return Task.FromResult<IReadOnlyList<OneTimeTask>>(tasks);
        }

        var pastHeader = false;

        foreach (var line in File.ReadAllLines(_filePath))
        {
            if (!line.StartsWith("|", StringComparison.Ordinal))
            {
                pastHeader = false;
                continue;
            }

            if (line.Contains("---", StringComparison.Ordinal))
            {
                pastHeader = true;
                continue;
            }

            if (!pastHeader)
            {
                continue;
            }

            var cells = line.Split('|');
            if (cells.Length < 6)
            {
                continue;
            }

            var status = cells[1].Trim();
            var task = cells[2].Trim();
            var assignee = cells[3].Trim();
            var comment = cells[4].Trim();

            if (string.IsNullOrWhiteSpace(task))
            {
                continue;
            }

            tasks.Add(new OneTimeTask(status, task, assignee, comment));
        }

        return Task.FromResult<IReadOnlyList<OneTimeTask>>(tasks);
    }

    private static string Escape(string? value) =>
        (value ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", " ")
            .Replace("|", "\\|")
            .Trim();
}
