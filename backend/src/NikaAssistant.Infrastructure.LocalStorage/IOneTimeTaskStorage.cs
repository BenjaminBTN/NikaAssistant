using NikaAssistant.Contracts;

namespace NikaAssistant.Infrastructure.LocalStorage;

public interface IOneTimeTaskStorage
{
    Task AddAsync(AddTaskRequest request, CancellationToken cancellationToken = default);

    string ResolveAssignee(string? assignee);

    Task DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default);

    Task UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OneTimeTask>> GetAllAsync(CancellationToken cancellationToken = default);
}
