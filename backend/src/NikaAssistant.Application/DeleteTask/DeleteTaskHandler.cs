using NikaAssistant.Contracts;
using NikaAssistant.Infrastructure.LocalStorage;

namespace NikaAssistant.Application.DeleteTask;

public sealed class DeleteTaskHandler
{
    private readonly IOneTimeTaskStorage _storage;

    public DeleteTaskHandler(IOneTimeTaskStorage storage)
    {
        _storage = storage;
    }

    public Task DeleteTaskAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) =>
        _storage.DeleteAsync(request, cancellationToken);
}
