using NikaAssistant.Application.Abstractions;
using NikaAssistant.Application.Recurring;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.GetTask;

public sealed class GetTaskHandler
{
    private readonly IOneTimeTaskStorage _storage;
    private readonly IRecurringTaskStorage _recurring;
    private readonly RecurringRolloverService _rollover;

    public GetTaskHandler(
        IOneTimeTaskStorage storage,
        IRecurringTaskStorage recurring,
        RecurringRolloverService rollover)
    {
        _storage = storage;
        _recurring = recurring;
        _rollover = rollover;
    }

    public async Task<IReadOnlyList<OneTimeTask>> GetTasksAll(CancellationToken cancellationToken = default)
    {
        await _rollover.RolloverDueAsync(cancellationToken);
        return await _storage.GetAllAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OneTimeTask>> GetRecurringAll(CancellationToken cancellationToken = default)
    {
        await _rollover.RolloverDueAsync(cancellationToken);
        return await _recurring.GetAllAsync(cancellationToken);
    }
}
