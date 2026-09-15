using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;
using NikaAssistant.Domain;

namespace NikaAssistant.Application.Yearly;

public sealed class YearlyRolloverService
{
    private readonly IYearlyTaskStorage _yearly;
    private readonly IOneTimeTaskStorage _oneTime;

    public YearlyRolloverService(IYearlyTaskStorage yearly, IOneTimeTaskStorage oneTime)
    {
        _yearly = yearly;
        _oneTime = oneTime;
    }

    // Канун ежегодной задачи (срок — завтра):
    // 1) копируем её в one-time (там она попадёт в "Завтра") с тегом "Ежегодно";
    // 2) саму ежегодную сдвигаем на будущий год.
    public async Task<int> RolloverDueAsync(CancellationToken cancellationToken = default)
    {
        var yearlyTasks = await _yearly.GetAllAsync(cancellationToken);
        if (yearlyTasks.Count == 0)
        {
            return 0;
        }

        var oneTimeTasks = await _oneTime.GetAllAsync(cancellationToken);
        var rolled = 0;

        foreach (var yearly in yearlyTasks)
        {
            if (YearlyTaskRules.IsCompleted(yearly.Status))
            {
                continue;
            }

            if (!YearlyTaskRules.IsDueTomorrowOrOverdue(yearly.DueDate))
            {
                continue;
            }

            var originalDue = TaskNormalizer.NormalizeDueDate(yearly.DueDate);

            // Идемпотентность: если копия уже есть в one-time — только сдвигаем ежегодную.
            var alreadyCopied = oneTimeTasks.Any(t =>
                t.Task == yearly.Task &&
                YearlyTaskRules.HasYearlyTag(t.Tags) &&
                TaskNormalizer.NormalizeDueDate(t.DueDate) == originalDue);

            if (!alreadyCopied)
            {
                await _oneTime.AddAsync(new AddTaskRequest(
                    yearly.Task,
                    yearly.Assignee,
                    yearly.Comment,
                    YearlyTaskRules.EnsureYearlyTag(yearly.Tags),
                    originalDue), cancellationToken);
            }

            var nextDue = YearlyTaskRules.NextYearlyDueDate(originalDue);
            await _yearly.UpdateAsync(new UpdateTaskRequest(
                yearly.Status,
                yearly.Task,
                yearly.Assignee,
                yearly.Comment,
                yearly.Status,
                YearlyTaskRules.EnsureYearlyTag(yearly.Tags),
                yearly.Comment,
                yearly.Task,
                yearly.Assignee,
                originalDue,
                nextDue), cancellationToken);

            rolled++;
        }

        return rolled;
    }
}
