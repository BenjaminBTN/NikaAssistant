namespace NikaAssistant.Domain;

// Правила ежемесячных задач: общий тег и сдвиг даты на будущий месяц.
public static class MonthlyTaskRules
{
    public const string MonthlyTag = "Ежемесячно";

    public static bool HasMonthlyTag(IEnumerable<string>? tags) =>
        tags?.Any(t => t.Equals(MonthlyTag, StringComparison.OrdinalIgnoreCase)) ?? false;

    public static List<string> EnsureMonthlyTag(IEnumerable<string>? tags)
    {
        var list = (tags ?? Enumerable.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();
        if (!list.Any(t => t.Equals(MonthlyTag, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(MonthlyTag);
        }

        return list;
    }

    // Тег "Ежемесячно" нельзя добавить или снять через редактирование:
    // - если был — принудительно сохраняем;
    // - если не было — вырезаем попытку добавить.
    public static List<string> ApplyEditTagPolicy(IReadOnlyList<string> currentTags, IEnumerable<string>? requestedTags)
    {
        var hadMonthly = HasMonthlyTag(currentTags);
        var requested = (requestedTags ?? Enumerable.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();

        if (hadMonthly)
        {
            if (!requested.Any(t => t.Equals(MonthlyTag, StringComparison.OrdinalIgnoreCase)))
            {
                requested.Add(MonthlyTag);
            }

            return requested;
        }

        return requested.Where(t => !t.Equals(MonthlyTag, StringComparison.OrdinalIgnoreCase)).ToList();
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

    // Просроченные ежемесячные тоже считаем нужными ролловера
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

    public static string NextMonthlyDueDate(string? currentDueDate, DateTime? now = null)
    {
        var reference = now ?? DateTime.Now;
        var tomorrow = reference.Date.AddDays(1);
        var due = ParseDueDate(currentDueDate) ?? reference.Date.AddHours(19);

        // Сохраняем время (обычно 19:00), сдвигаем по месяцам, пока дата не уйдёт в будущее.
        var next = due;
        do
        {
            next = next.AddMonths(1);
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
