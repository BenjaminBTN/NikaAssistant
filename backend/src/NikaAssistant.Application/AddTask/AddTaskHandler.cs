using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.CreateTask;

public sealed class AddTaskHandler
{
    private readonly IOneTimeTaskStorage _storage;
    private readonly IMonthlyTaskStorage _monthly;
    private readonly IYearlyTaskStorage _yearly;

    public AddTaskHandler(IOneTimeTaskStorage storage, IMonthlyTaskStorage monthly, IYearlyTaskStorage yearly)
    {
        _storage = storage;
        _monthly = monthly;
        _yearly = yearly;
    }

    public Task AddTaskAsync(AddTaskRequest request, CancellationToken cancellationToken = default)
    {
        // Теги "Ежемесячно"/"Ежегодно" доступны только при создании: такая задача хранится
        // в monthly-tasks.md / yearly-tasks.md, а не в one-time.
        if (Domain.MonthlyTaskRules.HasMonthlyTag(request.Tags))
        {
            return _monthly.AddAsync(request, cancellationToken);
        }

        if (Domain.YearlyTaskRules.HasYearlyTag(request.Tags))
        {
            return _yearly.AddAsync(request, cancellationToken);
        }

        return _storage.AddAsync(request, cancellationToken);
    }

    public bool IsMonthly(AddTaskRequest request) =>
        Domain.MonthlyTaskRules.HasMonthlyTag(request.Tags);

    public bool IsYearly(AddTaskRequest request) =>
        Domain.YearlyTaskRules.HasYearlyTag(request.Tags);
}
