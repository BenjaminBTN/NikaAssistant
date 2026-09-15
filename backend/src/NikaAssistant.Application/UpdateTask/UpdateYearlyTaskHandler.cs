using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.UpdateTask;

public sealed class UpdateYearlyTaskHandler
{
    private readonly IYearlyTaskStorage _storage;

    public UpdateYearlyTaskHandler(IYearlyTaskStorage storage)
    {
        _storage = storage;
    }

    public Task UpdateTaskAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) =>
        _storage.UpdateAsync(request, cancellationToken);
}
