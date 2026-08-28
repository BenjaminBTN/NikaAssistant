using NikaAssistant.Contracts;
using NikaAssistant.Infrastructure.LocalStorage;

namespace NikaAssistant.Application.CreateTask;

public sealed class AddTaskHandler
{
    private readonly IOneTimeTaskStorage _storage;

    public AddTaskHandler(IOneTimeTaskStorage storage)
    {
        _storage = storage;
    }

    public Task AddTaskAsync(AddTaskRequest request, CancellationToken cancellationToken = default) =>
        _storage.AddAsync(request, cancellationToken);
}
