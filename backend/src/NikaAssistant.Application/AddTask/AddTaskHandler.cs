using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

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
