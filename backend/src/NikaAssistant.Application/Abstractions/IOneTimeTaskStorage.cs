using NikaAssistant.Contracts;

namespace NikaAssistant.Application.Abstractions;

public interface IOneTimeTaskStorage
{
    Task AddAsync(AddTaskRequest request, CancellationToken cancellationToken = default);

    string ResolveAssignee(string? assignee);

    Task DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default);

    Task<int> ArchiveCompletedAsync(CancellationToken cancellationToken = default);

    Task UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OneTimeTask>> GetAllAsync(CancellationToken cancellationToken = default);
}
