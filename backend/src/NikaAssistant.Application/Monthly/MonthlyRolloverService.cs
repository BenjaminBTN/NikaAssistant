using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;
using NikaAssistant.Domain;

namespace NikaAssistant.Application.Monthly;

public sealed class MonthlyRolloverService
{
    private readonly IMonthlyTaskStorage _monthly;
    private readonly IOneTimeTaskStorage _oneTime;

    public MonthlyRolloverService(IMonthlyTaskStorage monthly, IOneTimeTaskStorage oneTime)
    {
        _monthly = monthly;
        _oneTime = oneTime;
    }

    // Канун ежемесячной задачи (срок — завтра):
    // 1) копируем её в one-time (там она попадёт в "Завтра") с тегом "Ежемесячно";
    // 2) саму ежемесячную сдвигаем на будущий месяц.
    public async Task<int> RolloverDueAsync(CancellationToken cancellationToken = default)
    {
        var monthlyTasks = await _monthly.GetAllAsync(cancellationToken);
        if (monthlyTasks.Count == 0)
        {
            return 0;
        }

        var oneTimeTasks = await _oneTime.GetAllAsync(cancellationToken);
        var rolled = 0;

        foreach (var monthly in monthlyTasks)
        {
            if (MonthlyTaskRules.IsCompleted(monthly.Status))
            {
                continue;
            }

            if (!MonthlyTaskRules.IsDueTomorrowOrOverdue(monthly.DueDate))
            {
                continue;
            }

            var originalDue = TaskNormalizer.NormalizeDueDate(monthly.DueDate);

            // Идемпотентность: если копия уже есть в one-time — только сдвигаем ежемесячную.
            var alreadyCopied = oneTimeTasks.Any(t =>
                t.Task == monthly.Task &&
                MonthlyTaskRules.HasMonthlyTag(t.Tags) &&
                TaskNormalizer.NormalizeDueDate(t.DueDate) == originalDue);

            if (!alreadyCopied)
            {
                await _oneTime.AddAsync(new AddTaskRequest(
                    monthly.Task,
                    monthly.Assignee,
                    monthly.Comment,
                    MonthlyTaskRules.EnsureMonthlyTag(monthly.Tags),
                    originalDue), cancellationToken);
            }

            var nextDue = MonthlyTaskRules.NextMonthlyDueDate(originalDue);
            await _monthly.UpdateAsync(new UpdateTaskRequest(
                monthly.Status,
                monthly.Task,
                monthly.Assignee,
                monthly.Comment,
                monthly.Status,
                MonthlyTaskRules.EnsureMonthlyTag(monthly.Tags),
                monthly.Comment,
                monthly.Task,
                monthly.Assignee,
                originalDue,
                nextDue), cancellationToken);

            rolled++;
        }

        return rolled;
    }
}
