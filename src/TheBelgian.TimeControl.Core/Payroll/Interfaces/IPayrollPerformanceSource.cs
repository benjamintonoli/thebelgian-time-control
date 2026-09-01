using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Interfaces;

public interface IPayrollPerformanceSource
{
    Task<IReadOnlyList<NormalizedPerformanceEntry>> ReadPerformancesAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default);
}
