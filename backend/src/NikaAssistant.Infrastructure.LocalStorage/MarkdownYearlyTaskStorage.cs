using Microsoft.Extensions.Configuration;
using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;
using NikaAssistant.Domain;

namespace NikaAssistant.Infrastructure.LocalStorage;

public sealed class MarkdownYearlyTaskStorage : IYearlyTaskStorage
{
    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "NikaAssistant", "Tasks", "Yearly", "yearly-tasks.md");
    private static readonly string DefaultArchivePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "NikaAssistant", "Tasks", "Yearly", "archive-yearly-tasks.md");
    private static readonly object Sync = new();

    // Актуальный порядок столбцов: Статус | Задача | Срок | Ответственный | Теги | Комментарий
    private const string HeaderRow = "| Статус | Задача | Срок | Ответственный | Теги | Комментарий |";
    private const string SeparatorRow = "| --- | --- | --- | --- | --- | --- |";

    private readonly string _filePath;
    private readonly string _archivePath;
    private readonly string? _defaultAssignee;

    public MarkdownYearlyTaskStorage(IConfiguration configuration)
    {
        _filePath = ResolvePath(configuration["Storage:YearlyTasksPath"], DefaultFilePath);
        _archivePath = ResolvePath(
            configuration["Storage:YearlyArchivePath"] ?? configuration["Storage:ArchivePath"],
            DefaultArchivePath);
        _defaultAssignee = configuration["Storage:DefaultAssignee"];
    }

    private static string ResolvePath(string? configuredPath, string fallbackPath)
    {
        var raw = string.IsNullOrWhiteSpace(configuredPath) ? fallbackPath : configuredPath;
        raw = Environment.ExpandEnvironmentVariables(raw);
        if (raw.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            raw = Path.Combine(home, raw.Substring(1).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return raw;
    }

    public string ResolveAssignee(string? assignee) =>
        string.IsNullOrWhiteSpace(assignee) ? _defaultAssignee ?? string.Empty : assignee;

    public Task AddAsync(AddTaskRequest request, CancellationToken cancellationToken = default)
    {
        request = request with
        {
            Task = TaskNormalizer.NormalizeTaskTitle(request.Task),
            Tags = YearlyTaskRules.EnsureYearlyTag(request.Tags),
        };
        var assignee = ResolveAssignee(request.Assignee);
        var tags = string.Join(", ", request.Tags!.Select(Escape));
        var dueDate = TaskNormalizer.NormalizeDueDate(request.DueDate);
        var row = BuildRow("[ ]", request.Task, dueDate, assignee, tags, request.Comment);

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
                    "# Ежегодные задачи" + Environment.NewLine + Environment.NewLine +
                    HeaderRow + Environment.NewLine +
                    SeparatorRow + Environment.NewLine;

                File.WriteAllText(_filePath, header);
            }

            EnsureTrailingNewline(_filePath);
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
                return parsed is not null && MatchesDelete(parsed.Value, request);
            });

            if (index >= 0)
            {
                var archived = lines[index];
                lines.RemoveAt(index);
                File.WriteAllLines(_filePath, lines);
                AppendToArchive(archived);
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> ArchiveCompletedAsync(CancellationToken cancellationToken = default)
    {
        lock (Sync)
        {
            if (!File.Exists(_filePath))
            {
                return Task.FromResult(0);
            }

            MigrateSchema();

            var lines = File.ReadAllLines(_filePath).ToList();
            var archivedRows = new List<string>();
            var remaining = new List<string>(lines.Count);

            foreach (var line in lines)
            {
                var parsed = ParseRow(line);
                if (parsed is not null && IsCompleted(parsed.Value.Status))
                {
                    archivedRows.Add(line);
                }
                else
                {
                    remaining.Add(line);
                }
            }

            if (archivedRows.Count == 0)
            {
                return Task.FromResult(0);
            }

            File.WriteAllLines(_filePath, remaining);
            foreach (var row in archivedRows)
            {
                AppendToArchive(row);
            }

            return Task.FromResult(archivedRows.Count);
        }
    }

    private static bool IsCompleted(string? status) =>
        !string.IsNullOrEmpty(status) &&
        status.IndexOf("x", StringComparison.OrdinalIgnoreCase) >= 0;

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
                return parsed is not null && MatchesUpdate(parsed.Value, request);
            });

            if (index < 0)
            {
                return Task.CompletedTask;
            }

            var parsed = ParseRow(lines[index])!.Value;
            var currentTags = string.IsNullOrWhiteSpace(parsed.Tags)
                ? new List<string>()
                : parsed.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var requestedTags = request.NewTags == null
                ? currentTags
                : request.NewTags;
            // Тег "Ежегодно" нельзя снять через редактирование.
            var newTags = string.Join(", ",
                YearlyTaskRules.ApplyEditTagPolicy(currentTags, requestedTags).Select(Escape));
            if (!YearlyTaskRules.HasYearlyTag(newTags.Split(',', StringSplitOptions.TrimEntries)))
            {
                newTags = string.Join(", ", YearlyTaskRules.EnsureYearlyTag(
                    newTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Select(Escape));
            }

            var newTask = request.NewTask ?? parsed.Task;
            var newAssignee = request.NewAssignee ?? parsed.Assignee;
            var newComment = request.NewComment ?? parsed.Comment;
            var newDueDate = string.IsNullOrWhiteSpace(request.NewDueDate)
                ? parsed.DueDate
                : TaskNormalizer.NormalizeDueDate(request.NewDueDate);

            lines[index] = BuildRow(request.NewStatus, newTask, newDueDate, newAssignee, newTags, newComment);
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
        var hasDueDateColumn = false;

        foreach (var line in File.ReadAllLines(_filePath))
        {
            if (line.Contains("---", StringComparison.Ordinal))
            {
                pastHeader = true;
                var cols = line.Split('|', StringSplitOptions.RemoveEmptyEntries).Length;
                hasTagsColumn = cols >= 5;
                hasDueDateColumn = cols >= 6;
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
            string dueDate;
            List<string> tags;
            if (hasDueDateColumn)
            {
                dueDate = string.IsNullOrWhiteSpace(parsed.Value.DueDate)
                    ? TaskNormalizer.TodayString()
                    : parsed.Value.DueDate;
                comment = parsed.Value.Comment;
                tags = string.IsNullOrWhiteSpace(parsed.Value.Tags)
                    ? new List<string>()
                    : parsed.Value.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !LooksLikeDueDate(x)).ToList();
            }
            else if (hasTagsColumn)
            {
                dueDate = TaskNormalizer.TodayString();
                comment = parsed.Value.Comment;
                tags = string.IsNullOrWhiteSpace(parsed.Value.Tags)
                    ? new List<string>()
                    : parsed.Value.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !LooksLikeDueDate(x)).ToList();
            }
            else
            {
                dueDate = TaskNormalizer.TodayString();
                comment = parsed.Value.Tags;
                tags = new List<string>();
            }

            if (string.IsNullOrWhiteSpace(task))
            {
                continue;
            }

            // Ежегодные задачи всегда с тегом "Ежегодно".
            if (!YearlyTaskRules.HasYearlyTag(tags))
            {
                tags = YearlyTaskRules.EnsureYearlyTag(tags);
            }

            tasks.Add(new OneTimeTask(status, Unescape(task), Unescape(assignee), Unescape(comment), tags, dueDate));
        }

        return Task.FromResult<IReadOnlyList<OneTimeTask>>(tasks);
    }

    private static (string Status, string Task, string Assignee, string Tags, string DueDate, string Comment)? ParseRow(string line)
    {
        if (!line.StartsWith("|", StringComparison.Ordinal))
        {
            return null;
        }

        var cells = line.Split('|');
        if (cells.Length >= 8)
        {
            var third = cells[3].Trim();
            var fifth = cells[5].Trim();
            if (!LooksLikeDueDate(third) && LooksLikeDueDate(fifth))
            {
                return (
                    Status: cells[1].Trim(),
                    Task: cells[2].Trim(),
                    Assignee: cells[3].Trim(),
                    Tags: cells[4].Trim(),
                    DueDate: cells[5].Trim(),
                    Comment: string.Join("|", cells.Skip(6).Take(cells.Length - 7)).Trim());
            }

            return (
                Status: cells[1].Trim(),
                Task: cells[2].Trim(),
                Assignee: cells[4].Trim(),
                Tags: cells[5].Trim(),
                DueDate: cells[3].Trim(),
                Comment: string.Join("|", cells.Skip(6).Take(cells.Length - 7)).Trim());
        }

        if (cells.Length >= 7)
        {
            return (
                Status: cells[1].Trim(),
                Task: cells[2].Trim(),
                Assignee: cells[3].Trim(),
                Tags: cells[4].Trim(),
                DueDate: string.Empty,
                Comment: string.Join("|", cells.Skip(5).Take(cells.Length - 6)).Trim());
        }

        if (cells.Length >= 6)
        {
            return (
                Status: cells[1].Trim(),
                Task: cells[2].Trim(),
                Assignee: cells[3].Trim(),
                Tags: string.Empty,
                DueDate: string.Empty,
                Comment: string.Join("|", cells.Skip(4).Take(cells.Length - 5)).Trim());
        }

        return null;
    }

    private static string CleanTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return string.Empty;
        }

        var parts = tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p) && !LooksLikeDueDate(p))
            .ToList();

        return string.Join(", ", parts);
    }

    private static bool LooksLikeDueDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim().Replace('T', ' ');
        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d{4}-\d{2}-\d{2}([ ]\d{2}:\d{2}(:\d{2})?)?$")
            || (trimmed.Contains('-') && DateTime.TryParse(trimmed, out _));
    }

    private static bool MatchesDelete(
        (string Status, string Task, string Assignee, string Tags, string DueDate, string Comment) parsed,
        DeleteTaskRequest request)
    {
        if (parsed.Status != request.Status.Trim() ||
            parsed.Task != Escape(request.Task).Trim() ||
            parsed.Assignee != Escape(request.Assignee).Trim() ||
            parsed.Comment != Escape(request.Comment).Trim())
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.DueDate) &&
            TaskNormalizer.NormalizeDueDate(parsed.DueDate) != TaskNormalizer.NormalizeDueDate(request.DueDate))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesUpdate(
        (string Status, string Task, string Assignee, string Tags, string DueDate, string Comment) parsed,
        UpdateTaskRequest request)
    {
        if (parsed.Status != request.Status.Trim() ||
            parsed.Task != Escape(request.Task).Trim() ||
            parsed.Assignee != Escape(request.Assignee).Trim() ||
            parsed.Comment != Escape(request.Comment).Trim())
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.DueDate) &&
            TaskNormalizer.NormalizeDueDate(parsed.DueDate) != TaskNormalizer.NormalizeDueDate(request.DueDate))
        {
            return false;
        }

        return true;
    }

    private void MigrateSchema()
    {
        MigrateFile(_filePath);
    }

    private static void MigrateFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var lines = File.ReadAllLines(filePath);
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
                if (line.Trim() != HeaderRow)
                {
                    lines[i] = HeaderRow;
                    migrated = true;
                }
                continue;
            }

            if (line.Contains("---", StringComparison.Ordinal))
            {
                if (line.Trim() != SeparatorRow)
                {
                    lines[i] = SeparatorRow;
                    migrated = true;
                }
                continue;
            }

            var cols = CountDataColumns(line);
            if (cols == 4)
            {
                var cells = line.Split('|').ToList();
                cells.Insert(3, $" {TaskNormalizer.TodayString()} ");
                cells.Insert(5, " ");
                lines[i] = string.Join("|", cells);
                migrated = true;
            }
            else if (cols == 5)
            {
                var cells = line.Split('|').ToList();
                cells.Insert(3, $" {TaskNormalizer.TodayString()} ");
                lines[i] = string.Join("|", cells);
                migrated = true;
            }
            else if (cols >= 6)
            {
                var cells = line.Split('|').ToList();
                var third = cells[3].Trim();
                var fifth = cells[5].Trim();

                if (!LooksLikeDueDate(third) && (LooksLikeDueDate(fifth) || string.IsNullOrWhiteSpace(third)))
                {
                    var dueRaw = cells[5];
                    cells.RemoveAt(5);
                    var normalized = TaskNormalizer.NormalizeDueDate(dueRaw.Trim());
                    cells.Insert(3, $" {normalized} ");
                    cells[5] = $" {CleanTags(cells[5])} ";
                    lines[i] = string.Join("|", cells);
                    migrated = true;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(third))
                    {
                        cells[3] = $" {TaskNormalizer.TodayString()} ";
                        lines[i] = string.Join("|", cells);
                        migrated = true;
                    }
                    else
                    {
                        var normalized = TaskNormalizer.NormalizeDueDate(third);
                        if (normalized != third)
                        {
                            cells[3] = $" {normalized} ";
                            lines[i] = string.Join("|", cells);
                            migrated = true;
                        }
                    }

                    var cleanedTags = CleanTags(cells[5]);
                    if (cells[5].Trim() != cleanedTags)
                    {
                        cells[5] = $" {cleanedTags} ";
                        lines[i] = string.Join("|", cells);
                        migrated = true;
                    }
                }
            }
        }

        if (migrated)
        {
            File.WriteAllLines(filePath, lines);
        }
    }

    private static int CountDataColumns(string line)
    {
        var cells = line.Split('|');
        return Math.Max(0, cells.Length - 2);
    }

    private void AppendToArchive(string row)
    {
        var directory = Path.GetDirectoryName(_archivePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(_archivePath))
        {
            var header =
                "# Архив ежегодных задач" + Environment.NewLine + Environment.NewLine +
                HeaderRow + Environment.NewLine +
                SeparatorRow + Environment.NewLine;

            File.WriteAllText(_archivePath, header);
        }
        else
        {
            MigrateFile(_archivePath);
        }

        EnsureTrailingNewline(_archivePath);
        File.AppendAllText(_archivePath, row + Environment.NewLine);
    }

    private static void EnsureTrailingNewline(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var content = File.ReadAllText(filePath);
        if (content.Length > 0 && !content.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            File.AppendAllText(filePath, Environment.NewLine);
        }
    }

    private static string BuildRow(string status, string task, string? dueDate, string assignee, string tags, string? comment) =>
        $"| {status} | {Escape(task)} | {Escape(TaskNormalizer.NormalizeDueDate(dueDate))} | {Escape(assignee)} | {tags} | {Escape(comment)} |";

    private static string Escape(string? value) =>
        (value ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\n", "\\n")
            .Replace("|", "\\|")
            .Trim();

    private static string Unescape(string? value) =>
        (value ?? string.Empty).Replace("\\n", "\n");
}
