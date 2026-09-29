using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.DeleteTask;

public sealed class DeleteRecurringTaskHandler
{
    private readonly IRecurringTaskStorage _storage;

    public DeleteRecurringTaskHandler(IRecurringTaskStorage storage)
    {
        _storage = storage;
    }

    public Task DeleteTaskAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) =>
        _storage.DeleteAsync(request, cancellationToken);
}
