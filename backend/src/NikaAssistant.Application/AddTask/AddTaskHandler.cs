using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.CreateTask;

public sealed class AddTaskHandler
{
    private readonly IOneTimeTaskStorage _storage;
    private readonly IMonthlyTaskStorage _monthly;

    public AddTaskHandler(IOneTimeTaskStorage storage, IMonthlyTaskStorage monthly)
    {
        _storage = storage;
        _monthly = monthly;
    }

    public Task AddTaskAsync(AddTaskRequest request, CancellationToken cancellationToken = default)
    {
        // Тег "Ежемесячно" доступен только при создании: такая задача хранится
        // в monthly-tasks.md, а не в one-time.
        if (Domain.MonthlyTaskRules.HasMonthlyTag(request.Tags))
        {
            return _monthly.AddAsync(request, cancellationToken);
        }

        return _storage.AddAsync(request, cancellationToken);
    }

    public bool IsMonthly(AddTaskRequest request) =>
        Domain.MonthlyTaskRules.HasMonthlyTag(request.Tags);
}
