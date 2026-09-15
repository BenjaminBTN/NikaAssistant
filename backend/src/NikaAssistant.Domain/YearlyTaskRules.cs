namespace NikaAssistant.Domain;

// Правила ежегодных задач: общий тег и сдвиг даты на будущий год.
public static class YearlyTaskRules
{
    public const string YearlyTag = "Ежегодно";

    public static bool HasYearlyTag(IEnumerable<string>? tags) =>
        tags?.Any(t => t.Equals(YearlyTag, StringComparison.OrdinalIgnoreCase)) ?? false;

    public static List<string> EnsureYearlyTag(IEnumerable<string>? tags)
    {
        var list = (tags ?? Enumerable.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();
        if (!list.Any(t => t.Equals(YearlyTag, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(YearlyTag);
        }

        return list;
    }

    // Тег "Ежегодно" нельзя добавить или снять через редактирование:
    // - если был — принудительно сохраняем;
    // - если не было — вырезаем попытку добавить.
    public static List<string> ApplyEditTagPolicy(IReadOnlyList<string> currentTags, IEnumerable<string>? requestedTags)
    {
        var hadYearly = HasYearlyTag(currentTags);
        var requested = (requestedTags ?? Enumerable.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();

        if (hadYearly)
        {
            if (!requested.Any(t => t.Equals(YearlyTag, StringComparison.OrdinalIgnoreCase)))
            {
                requested.Add(YearlyTag);
            }

            return requested;
        }

        return requested.Where(t => !t.Equals(YearlyTag, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static bool IsCompleted(string? status) =>
        !string.IsNullOrEmpty(status) &&
        status.IndexOf("x", StringComparison.OrdinalIgnoreCase) >= 0;

    // Канун: срок задачи — завтра (сравнение по дате, без времени).
    public static bool IsDueTomorrow(string? dueDate, DateTime? now = null)
    {
        var due = ParseDueDate(dueDate);
        if (due is null)
        {
            return false;
        }

        var reference = now ?? DateTime.Now;
        return due.Value.Date == reference.Date.AddDays(1);
    }

    // Просроченные ежегодные тоже считаем нужными ролловера
    // (приложение могли не открывать несколько дней).
    public static bool IsDueTomorrowOrOverdue(string? dueDate, DateTime? now = null)
    {
        var due = ParseDueDate(dueDate);
        if (due is null)
        {
            return false;
        }

        var reference = now ?? DateTime.Now;
        return due.Value.Date <= reference.Date.AddDays(1);
    }

    public static string NextYearlyDueDate(string? currentDueDate, DateTime? now = null)
    {
        var reference = now ?? DateTime.Now;
        var tomorrow = reference.Date.AddDays(1);
        var due = ParseDueDate(currentDueDate) ?? reference.Date.AddHours(19);

        // Сохраняем время (обычно 19:00), сдвигаем по годам, пока дата не уйдёт в будущее.
        var next = due;
        do
        {
            next = next.AddYears(1);
        } while (next.Date <= tomorrow);

        return next.ToString("yyyy-MM-dd HH:mm");
    }

    public static DateTime? ParseDueDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().Replace('T', ' ');
        return DateTime.TryParse(trimmed, out var dt) ? dt : null;
    }
}
