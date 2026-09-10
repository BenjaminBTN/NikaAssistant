using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.GetTask;

public sealed class GetTaskHandler
{
    private readonly IOneTimeTaskStorage _storage;

    public GetTaskHandler(IOneTimeTaskStorage storage)
    {
        _storage = storage;
    }

    public Task<IReadOnlyList<OneTimeTask>> GetTasksAll(CancellationToken cancellationToken = default) =>
        _storage.GetAllAsync(cancellationToken);
}
