using NikaAssistant.Application.Abstractions;
using NikaAssistant.Contracts;

namespace NikaAssistant.Application.GetTask;

public sealed class GetTaskHandler
{
    private readonly IOneTimeTaskStorage _storage;
    private readonly IMonthlyTaskStorage _monthly;
    private readonly IYearlyTaskStorage _yearly;
    private readonly Monthly.MonthlyRolloverService _rollover;
    private readonly Yearly.YearlyRolloverService _yearlyRollover;

    public GetTaskHandler(
        IOneTimeTaskStorage storage,
        IMonthlyTaskStorage monthly,
        IYearlyTaskStorage yearly,
        Monthly.MonthlyRolloverService rollover,
        Yearly.YearlyRolloverService yearlyRollover)
    {
        _storage = storage;
        _monthly = monthly;
        _yearly = yearly;
        _rollover = rollover;
        _yearlyRollover = yearlyRollover;
    }

    public async Task<IReadOnlyList<OneTimeTask>> GetTasksAll(CancellationToken cancellationToken = default)
    {
        await _rollover.RolloverDueAsync(cancellationToken);
        await _yearlyRollover.RolloverDueAsync(cancellationToken);
        return await _storage.GetAllAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OneTimeTask>> GetMonthlyAll(CancellationToken cancellationToken = default)
    {
        await _rollover.RolloverDueAsync(cancellationToken);
        return await _monthly.GetAllAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OneTimeTask>> GetYearlyAll(CancellationToken cancellationToken = default)
    {
        await _yearlyRollover.RolloverDueAsync(cancellationToken);
        return await _yearly.GetAllAsync(cancellationToken);
    }
}
