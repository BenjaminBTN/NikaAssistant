using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.DeleteTask;

public sealed class DeleteYearlyTaskHandler
{
    private readonly IYearlyTaskStorage _storage;

    public DeleteYearlyTaskHandler(IYearlyTaskStorage storage)
    {
        _storage = storage;
    }

    public Task DeleteTaskAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) =>
        _storage.DeleteAsync(request, cancellationToken);
}
