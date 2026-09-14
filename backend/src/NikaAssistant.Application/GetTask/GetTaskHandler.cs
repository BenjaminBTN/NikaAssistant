using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.GetTask;

public sealed class GetTaskHandler
{
    private readonly IOneTimeTaskStorage _storage;
    private readonly IMonthlyTaskStorage _monthly;
    private readonly Monthly.MonthlyRolloverService _rollover;

    public GetTaskHandler(
        IOneTimeTaskStorage storage,
        IMonthlyTaskStorage monthly,
        Monthly.MonthlyRolloverService rollover)
    {
        _storage = storage;
        _monthly = monthly;
        _rollover = rollover;
    }

    public async Task<IReadOnlyList<OneTimeTask>> GetTasksAll(CancellationToken cancellationToken = default)
    {
        await _rollover.RolloverDueAsync(cancellationToken);
        return await _storage.GetAllAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OneTimeTask>> GetMonthlyAll(CancellationToken cancellationToken = default)
    {
        await _rollover.RolloverDueAsync(cancellationToken);
        return await _monthly.GetAllAsync(cancellationToken);
    }
}
