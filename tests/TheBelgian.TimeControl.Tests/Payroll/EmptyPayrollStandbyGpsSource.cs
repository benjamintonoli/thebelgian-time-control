using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;

namespace TheBelgian.TimeControl.Tests.Payroll;

internal sealed class EmptyPayrollStandbyGpsSource : IPayrollStandbyGpsSource
{
    public Task<StandbyGpsBatchResult> ReadStandbyGpsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<(string ResourceId, string DisplayName, DateOnly Date)> standbyResourceDates,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new StandbyGpsBatchResult([], 0, 0, 0, 0, 0, "test-empty"));
}
