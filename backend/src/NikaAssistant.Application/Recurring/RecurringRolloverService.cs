using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;
using NikaAssistant.Domain;

namespace NikaAssistant.Application.Recurring;

// Единый сервис ролловера периодических задач (замена Monthly/YearlyRolloverService).
// Канун задачи с тегом "Раз в <N> <единица>" (срок — завтра или просрочена):
// 1) копируем её в one-time (там она попадёт в "Завтра") с тем же тегом периода;
// 2) саму периодическую сдвигаем вперёд на её интервал.
public sealed class RecurringRolloverService
{
    private readonly IRecurringTaskStorage _recurring;
    private readonly IOneTimeTaskStorage _oneTime;

    public RecurringRolloverService(IRecurringTaskStorage recurring, IOneTimeTaskStorage oneTime)
    {
        _recurring = recurring;
        _oneTime = oneTime;
    }

    public async Task<int> RolloverDueAsync(CancellationToken cancellationToken = default)
    {
        var recurringTasks = await _recurring.GetAllAsync(cancellationToken);
        if (recurringTasks.Count == 0)
        {
            return 0;
        }

        var oneTimeTasks = await _oneTime.GetAllAsync(cancellationToken);
        var rolled = 0;

        foreach (var recurring in recurringTasks)
        {
            if (RecurringTaskRules.IsCompleted(recurring.Status))
            {
                continue;
            }

            if (!RecurringTaskRules.IsDueTomorrowOrOverdue(recurring.DueDate))
            {
                continue;
            }

            // Интервал берём из тега; задачи без распознаваемого периода пропускаем.
            if (!RecurringTaskRules.TryGetPeriod(recurring.Tags, out var amount, out var unit))
            {
                if (RecurringTaskRules.HasLegacyMonthlyTag(recurring.Tags))
                {
                    amount = 1;
                    unit = RecurringPeriodUnit.Months;
                }
                else if (RecurringTaskRules.HasLegacyYearlyTag(recurring.Tags))
                {
                    amount = 1;
                    unit = RecurringPeriodUnit.Years;
                }
                else
                {
                    continue;
                }
            }

            var originalDue = TaskNormalizer.NormalizeDueDate(recurring.DueDate);

            // Идемпотентность: если копия уже есть в one-time — только сдвигаем периодическую.
            // Период сравниваем по значению, а не по строке тега — переживает смену формата.
            var alreadyCopied = oneTimeTasks.Any(t =>
                t.Task == recurring.Task &&
                TaskNormalizer.NormalizeDueDate(t.DueDate) == originalDue &&
                HasSamePeriod(t.Tags, amount, unit));

            if (!alreadyCopied)
            {
                await _oneTime.AddAsync(new AddTaskRequest(
                    recurring.Task,
                    recurring.Assignee,
                    recurring.Comment,
                    RecurringTaskRules.EnsurePeriodTag(recurring.Tags, amount, unit),
                    originalDue), cancellationToken);
            }

            var nextDue = RecurringTaskRules.NextDueDate(originalDue, amount, unit);
            await _recurring.UpdateAsync(new UpdateTaskRequest(
                recurring.Status,
                recurring.Task,
                recurring.Assignee,
                recurring.Comment,
                recurring.Status,
                RecurringTaskRules.EnsurePeriodTag(recurring.Tags, amount, unit),
                recurring.Comment,
                recurring.Task,
                recurring.Assignee,
                originalDue,
                nextDue), cancellationToken);

            rolled++;
        }

        return rolled;
    }

    // Копия в one-time считается той же самой, если её интервал совпадает
    // (сравнение по распарсенному периоду, а не по строке — переживает смену
    // формата тега; устаревшие "Ежемесячно"/"Ежегодно" эквивалентны 1 мес./1 году).
    private static bool HasSamePeriod(IEnumerable<string> tags, int amount, RecurringPeriodUnit unit)
    {
        if (RecurringTaskRules.TryGetPeriod(tags, out var copyAmount, out var copyUnit))
        {
            return copyAmount == amount && copyUnit == unit;
        }

        if (amount != 1)
        {
            return false;
        }

        return unit switch
        {
            RecurringPeriodUnit.Months => RecurringTaskRules.HasLegacyMonthlyTag(tags),
            RecurringPeriodUnit.Years => RecurringTaskRules.HasLegacyYearlyTag(tags),
            _ => false,
        };
    }
}
