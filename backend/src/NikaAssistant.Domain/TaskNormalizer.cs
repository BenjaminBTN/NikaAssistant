using System.Globalization;

namespace NikaAssistant.Domain;

// Доменные правила нормализации задач. Живут в Domain, чтобы Application
// и Infrastructure пользовались одной реализацией без зависимости
// Application -> Infrastructure.
public static class TaskNormalizer
{
    public static string TodayString() =>
        DateTime.Today.AddHours(19).ToString("yyyy-MM-dd HH:mm");

    public static string NormalizeTaskTitle(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return char.ToUpper(trimmed[0], CultureInfo.GetCultureInfo("ru-RU")) + trimmed.Substring(1);
    }

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
