using NikaAssistant.Contracts;
using NikaAssistant.Infrastructure.LocalStorage;

namespace NikaAssistant.Application.UpdateTask;

public sealed class UpdateTaskHandler
{
    private readonly IOneTimeTaskStorage _storage;

    public UpdateTaskHandler(IOneTimeTaskStorage storage)
    {
        _storage = storage;
    }

    public Task UpdateTaskAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) =>
        _storage.UpdateAsync(request, cancellationToken);
}
