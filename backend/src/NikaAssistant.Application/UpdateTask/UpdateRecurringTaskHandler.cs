using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.UpdateTask;

public sealed class UpdateRecurringTaskHandler
{
    private readonly IRecurringTaskStorage _storage;

    public UpdateRecurringTaskHandler(IRecurringTaskStorage storage)
    {
        _storage = storage;
    }

    public Task UpdateTaskAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) =>
        _storage.UpdateAsync(request, cancellationToken);
}
