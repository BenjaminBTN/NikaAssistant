using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.DeleteTask;

public sealed class DeleteMonthlyTaskHandler
{
    private readonly IMonthlyTaskStorage _storage;

    public DeleteMonthlyTaskHandler(IMonthlyTaskStorage storage)
    {
        _storage = storage;
    }

    public Task DeleteTaskAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) =>
        _storage.DeleteAsync(request, cancellationToken);
}
