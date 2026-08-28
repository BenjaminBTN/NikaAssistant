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
        var tags = request.Tags == null ? "" : string.Join(", ", request.Tags.Select(Escape));
        var row = BuildRow("[ ]", request.Task, request.Assignee, tags, request.Comment);

        lock (Sync)
        {
            MigrateSchema();

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(_filePath))
            {
                var header =
                    "# Разовые задачи" + Environment.NewLine + Environment.NewLine +
                    "| Статус | Задача | Ответственный | Теги | Комментарий |" + Environment.NewLine +
                    "| --- | --- | --- | --- | --- |" + Environment.NewLine;

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

            MigrateSchema();

            var lines = File.ReadAllLines(_filePath).ToList();
            var index = lines.FindIndex(l =>
            {
                var parsed = ParseRow(l);
                return parsed is not null &&
                    parsed.Value.Status == request.Status.Trim() &&
                    parsed.Value.Task == Escape(request.Task).Trim() &&
                    parsed.Value.Assignee == Escape(request.Assignee).Trim() &&
                    parsed.Value.Comment == Escape(request.Comment).Trim();
            });

            if (index >= 0)
            {
                lines.RemoveAt(index);
                File.WriteAllLines(_filePath, lines);
            }
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default)
    {
        lock (Sync)
        {
            if (!File.Exists(_filePath))
            {
                return Task.CompletedTask;
            }

            MigrateSchema();

            var lines = File.ReadAllLines(_filePath).ToList();
            var index = lines.FindIndex(l =>
            {
                var parsed = ParseRow(l);
                return parsed is not null &&
                    parsed.Value.Status == request.Status.Trim() &&
                    parsed.Value.Task == Escape(request.Task).Trim() &&
                    parsed.Value.Assignee == Escape(request.Assignee).Trim() &&
                    parsed.Value.Comment == Escape(request.Comment).Trim();
            });

            if (index < 0)
            {
                return Task.CompletedTask;
            }

            var parsed = ParseRow(lines[index])!.Value;
            var newTags = request.NewTags == null
                ? parsed.Tags
                : string.Join(", ", request.NewTags.Select(Escape));

            var cells = lines[index].Split('|');
            cells[1] = " " + request.NewStatus + " ";
            cells[4] = " " + newTags + " ";
            lines[index] = string.Join("|", cells);
            File.WriteAllLines(_filePath, lines);
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

        lock (Sync)
        {
            MigrateSchema();
        }

        var pastHeader = false;
        var hasTagsColumn = false;

        foreach (var line in File.ReadAllLines(_filePath))
        {
            if (line.Contains("---", StringComparison.Ordinal))
            {
                pastHeader = true;
                hasTagsColumn = line.Split('|', StringSplitOptions.RemoveEmptyEntries).Length >= 5;
                continue;
            }

            if (!pastHeader)
            {
                continue;
            }

            var parsed = ParseRow(line);
            if (parsed is null)
            {
                continue;
            }

            var status = parsed.Value.Status;
            var task = parsed.Value.Task;
            var assignee = parsed.Value.Assignee;

            string comment;
            List<string> tags;
            if (hasTagsColumn)
            {
                comment = parsed.Value.Comment;
                tags = string.IsNullOrWhiteSpace(parsed.Value.Tags)
                    ? new List<string>()
                    : parsed.Value.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            else
            {
                comment = parsed.Value.Tags;
                tags = new List<string>();
            }

            if (string.IsNullOrWhiteSpace(task))
            {
                continue;
            }

            tasks.Add(new OneTimeTask(status, task, assignee, comment, tags));
        }

        return Task.FromResult<IReadOnlyList<OneTimeTask>>(tasks);
    }

    private static (string Status, string Task, string Assignee, string Tags, string Comment)? ParseRow(string line)
    {
        if (!line.StartsWith("|", StringComparison.Ordinal))
        {
            return null;
        }

        var cells = line.Split('|');
        if (cells.Length < 6)
        {
            return null;
        }

        return (
            Status: cells[1].Trim(),
            Task: cells[2].Trim(),
            Assignee: cells[3].Trim(),
            Tags: cells[4].Trim(),
            Comment: cells[5].Trim());
    }

    private void MigrateSchema()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        var lines = File.ReadAllLines(_filePath);
        var migrated = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("|", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("Статус", StringComparison.Ordinal))
            {
                if (CountDataColumns(line) < 5)
                {
                    lines[i] = "| Статус | Задача | Ответственный | Теги | Комментарий |";
                    migrated = true;
                }
                continue;
            }

            if (line.Contains("---", StringComparison.Ordinal))
            {
                if (CountDataColumns(line) < 5)
                {
                    lines[i] = "| --- | --- | --- | --- | --- |";
                    migrated = true;
                }
                continue;
            }

            if (CountDataColumns(line) == 4)
            {
                var cells = line.Split('|').ToList();
                cells.Insert(4, " ");
                lines[i] = string.Join("|", cells);
                migrated = true;
            }
        }

        if (migrated)
        {
            File.WriteAllLines(_filePath, lines);
        }
    }

    private static int CountDataColumns(string line)
    {
        var cells = line.Split('|');
        return Math.Max(0, cells.Length - 2);
    }

    private static string BuildRow(string status, string task, string assignee, string tags, string? comment) =>
        $"| {status} | {Escape(task)} | {Escape(assignee)} | {tags} | {Escape(comment)} |";

    private static string Escape(string? value) =>
        (value ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", " ")
            .Replace("|", "\\|")
            .Trim();
}
