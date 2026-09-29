using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.CreateTask;

public sealed class AddTaskHandler
{
    private readonly IOneTimeTaskStorage _storage;
    private readonly IRecurringTaskStorage _recurring;

    public AddTaskHandler(IOneTimeTaskStorage storage, IRecurringTaskStorage recurring)
    {
        _storage = storage;
        _recurring = recurring;
    }

    public Task AddTaskAsync(AddTaskRequest request, CancellationToken cancellationToken = default)
    {
        // Тег "Раз в ..." (и legacy-варианты) доступен только при создании:
        // такая задача хранится в recurring-tasks.md, а не в one-time.
        if (Domain.RecurringTaskRules.IsRecurring(request.Tags))
        {
            return _recurring.AddAsync(request, cancellationToken);
        }

        return _storage.AddAsync(request, cancellationToken);
    }

    public bool IsRecurring(AddTaskRequest request) =>
        Domain.RecurringTaskRules.IsRecurring(request.Tags);
}
