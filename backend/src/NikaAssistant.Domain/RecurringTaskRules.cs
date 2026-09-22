using System.Text.RegularExpressions;

namespace NikaAssistant.Domain;

// Единые правила повторяемых (периодических) задач.
// Стандартный тег: "Раз в <N> <единица>", например:
// "Раз в 14 дней", "Раз в 6 месяцев", "Раз в 2 месяца",
// "Раз в 1 год", "Раз в 2 года", "Раз в 5 лет".
public enum RecurringPeriodUnit
{
    Days,
    Months,
    Years,
}

public static class RecurringTaskRules
{
    // Устаревшие теги (до объединения monthly/yearly и старого формата "Период - ...").
    // Понимаются для обратной совместимости и мигрируются в стандартный тег "Раз в ...".
    public const string LegacyMonthlyTag = "Ежемесячно";
    public const string LegacyYearlyTag = "Ежегодно";

    private static readonly Regex PeriodTagRegex = new(
        @"^Раз\s+в\s+(\d+)\s*(день|дня|дней|месяц|месяца|месяцев|год|года|лет)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Предыдущий формат ("Период - 14 дней"): только чтение и миграция, новое не создаём.
    private static readonly Regex OldPeriodTagRegex = new(
        @"^Период\s*-\s*(\d+)\s*(день|дня|дней|месяц|месяца|месяцев|год|года|лет)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string BuildPeriodTag(int amount, RecurringPeriodUnit unit)
    {
        if (amount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Период должен быть не меньше 1.");
        }

        return $"Раз в {amount} {Pluralize(amount, unit)}";
    }

    public static string Pluralize(int amount, RecurringPeriodUnit unit)
    {
        // Русская плюрализация: 1 / 2-4 / остальные (с исключением 11-14).
        var mod100 = amount % 100;
        var mod10 = amount % 10;
        var form = (mod100 is >= 11 and <= 14) ? 2 : mod10 == 1 ? 0 : mod10 is >= 2 and <= 4 ? 1 : 2;

        return unit switch
        {
            RecurringPeriodUnit.Days => form == 0 ? "день" : form == 1 ? "дня" : "дней",
            RecurringPeriodUnit.Months => form == 0 ? "месяц" : form == 1 ? "месяца" : "месяцев",
            RecurringPeriodUnit.Years => form == 0 ? "год" : form == 1 ? "года" : "лет",
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };
    }

    public static bool TryGetPeriod(IEnumerable<string>? tags, out int amount, out RecurringPeriodUnit unit)
    {
        amount = 0;
        unit = RecurringPeriodUnit.Months;

        if (tags is null)
        {
            return false;
        }

        var cleaned = CleanTagList(tags);

        // Новый формат приоритетнее: если рядом затесался и старый — побеждает новый.
        foreach (var tag in cleaned)
        {
            if (TryParsePeriod(tag, PeriodTagRegex, out amount, out unit))
            {
                return true;
            }
        }

        foreach (var tag in cleaned)
        {
            if (TryParsePeriod(tag, OldPeriodTagRegex, out amount, out unit))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParsePeriod(string tag, Regex regex, out int amount, out RecurringPeriodUnit unit)
    {
        amount = 0;
        unit = RecurringPeriodUnit.Months;

        var match = regex.Match(tag);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, out var parsed) || parsed < 1)
        {
            return false;
        }

        unit = NormalizeUnitWord(match.Groups[2].Value);
        amount = parsed;
        return true;
    }

    public static bool HasPeriodTag(IEnumerable<string>? tags) =>
        TryGetPeriod(tags, out _, out _);

    public static bool HasLegacyMonthlyTag(IEnumerable<string>? tags) =>
        tags?.Any(t => t.Equals(LegacyMonthlyTag, StringComparison.OrdinalIgnoreCase)) ?? false;

    public static bool HasLegacyYearlyTag(IEnumerable<string>? tags) =>
        tags?.Any(t => t.Equals(LegacyYearlyTag, StringComparison.OrdinalIgnoreCase)) ?? false;

    // Задача считается повторяемой, если есть новый тег "Раз в ..."
    // либо один из устаревших ("Ежемесячно"/"Ежегодно"/"Период - ...").
    public static bool IsRecurring(IEnumerable<string>? tags) =>
        HasPeriodTag(tags) || HasLegacyMonthlyTag(tags) || HasLegacyYearlyTag(tags);

    // Заменяет устаревшие теги на стандартный "Раз в ...".
    // Если список уже каноничен — возвращается как есть, без переупорядочивания.
    public static List<string> MigrateLegacyTags(IEnumerable<string>? tags)
    {
        var list = CleanTagList(tags);

        if (!TryGetPeriod(list, out var amount, out var unit))
        {
            var hasMonthly = list.Any(t => t.Equals(LegacyMonthlyTag, StringComparison.OrdinalIgnoreCase));
            var hasYearly = list.Any(t => t.Equals(LegacyYearlyTag, StringComparison.OrdinalIgnoreCase));
            if (!hasMonthly && !hasYearly)
            {
                return list;
            }

            var result = list.Where(t =>
                !t.Equals(LegacyMonthlyTag, StringComparison.OrdinalIgnoreCase) &&
                !t.Equals(LegacyYearlyTag, StringComparison.OrdinalIgnoreCase)).ToList();

            if (hasMonthly)
            {
                result.Add(BuildPeriodTag(1, RecurringPeriodUnit.Months));
            }
            else
            {
                result.Add(BuildPeriodTag(1, RecurringPeriodUnit.Years));
            }

            return result;
        }

        var canonical = BuildPeriodTag(amount, unit);
        var needsMigration = list.Any(t =>
            t.Equals(LegacyMonthlyTag, StringComparison.OrdinalIgnoreCase) ||
            t.Equals(LegacyYearlyTag, StringComparison.OrdinalIgnoreCase) ||
            OldPeriodTagRegex.IsMatch(t) ||
            (PeriodTagRegex.IsMatch(t) && !t.Equals(canonical, StringComparison.Ordinal)));
        if (!needsMigration)
        {
            return list;
        }

        var migrated = list.Where(t =>
            !IsPeriodTag(t) &&
            !t.Equals(LegacyMonthlyTag, StringComparison.OrdinalIgnoreCase) &&
            !t.Equals(LegacyYearlyTag, StringComparison.OrdinalIgnoreCase)).ToList();
        migrated.Add(canonical);
        return migrated;
    }

    // Гарантирует ровно один стандартный тег периода (плюс миграция legacy).
    public static List<string> EnsurePeriodTag(IEnumerable<string>? tags, int amount, RecurringPeriodUnit unit)
    {
        var list = CleanTagList(tags)
            .Where(t => !IsPeriodTag(t) &&
                !t.Equals(LegacyMonthlyTag, StringComparison.OrdinalIgnoreCase) &&
                !t.Equals(LegacyYearlyTag, StringComparison.OrdinalIgnoreCase))
            .ToList();
        list.Add(BuildPeriodTag(amount, unit));
        return list;
    }

    public static bool IsPeriodTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var trimmed = tag.Trim();
        return PeriodTagRegex.IsMatch(trimmed) || OldPeriodTagRegex.IsMatch(trimmed);
    }

    // Тег периода нельзя добавить, снять или изменить через редактирование:
    // - если был (включая legacy) — принудительно сохраняем исходный интервал;
    // - если не было — вырезаем попытку добавить.
    public static List<string> ApplyEditTagPolicy(IReadOnlyList<string> currentTags, IEnumerable<string>? requestedTags)
    {
        var current = CleanTagList(currentTags);
        var requested = CleanTagList(requestedTags)
            .Where(t => !IsPeriodTag(t) &&
                !t.Equals(LegacyMonthlyTag, StringComparison.OrdinalIgnoreCase) &&
                !t.Equals(LegacyYearlyTag, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (TryGetPeriod(current, out var amount, out var unit))
        {
            requested.Add(BuildPeriodTag(amount, unit));
            return requested;
        }

        if (current.Any(t => t.Equals(LegacyMonthlyTag, StringComparison.OrdinalIgnoreCase)))
        {
            requested.Add(BuildPeriodTag(1, RecurringPeriodUnit.Months));
            return requested;
        }

        if (current.Any(t => t.Equals(LegacyYearlyTag, StringComparison.OrdinalIgnoreCase)))
        {
            requested.Add(BuildPeriodTag(1, RecurringPeriodUnit.Years));
            return requested;
        }

        return requested;
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

    // Просроченные повторяемые тоже считаем нужными ролловера
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

    public static string NextDueDate(string? currentDueDate, int amount, RecurringPeriodUnit unit, DateTime? now = null)
    {
        if (amount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Период должен быть не меньше 1.");
        }

        var reference = now ?? DateTime.Now;
        var tomorrow = reference.Date.AddDays(1);
        var due = ParseDueDate(currentDueDate) ?? reference.Date.AddHours(19);

        // Сохраняем время (обычно 19:00), сдвигаем на период, пока дата не уйдёт в будущее.
        var next = due;
        do
        {
            next = unit switch
            {
                RecurringPeriodUnit.Days => next.AddDays(amount),
                RecurringPeriodUnit.Months => next.AddMonths(amount),
                RecurringPeriodUnit.Years => next.AddYears(amount),
                _ => throw new ArgumentOutOfRangeException(nameof(unit)),
            };
        } while (next.Date <= tomorrow);

        return next.ToString("yyyy-MM-dd HH:mm");
    }

    // Следующая дата по тегам задачи (с учётом legacy). Бросает, если периода нет.
    public static string NextDueDateForTags(string? currentDueDate, IEnumerable<string>? tags, DateTime? now = null)
    {
        if (TryGetPeriod(tags, out var amount, out var unit))
        {
            return NextDueDate(currentDueDate, amount, unit, now);
        }

        if (HasLegacyMonthlyTag(tags))
        {
            return NextDueDate(currentDueDate, 1, RecurringPeriodUnit.Months, now);
        }

        if (HasLegacyYearlyTag(tags))
        {
            return NextDueDate(currentDueDate, 1, RecurringPeriodUnit.Years, now);
        }

        throw new InvalidOperationException("У задачи нет тега периода.");
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

    private static RecurringPeriodUnit NormalizeUnitWord(string word)
    {
        var lower = word.ToLowerInvariant();
        if (lower.StartsWith("ден") || lower.StartsWith("дн") || lower.StartsWith("day"))
        {
            return RecurringPeriodUnit.Days;
        }

        if (lower.StartsWith("мес") || lower.StartsWith("month"))
        {
            return RecurringPeriodUnit.Months;
        }

        return RecurringPeriodUnit.Years;
    }

    private static List<string> CleanTagList(IEnumerable<string>? tags) =>
        (tags ?? Enumerable.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();
}
