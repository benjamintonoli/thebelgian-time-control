using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Interfaces;

public interface IPayrollStandbyGpsSource
{
    Task<StandbyGpsBatchResult> ReadStandbyGpsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<(string ResourceId, string DisplayName, DateOnly Date)> standbyResourceDates,
        CancellationToken cancellationToken = default);
}
