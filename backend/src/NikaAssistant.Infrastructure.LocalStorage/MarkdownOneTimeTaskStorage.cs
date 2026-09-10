using Microsoft.Extensions.Configuration;
using NikaAssistant.Contracts;

namespace NikaAssistant.Infrastructure.LocalStorage;

public sealed class MarkdownOneTimeTaskStorage : IOneTimeTaskStorage
{
    private const string DefaultFilePath = @"C:\Users\galki\Storage\Tasks\OneTime\one-time-tasks.md";
    private const string DefaultArchivePath = @"C:\Users\galki\Storage\Tasks\Archive\archive-tasks.md";
    private static readonly object Sync = new();

    // Актуальный порядок столбцов: Статус | Задача | Срок | Ответственный | Теги | Комментарий
    private const string HeaderRow = "| Статус | Задача | Срок | Ответственный | Теги | Комментарий |";
    private const string SeparatorRow = "| --- | --- | --- | --- | --- | --- |";

    private readonly string _filePath;
    private readonly string _archivePath;
    private readonly string? _defaultAssignee;

    public MarkdownOneTimeTaskStorage(IConfiguration configuration)
    {
        _filePath = configuration["Storage:OneTimeTasksPath"] ?? DefaultFilePath;
        _archivePath = configuration["Storage:ArchivePath"] ?? DefaultArchivePath;
        _defaultAssignee = configuration["Storage:DefaultAssignee"];
    }

    public string ResolveAssignee(string? assignee) =>
        string.IsNullOrWhiteSpace(assignee) ? _defaultAssignee ?? string.Empty : assignee;

    public Task AddAsync(AddTaskRequest request, CancellationToken cancellationToken = default)
    {
        var assignee = ResolveAssignee(request.Assignee);
        var tags = request.Tags == null ? "" : string.Join(", ", request.Tags.Select(Escape));
        var dueDate = NormalizeDueDate(request.DueDate);
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
                    "# Разовые задачи" + Environment.NewLine + Environment.NewLine +
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
            var newTags = request.NewTags == null
                ? CleanTags(parsed.Tags)
                : string.Join(", ", request.NewTags.Select(Escape));

            var newTask = request.NewTask ?? parsed.Task;
            var newAssignee = request.NewAssignee ?? parsed.Assignee;
            var newComment = request.NewComment ?? parsed.Comment;
            var newDueDate = string.IsNullOrWhiteSpace(request.NewDueDate)
                ? parsed.DueDate
                : NormalizeDueDate(request.NewDueDate);

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
                    ? TodayString()
                    : parsed.Value.DueDate;
                comment = parsed.Value.Comment;
                tags = string.IsNullOrWhiteSpace(parsed.Value.Tags)
                    ? new List<string>()
                    : parsed.Value.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !LooksLikeDueDate(x)).ToList();
            }
            else if (hasTagsColumn)
            {
                dueDate = TodayString();
                comment = parsed.Value.Comment;
                tags = string.IsNullOrWhiteSpace(parsed.Value.Tags)
                    ? new List<string>()
                    : parsed.Value.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !LooksLikeDueDate(x)).ToList();
            }
            else
            {
                dueDate = TodayString();
                comment = parsed.Value.Tags;
                tags = new List<string>();
            }

            if (string.IsNullOrWhiteSpace(task))
            {
                continue;
            }

            tasks.Add(new OneTimeTask(status, task, assignee, comment, tags, dueDate));
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
        // Актуальные 6 колонок: | Статус | Задача | Срок | Ответственный | Теги | Комментарий |
        // Плюс поддержка предыдущего порядка: | Статус | Задача | Ответственный | Теги | Срок | Комментарий |
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

        // Legacy 5 columns: | Статус | Задача | Ответственный | Теги | Комментарий |
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

        // Legacy 4 columns (no tags)
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

    // Убирает из строки тегов фрагменты, похожие на дату (мусор от старой версии,
    // которая писала срок в колонку тегов, когда тегов не было).
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

        // DueDate проверяем только если он передан (обратная совместимость).
        if (!string.IsNullOrWhiteSpace(request.DueDate) &&
            NormalizeDueDate(parsed.DueDate) != NormalizeDueDate(request.DueDate))
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
            NormalizeDueDate(parsed.DueDate) != NormalizeDueDate(request.DueDate))
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
                // Старая схема без тегов: | Статус | Задача | Ответственный | Комментарий |
                // -> | Статус | Задача | Срок(сегодня) | Ответственный | Теги(пусто) | Комментарий |
                var cells = line.Split('|').ToList();
                cells.Insert(3, $" {TodayString()} ");
                cells.Insert(5, " ");
                lines[i] = string.Join("|", cells);
                migrated = true;
            }
            else if (cols == 5)
            {
                // Старая схема без срока: | Статус | Задача | Ответственный | Теги | Комментарий |
                // -> вставляем сегодняшний Срок после Задачи.
                var cells = line.Split('|').ToList();
                cells.Insert(3, $" {TodayString()} ");
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
                    // Предыдущий порядок: | Статус | Задача | Ответственный | Теги | Срок | Комментарий |
                    // -> переставляем Срок на 3-ю позицию.
                    var dueRaw = cells[5];
                    cells.RemoveAt(5);
                    var normalized = NormalizeDueDate(dueRaw.Trim());
                    cells.Insert(3, $" {normalized} ");
                    // Заодно вычищаем мусор из Тегов (туда могла попасть дата).
                    cells[5] = $" {CleanTags(cells[5])} ";
                    lines[i] = string.Join("|", cells);
                    migrated = true;
                }
                else
                {
                    // Уже новый порядок: | Статус | Задача | Срок | Ответственный | Теги | Комментарий |
                    // Заполняем пустой Срок сегодняшней датой, нормализуем формат.
                    if (string.IsNullOrWhiteSpace(third))
                    {
                        cells[3] = $" {TodayString()} ";
                        lines[i] = string.Join("|", cells);
                        migrated = true;
                    }
                    else
                    {
                        var normalized = NormalizeDueDate(third);
                        if (normalized != third)
                        {
                            cells[3] = $" {normalized} ";
                            lines[i] = string.Join("|", cells);
                            migrated = true;
                        }
                    }

                    // Вычищаем мусор из Тегов: туда могла попасть дата
                    // (старая версия писала срок в колонку тегов, когда тегов не было).
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
                "# Архив задач" + Environment.NewLine + Environment.NewLine +
                HeaderRow + Environment.NewLine +
                SeparatorRow + Environment.NewLine;

            File.WriteAllText(_archivePath, header);
        }
        else
        {
            // Архив уже в новом порядке (строка приходит из мигрированного файла),
            // но старый архивный файл мог остаться в предыдущем порядке — нормализуем.
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
        $"| {status} | {Escape(task)} | {Escape(NormalizeDueDate(dueDate))} | {Escape(assignee)} | {tags} | {Escape(comment)} |";

    private static string Escape(string? value) =>
        (value ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", " ")
            .Replace("|", "\\|")
            .Trim();

    private static string TodayString() =>
        DateTime.Today.AddHours(19).ToString("yyyy-MM-dd HH:mm");

    public static string NormalizeDueDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TodayString();
        }

        var trimmed = value.Trim().Replace('T', ' ');
        if (DateTime.TryParse(trimmed, out var dt))
        {
            // Если в строке нет времени (только дата) — ставим 19:00.
            if (!trimmed.Contains(':'))
            {
                return dt.Date.AddHours(19).ToString("yyyy-MM-dd HH:mm");
            }

            return dt.ToString("yyyy-MM-dd HH:mm");
        }

        return trimmed;
    }
}
